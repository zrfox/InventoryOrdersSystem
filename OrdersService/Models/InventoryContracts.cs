namespace OrdersService.Models;

// Wire contract of InventoryService's /api/stock-adjustments endpoint.
// Microservice rule: OrdersService never references InventoryService code,
// it only mirrors the JSON shape it depends on.

public sealed record StockAdjustmentRequestDto(int ProductId, int QuantityChange, string ReferenceKey);

public sealed class StockAdjustmentResponseDto
{
    public int ProductId { get; set; }
    public int StockQuantity { get; set; }
    public bool AlreadyApplied { get; set; }
    public string ReferenceKey { get; set; } = string.Empty;
}

public sealed class InventoryErrorDto
{
    public string? Error { get; set; }
    public int? StockQuantity { get; set; }
}
