using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrdersService.Controllers;
using OrdersService.Data;
using OrdersService.Models;
using OrdersService.Services;

namespace OrdersService.Tests;

internal static class TestSupport
{
    // Pass the same dbName to get a second context over the same in-memory database
    // (used to simulate a second request or a service restart).
    public static OrdersDbContext NewContext(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options;
        return new OrdersDbContext(options);
    }

    public static OrderCancellationService CreateCancellationService(
        OrdersDbContext context,
        IHttpClientFactory httpClientFactory,
        CancellationGate? gate = null,
        CancellationOptions? options = null)
    {
        return new OrderCancellationService(
            context,
            httpClientFactory,
            gate ?? new CancellationGate(),
            Microsoft.Extensions.Options.Options.Create(options ?? new CancellationOptions()),
            NullLogger<OrderCancellationService>.Instance);
    }

    public static OrdersController CreateController(
        OrdersDbContext context,
        IHttpClientFactory httpClientFactory,
        CancellationGate? gate = null,
        CancellationOptions? options = null,
        IdempotencyLocks? idempotencyLocks = null,
        ILogger<OrdersController>? logger = null)
    {
        return new OrdersController(
            context,
            httpClientFactory,
            CreateCancellationService(context, httpClientFactory, gate, options),
            idempotencyLocks ?? new IdempotencyLocks(),
            logger ?? NullLogger<OrdersController>.Instance);
    }

    public static async Task<Order> SeedOrderAsync(OrdersDbContext context, string status, int quantity = 2)
    {
        var order = new Order
        {
            ProductId = FakeInventory.ProductId,
            Quantity = quantity,
            DateCreated = DateTime.UtcNow,
            Status = status
        };
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order;
    }
}

internal enum FakeInventoryMode
{
    Up,
    Down,                   // connection refused: the request never arrives
    Hanging,                // times out without doing anything
    AppliesThenLosesReply   // does the work, but the caller never gets the answer
}

// Stands in for InventoryService in unit tests: one product, and the same
// "each operationId is applied at most once" rule as the real service.
internal sealed class FakeInventory : HttpMessageHandler, IHttpClientFactory
{
    public const int ProductId = 1;

    public FakeInventoryMode Mode { get; set; } = FakeInventoryMode.Up;
    public int Stock { get; set; }
    public int RestoreRequests { get; private set; }
    public HashSet<Guid> AppliedOperations { get; } = new();

    public HttpClient CreateClient(string name)
    {
        return new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("http://fake-inventory-service/") };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (Mode == FakeInventoryMode.Down)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (simulated).");
        }
        if (Mode == FakeInventoryMode.Hanging)
        {
            throw new TaskCanceledException("Simulated timeout.");
        }

        var path = request.RequestUri!.AbsolutePath;

        if (request.Method == HttpMethod.Get && path == $"/api/products/{ProductId}")
        {
            return Json(HttpStatusCode.OK, CurrentProduct());
        }

        if (request.Method == HttpMethod.Post && path == $"/api/products/{ProductId}/stock/deduct")
        {
            var body = await request.Content!.ReadFromJsonAsync<DeductStockRequest>(cancellationToken: cancellationToken);
            if (body!.Quantity > Stock)
            {
                return Json(HttpStatusCode.Conflict, new { code = "insufficient_stock" });
            }
            Stock -= body.Quantity;
            return Json(HttpStatusCode.OK, CurrentProduct());
        }

        if (request.Method == HttpMethod.Post && path == $"/api/products/{ProductId}/stock/restore")
        {
            RestoreRequests++;
            var body = await request.Content!.ReadFromJsonAsync<RestoreStockRequest>(cancellationToken: cancellationToken);
            var applied = AppliedOperations.Add(body!.OperationId);
            if (applied) Stock += body.Quantity;

            if (Mode == FakeInventoryMode.AppliesThenLosesReply)
            {
                throw new TaskCanceledException("Simulated lost response.");
            }
            return Json(HttpStatusCode.OK, new { applied, stockQuantity = Stock });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private ProductDto CurrentProduct()
    {
        return new ProductDto { Id = ProductId, Name = "Widget", StockQuantity = Stock, Price = 9.99m };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body)
    {
        return new HttpResponseMessage(status) { Content = JsonContent.Create(body) };
    }
}

// Captures log output so tests can check what would appear in the terminal.
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
