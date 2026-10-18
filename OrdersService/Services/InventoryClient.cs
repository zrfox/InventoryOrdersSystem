using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OrdersService.Models;

namespace OrdersService.Services;

public sealed class InventoryClient : IInventoryClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<InventoryClient> _logger;

    public InventoryClient(HttpClient http, ILogger<InventoryClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<StockAdjustmentOutcome> AdjustStockAsync(int productId, int quantityChange, string referenceKey, CancellationToken ct = default)
    {
        var request = new StockAdjustmentRequestDto(productId, quantityChange, referenceKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("api/stock-adjustments", request, Json, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("InventoryService unreachable while applying {Key}: {Message}", referenceKey, ex.Message);
            return new StockAdjustmentOutcome(StockAdjustmentStatus.Unavailable, Message: "InventoryService is unreachable.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("InventoryService timed out while applying {Key}.", referenceKey);
            return new StockAdjustmentOutcome(StockAdjustmentStatus.Unavailable, Message: "InventoryService timed out.");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                {
                    var body = await ReadAsync<StockAdjustmentResponseDto>(response, ct);
                    if (body is null)
                        return new StockAdjustmentOutcome(StockAdjustmentStatus.UnexpectedError, Message: "InventoryService returned an unreadable response.");
                    return new StockAdjustmentOutcome(
                        body.AlreadyApplied ? StockAdjustmentStatus.AlreadyApplied : StockAdjustmentStatus.Applied,
                        body.StockQuantity);
                }
                case HttpStatusCode.Conflict:
                {
                    var error = await ReadAsync<InventoryErrorDto>(response, ct);
                    return new StockAdjustmentOutcome(StockAdjustmentStatus.InsufficientStock, error?.StockQuantity, error?.Error ?? "Insufficient stock.");
                }
                case HttpStatusCode.NotFound:
                    return new StockAdjustmentOutcome(StockAdjustmentStatus.ProductNotFound, Message: $"Product {productId} not found in inventory.");
                case HttpStatusCode.Unauthorized:
                    return new StockAdjustmentOutcome(StockAdjustmentStatus.Unauthorized, Message: "Failed to authenticate with InventoryService (check InventoryApiKey).");
                case HttpStatusCode.BadGateway:
                case HttpStatusCode.ServiceUnavailable:
                case HttpStatusCode.GatewayTimeout:
                    return new StockAdjustmentOutcome(StockAdjustmentStatus.Unavailable, Message: $"InventoryService unavailable (HTTP {(int)response.StatusCode}).");
                default:
                    return new StockAdjustmentOutcome(StockAdjustmentStatus.UnexpectedError, Message: $"InventoryService returned HTTP {(int)response.StatusCode}.");
            }
        }
    }

    public async Task<AdjustmentLookup> GetAdjustmentStateAsync(string referenceKey, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"api/stock-adjustments/{Uri.EscapeDataString(referenceKey)}", ct);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => AdjustmentLookup.Applied,
                HttpStatusCode.NotFound => AdjustmentLookup.NotApplied,
                _ => AdjustmentLookup.Unavailable
            };
        }
        catch (HttpRequestException)
        {
            return AdjustmentLookup.Unavailable;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return AdjustmentLookup.Unavailable;
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
