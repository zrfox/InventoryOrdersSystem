extern alias InventoryApp;
extern alias OrdersApp;
using InventoryProgram = InventoryApp::Program;
using OrdersProgram = OrdersApp::Program;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InventoryService.Data;
using InventoryService.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrdersService.Data;
using OrdersService.Models;
using OrdersService.Services;

namespace Integration.Tests;

// Flip IsUp to simulate InventoryService stopping and starting, as seen from OrdersService.
internal sealed class InventorySwitch
{
    private volatile bool _isUp = true;

    public bool IsUp
    {
        get => _isUp;
        set => _isUp = value;
    }
}

// Sits between OrdersService and the in-memory InventoryService. When the switch is off it
// fails exactly like a real "connection refused", before anything reaches Inventory.
internal sealed class SwitchableInventoryHandler : DelegatingHandler
{
    private readonly InventorySwitch _switch;

    public SwitchableInventoryHandler(InventorySwitch inventorySwitch, HttpMessageHandler inner) : base(inner)
    {
        _switch = inventorySwitch;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_switch.IsUp)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError,
                "Connection refused (simulated: InventoryService is down).");
        }
        return await base.SendAsync(request, cancellationToken);
    }
}

// Both services running in memory, wired together like in the existing integration tests,
// plus: an on/off switch for Inventory and the ability to stop/start OrdersService
// while keeping its database.
internal sealed class TestSystem : IAsyncDisposable
{
    public const string ApiKey = "test-key-123";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // An explicit root makes the in-memory databases outlive an individual host (needed for the restart test).
    private readonly InMemoryDatabaseRoot _databaseRoot = new();
    private readonly string _inventoryDbName = Guid.NewGuid().ToString();
    private readonly string _ordersDbName = Guid.NewGuid().ToString();

    private WebApplicationFactory<OrdersProgram>? _ordersFactory;
    private HttpClient? _ordersClient;

    public InventorySwitch Inventory { get; } = new();
    public WebApplicationFactory<InventoryProgram> InventoryFactory { get; }

    // Talks to InventoryService directly; not affected by the switch.
    public HttpClient InventoryClient { get; }

    public HttpClient OrdersClient => _ordersClient ?? throw new InvalidOperationException("OrdersService is stopped.");

    public TestSystem()
    {
        InventoryFactory = new WebApplicationFactory<InventoryProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?> { ["InventoryApiKey"] = ApiKey });
                });
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<InventoryDbContext>));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddDbContext<InventoryDbContext>(options =>
                        options.UseInMemoryDatabase(_inventoryDbName, _databaseRoot));
                });
            });

        InventoryClient = InventoryFactory.CreateClient();
        InventoryClient.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        StartOrdersService();
    }

    public void StartOrdersService()
    {
        _ordersFactory = new WebApplicationFactory<OrdersProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?> { ["InventoryApiKey"] = ApiKey });
                });
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<OrdersDbContext>));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddDbContext<OrdersDbContext>(options =>
                        options.UseInMemoryDatabase(_ordersDbName, _databaseRoot));

                    // Retry quickly so tests don't have to wait 5 seconds per pass.
                    services.Configure<CancellationOptions>(options => options.RetryIntervalSeconds = 0.1);

                    services.AddHttpClient("InventoryService", client =>
                        {
                            client.BaseAddress = new Uri("http://localhost/");
                            client.DefaultRequestHeaders.Remove("X-Api-Key");
                            client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
                        })
                        .ConfigurePrimaryHttpMessageHandler(() =>
                            new SwitchableInventoryHandler(Inventory, InventoryFactory.Server.CreateHandler()));
                });
            });

        // Creating the client starts the host, and with it the retry worker.
        _ordersClient = _ordersFactory.CreateClient();
        _ordersClient.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
    }

    public async Task StopOrdersServiceAsync()
    {
        _ordersClient?.Dispose();
        _ordersClient = null;
        if (_ordersFactory != null)
        {
            await _ordersFactory.DisposeAsync();
            _ordersFactory = null;
        }
    }

    public async Task<Product> SeedProductAsync(int stock)
    {
        using var scope = InventoryFactory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var product = new Product { Name = "Widget", StockQuantity = stock, Price = 9.99m };
        context.Products.Add(product);
        await context.SaveChangesAsync();
        return product;
    }

    public async Task<int> GetStockAsync(int productId)
    {
        var product = await InventoryClient.GetFromJsonAsync<Product>($"/api/products/{productId}", JsonOptions);
        return product!.StockQuantity;
    }

    public async Task<HttpResponseMessage> PostOrderAsync(int productId, int quantity, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest { ProductId = productId, Quantity = quantity })
        };
        if (idempotencyKey != null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await OrdersClient.SendAsync(request);
    }

    public async Task<Order> PlaceOrderAsync(int productId, int quantity)
    {
        var response = await PostOrderAsync(productId, quantity);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Order>(JsonOptions))!;
    }

    public async Task<Order> GetOrderAsync(int orderId)
    {
        return (await OrdersClient.GetFromJsonAsync<Order>($"/api/orders/{orderId}", JsonOptions))!;
    }

    public Task<HttpResponseMessage> CancelAsync(int orderId)
    {
        return OrdersClient.PostAsync($"/api/orders/{orderId}/cancel", null);
    }

    public Task<HttpResponseMessage> UndoAsync(int orderId)
    {
        return OrdersClient.PostAsync($"/api/orders/{orderId}/cancel/undo", null);
    }

    // Polls until the background worker has moved the order to the expected status.
    public async Task<Order> WaitForStatusAsync(int orderId, string status, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        Order order;
        do
        {
            order = await GetOrderAsync(orderId);
            if (order.Status == status) return order;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);

        throw new Xunit.Sdk.XunitException(
            $"Order {orderId} did not reach status '{status}' within {timeoutSeconds}s (last status: '{order.Status}').");
    }

    public async ValueTask DisposeAsync()
    {
        await StopOrdersServiceAsync();
        InventoryClient.Dispose();
        await InventoryFactory.DisposeAsync();
    }
}
