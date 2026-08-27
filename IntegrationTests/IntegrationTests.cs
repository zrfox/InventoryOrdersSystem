extern alias InventoryApp;
extern alias OrdersApp;
using InventoryProgram = InventoryApp::Program;
using OrdersProgram = OrdersApp::Program;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc;

using Microsoft.EntityFrameworkCore;
using OrdersService;
using InventoryService;
using System.Text.Json;
using InventoryService.Data;
using InventoryService.Models;

using OrdersService.Data;
using OrdersService.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Xml.Serialization;
using Microsoft.Extensions.Configuration;

using Microsoft.AspNetCore.Hosting;
using OrdersService.Controllers;
using System.Linq.Expressions;
using System.Net.Http.Json;
using System.Net;

namespace Integration.Tests;

public class IntegrationTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /* Passes and decrements Inventory on order creation.
     * Test appropriately fails when order exceeds quantity in stock.
     */
    [Fact]
    public async Task CreateProductAndOrder_ConfirmInventoryDecrementAndOrder()
    {
        // *start with Ids 0 because SQL will increment on its own
        var product = new Product { Id = 0, Name = "Widget", StockQuantity = 3, Price = 9.99m };
        var request = new CreateOrderRequest { ProductId = 1, Quantity = 2 };

        var inventoryDbName = Guid.NewGuid().ToString();
        var ordersDbName = Guid.NewGuid().ToString();

        const string testApiKey = "test-key-123";

        // gives the entry point for Program.cs in InventoryService and OrdersService
        // using aliases of the .csproj file references InventoryProgram and OrdersProgram
        // WebApplicationFactory is a test harness that runs InventoryService/Program.cs
        // but hosts it in-memory instead of on a real network port
        // InventoryProgram is the entry point ( <TEntryPoint> ) 
        var inventoryFactory = new WebApplicationFactory<InventoryProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["InventoryApiKey"] = "test-key-123"
                    });
                });

                // builder in the lambda here is a hook that lets you edit/override builder in Program.cs
                builder.ConfigureServices(services =>
                {

                    // find DbContext to remove if not null
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<InventoryDbContext>));
                    if (descriptor != null) services.Remove(descriptor);

                    // replace with InMemory database
                    services.AddDbContext<InventoryDbContext>(options =>
                        options.UseInMemoryDatabase(inventoryDbName));

                });
            });

        // ordersFactory runs OrdersService/Program.cs
        var ordersFactory = new WebApplicationFactory<OrdersProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["InventoryApiKey"] = testApiKey
                    });
                });

                builder.ConfigureServices(services =>
                {
                    // find if a current db context exists and remove it
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<OrdersDbContext>));
                    if (descriptor != null) services.Remove(descriptor);

                    // switch to InMemory database
                    services.AddDbContext<OrdersDbContext>(options =>
                        options.UseInMemoryDatabase(ordersDbName));
                    
                    // remove real "InventoryService" HttpClient registration
                    // but removes the config in Program.cs, which means base address
                    // and the X-Api-Key header are gone,so we have to add that back in here
                    services.AddHttpClient("InventoryService", client =>
                    {
                        // using localhost for uri will bypass real networking
                        client.BaseAddress = new Uri("http://localhost/");
                        client.DefaultRequestHeaders.Remove("X-Api-Key");   // clear the one from Program.cs
                        client.DefaultRequestHeaders.Add("X-Api-Key", testApiKey);
                    })
                        .ConfigurePrimaryHttpMessageHandler(() => inventoryFactory.Server.CreateHandler());
                });
            });
        // using here means call .Dispose() when this variable goes out of scope. 
        // scope will disappear after this block
        // need to get rid of contexts after done with them, product added still
        // retained by in-memory db. 
        // make scope, get InventoryDbContext in the scope?, add product, wait for changes to sync
        using (var seedScope = inventoryFactory.Services.CreateScope())
        {
            var seedContext = seedScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            // EF adds product and increments id
            seedContext.Products.Add(product);
            await seedContext.SaveChangesAsync();
        }

        var ordersClient = ordersFactory.CreateClient();
        ordersClient.DefaultRequestHeaders.Add("X-Api-Key", testApiKey);
        // PostAsJsonAsync is an extension method. It's definition in the docs are 
        // a static definition which shows what the compiler will makle it into
        // with first parameter as "this HttpClient"
        var response = await ordersClient.PostAsJsonAsync("/api/orders", request);
        //  ensuresuccess throws on 4xx and 5xx errors with a message
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        // deserializes response body. deserializes here = take json and make it into an Order again
        var createdOrder = await response.Content.ReadFromJsonAsync<Order>(_jsonOptions);

        // --- assert: order came back correct ---
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(createdOrder);
        Assert.Equal(product.Id, createdOrder.ProductId);
        Assert.Equal(2, createdOrder.Quantity);



        // --- assert: stock actually decremented ---
        var inventoryClient = inventoryFactory.CreateClient();
        inventoryClient.DefaultRequestHeaders.Add("X-Api-Key", testApiKey);

        var updated = await inventoryClient
            .GetFromJsonAsync<Product>($"/api/products/{product.Id}", _jsonOptions);

        Assert.Equal(1, updated!.StockQuantity);  // started at 3, ordered 2
    }

    /* Test correctly passes when the order is correctly rejected for exceeding quantity in stock.
     * Test correctly fails when order quantity does not exceed inventory.
     * Correctly fails when quantity is zero.
     */
    [Fact]
    public async Task CreateProductAndOrder_ConfirmInsufficientStock()
    {
        var product = new Product { Id = 0, Name = "Widget", Price = 9.99m, StockQuantity =  1};

        var InventoryDbName = Guid.NewGuid().ToString();
        var ordersDbName = Guid.NewGuid().ToString();

        const string testApiKey = "test-key-123";

        var inventoryFactory = new WebApplicationFactory<InventoryProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["InventoryApiKey"] = "test-key-123"
                    });
                });

                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<InventoryDbContext>));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddDbContext<InventoryDbContext>(options =>
                    options.UseInMemoryDatabase(InventoryDbName));
                }); 
            });
        /*
        using (var scopeTest = inventoryFactory.Services.CreateScope())
        {
            var config = scopeTest.ServiceProvider.GetRequiredService<IConfiguration>();
            Assert.Fail($"Inventory sees: '{config["InventoryApiKey"]}'");
        }
        */
        var ordersFactory = new WebApplicationFactory<OrdersProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["InventoryApiKey"] = testApiKey
                    });
                });

                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<OrdersDbContext>));
                    if (descriptor != null) services.Remove(descriptor);

                    services.AddDbContext<OrdersDbContext>(options =>
                        options.UseInMemoryDatabase(ordersDbName));

                    // add InventoryService to call within OrdersProgram
                    services.AddHttpClient("InventoryService", client =>
                    {
                        client.BaseAddress = new Uri("http://localhost/");
                        client.DefaultRequestHeaders.Remove("X-Api-Key");   // clear the one from Program.cs
                        client.DefaultRequestHeaders.Add("X-Api-Key", testApiKey);
                    })
                    .ConfigurePrimaryHttpMessageHandler(() => inventoryFactory.Server.CreateHandler());
                });
            });
        // should turn this into function...maybe most of these test for the initialization
        // seed inventory database with product to test Order creation on insufficient stock.
        // seeding is for preconditions to set the stage for out test.
        using (var seedScope = inventoryFactory.Services.CreateScope())
        {
            var seedContext = seedScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            // product is overwritten by EF with the database-generated key
            seedContext.Products.Add(product);
            await seedContext.SaveChangesAsync();
        }

        var request = new CreateOrderRequest { ProductId = product.Id, Quantity = 2 };

        var inventoryClient = inventoryFactory.CreateClient();
        inventoryClient.DefaultRequestHeaders.Add("X-Api-Key", testApiKey);
        var inventoryResponse = await inventoryClient.GetFromJsonAsync<Product>($"/api/products/{product.Id}");
        // not recognized, why? 
        Assert.NotNull(inventoryResponse);
        Assert.Equal(1, product.Id);


        var ordersClient = ordersFactory.CreateClient();
        ordersClient.DefaultRequestHeaders.Add("X-Api-Key", testApiKey);

        var response = await ordersClient.PostAsJsonAsync("/api/orders", request);
        var createdOrder = await response.Content.ReadFromJsonAsync<Order>(_jsonOptions); 
        // idk if i need this to populate HttpStatusCode
        //response.EnsureSuccessStatusCode();
        // check if no order created? check if stock for item the same. 
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(createdOrder);
        Assert.Equal("Rejected", createdOrder.Status);

        // "updated!.StockQuantity" is null forgiving, compiler will ignore. But Assert.NotNull is better. Changed.
        // GetFromJsonAsync(...) throws on errors. Not for testing 404, etc.
        var updated = await inventoryClient.GetFromJsonAsync<Product>($"api/products/{product.Id}");
        Assert.NotNull(updated);
        Assert.Equal(1, updated.StockQuantity);

        // the rejected order WAS persisted
        // persistent order shows everything else worked accordingly
        // as opposed to an entirely missing order, which is equivalent to nothing happening.
        using var scope = ordersFactory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var saved = await ctx.Orders.FindAsync(createdOrder.Id);
        Assert.NotNull(saved);
        Assert.Equal("Rejected", saved.Status);
    }
}

// WebAppplicationFactory exists to RUN actual Program.cs of InventoryService.
// this is the app itself. 
// var inventoryFactory = new WebApplicationFactory<InventoryService.Program>();

// we will use WithWebHostBuilder(builder => builder.ConfigureServices(...))
// to change the builder config in Program.cs after builder phase of Program.cs completes
// but BEFORE app = builder.Build() runs (which would finalize all configs