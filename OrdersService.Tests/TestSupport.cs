using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrdersService.Controllers;
using OrdersService.Data;
using OrdersService.Services;

namespace OrdersService.Tests;

// Records every request and answers via a delegate, so tests can inspect what
// OrdersService sent to InventoryService and simulate outages by throwing.
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    public sealed record RecordedRequest(HttpMethod Method, string Path, string? Body);

    private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<RecordedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.PathAndQuery, body));
        return _responder(request, body);
    }
}

public static class Responses
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static HttpResponseMessage Json200(object body) => JsonResponse(HttpStatusCode.OK, body);

    public static HttpResponseMessage JsonResponse(HttpStatusCode status, object? body = null)
    {
        var response = new HttpResponseMessage(status);
        if (body != null)
            response.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        return response;
    }

    public static HttpResponseMessage Applied(int productId, int stockQuantity, string referenceKey) =>
        Json200(new { productId, stockQuantity, alreadyApplied = false, referenceKey });

    public static HttpResponseMessage AlreadyApplied(int productId, int stockQuantity, string referenceKey) =>
        Json200(new { productId, stockQuantity, alreadyApplied = true, referenceKey });

    public static HttpResponseMessage InsufficientStock(int stockQuantity) =>
        JsonResponse(HttpStatusCode.Conflict, new { error = "Insufficient stock.", stockQuantity });

    public static HttpResponseMessage NotFound() => JsonResponse(HttpStatusCode.NotFound, new { error = "Product not found." });

    /// <summary>Simulates InventoryService not running.</summary>
    public static HttpResponseMessage Down() => throw new HttpRequestException("Connection refused (simulated outage).");
}

public sealed class TestHarness
{
    public OrdersDbContext Context { get; }
    public StubHttpMessageHandler Handler { get; }
    public InventoryClient Inventory { get; }
    public OrderCancellationProcessor Processor { get; }
    public PendingCancellationRetrier Retrier { get; }
    public OrdersController Controller { get; }

    public TestHarness(Func<HttpRequestMessage, string?, HttpResponseMessage> responder, int maxPending = 5)
    {
        Context = new OrdersDbContext(new DbContextOptionsBuilder<OrdersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        Handler = new StubHttpMessageHandler(responder);
        var http = new HttpClient(Handler) { BaseAddress = new Uri("http://fake-inventory-service/") };

        Inventory = new InventoryClient(http, NullLogger<InventoryClient>.Instance);
        Processor = new OrderCancellationProcessor(Inventory, NullLogger<OrderCancellationProcessor>.Instance);
        var options = Options.Create(new CancellationRetryOptions { MaxPending = maxPending });
        Retrier = new PendingCancellationRetrier(Context, Processor, new RetryCoordinator(), NullLogger<PendingCancellationRetrier>.Instance);
        Controller = new OrdersController(Context, Inventory, Processor, Retrier, options, NullLogger<OrdersController>.Instance);
    }

    public static JsonElement ParseBody(string? body) => JsonDocument.Parse(body ?? "{}").RootElement.Clone();
}
