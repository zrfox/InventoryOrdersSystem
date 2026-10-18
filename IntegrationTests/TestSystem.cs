extern alias InventoryApp;
extern alias OrdersApp;
using InventoryProgram = InventoryApp::Program;
using OrdersProgram = OrdersApp::Program;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using Xunit;

namespace Integration.Tests;

/// <summary>Flip <see cref="InventoryDown"/> to simulate InventoryService being stopped.</summary>
internal sealed class OutageSwitch
{
    private int _down;
    public bool InventoryDown
    {
        get => Volatile.Read(ref _down) == 1;
        set => Volatile.Write(ref _down, value ? 1 : 0);
    }
}

internal sealed class OutageHandler : DelegatingHandler
{
    private readonly OutageSwitch _switch;

    public OutageHandler(OutageSwitch @switch, HttpMessageHandler inner) : base(inner) => _switch = @switch;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_switch.InventoryDown)
            throw new HttpRequestException("Simulated InventoryService outage.");
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Boots both services in-memory, wires OrdersService's HttpClient into InventoryService's
/// TestServer, and lets tests simulate outages and OrdersService restarts.
/// </summary>
internal sealed class TestSystem : IAsyncDisposable
{
    public const string ApiKey = "test-key-123";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Roots keep the in-memory databases alive across host restarts.
    private readonly InMemoryDatabaseRoot _inventoryRoot = new();
    private readonly InMemoryDatabaseRoot _ordersRoot = new();
    private readonly string _inventoryDbName = Guid.NewGuid().ToString();
    private readonly string _ordersDbName = Guid.NewGuid().ToString();

    public OutageSwitch Outage { get; } = new();
    public WebApplicationFactory<InventoryProgram> Inventory { get; }
    public WebApplicationFactory<OrdersProgram> Orders { get; private set; }

    public TestSystem(bool retryWorkerEnabled = false, int retryIntervalSeconds = 3600)
    {
        Inventory = new WebApplicationFactory<InventoryProgram>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InventoryApiKey"] = ApiKey
            }));
            builder.ConfigureServices(services =>
            {
                RemoveDbContextOptions<InventoryDbContext>(services);
                services.AddDbContext<InventoryDbContext>(o => o.UseInMemoryDatabase(_inventoryDbName, _inventoryRoot));
            });
        });

        Orders = CreateOrdersFactory(retryWorkerEnabled, retryIntervalSeconds);
    }

    private WebApplicationFactory<OrdersProgram> CreateOrdersFactory(bool retryWorkerEnabled, int retryIntervalSeconds)
    {
        var inventory = Inventory;
        var outage = Outage;

        return new WebApplicationFactory<OrdersProgram>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InventoryApiKey"] = ApiKey,
                ["CancellationRetry:Enabled"] = retryWorkerEnabled ? "true" : "false",
                ["CancellationRetry:InitialDelaySeconds"] = "0",
                ["CancellationRetry:IntervalSeconds"] = retryIntervalSeconds.ToString(),
                ["CancellationRetry:MaxPending"] = "5"
            }));
            builder.ConfigureServices(services =>
            {
                RemoveDbContextOptions<OrdersDbContext>(services);
                services.AddDbContext<OrdersDbContext>(o => o.UseInMemoryDatabase(_ordersDbName, _ordersRoot));

                // Route the "InventoryService" client into Inventory's TestServer, behind the outage switch.
                services.AddHttpClient("InventoryService", client =>
                {
                    client.BaseAddress = new Uri("http://localhost/");
                    client.DefaultRequestHeaders.Remove("X-Api-Key");
                    client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new OutageHandler(outage, inventory.Server.CreateHandler()));
            });
        });
    }

    /// <summary>Simulates stopping OrdersService and starting it again against the same database.</summary>
    public async Task RestartOrdersServiceAsync(bool retryWorkerEnabled = true, int retryIntervalSeconds = 1)
    {
        await Orders.DisposeAsync();
        Orders = CreateOrdersFactory(retryWorkerEnabled, retryIntervalSeconds);
    }

    private static void RemoveDbContextOptions<TContext>(IServiceCollection services) where TContext : DbContext
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<TContext>));
        if (descriptor != null) services.Remove(descriptor);
    }

    public HttpClient InventoryClient()
    {
        var client = Inventory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    public HttpClient OrdersClient()
    {
        var client = Orders.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    public async Task<Product> SeedProductAsync(string name, int stock, decimal price = 9.99m)
    {
        using var scope = Inventory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var product = new Product { Name = name, StockQuantity = stock, Price = price };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    public async Task<int> GetStockAsync(int productId)
    {
        var product = await InventoryClient().GetFromJsonAsync<Product>($"/api/products/{productId}", Json);
        Assert.NotNull(product);
        return product.StockQuantity;
    }

    public async Task<Order> PlaceOrderAsync(int productId, int quantity, string? clientRequestId = null)
    {
        var response = await OrdersClient().PostAsJsonAsync("/api/orders",
            new CreateOrderRequest { ProductId = productId, Quantity = quantity, ClientRequestId = clientRequestId });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Unexpected {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<Order>(body, Json)!;
    }

    public async Task<Order> GetOrderAsync(int orderId)
    {
        var order = await OrdersClient().GetFromJsonAsync<Order>($"/api/orders/{orderId}", Json);
        Assert.NotNull(order);
        return order;
    }

    public async Task<Order> WaitForStatusAsync(int orderId, string status, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Order? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await GetOrderAsync(orderId);
            if (last.Status == status) return last;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Order {orderId} did not reach '{status}' within {timeout}; last status was '{last?.Status}'.");
    }

    public async ValueTask DisposeAsync()
    {
        await Orders.DisposeAsync();
        await Inventory.DisposeAsync();
    }
}
