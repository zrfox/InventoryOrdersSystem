using System.ComponentModel.DataAnnotations;

namespace InventoryService.Models;

public class DeductStockRequest
{
    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }
}

public class RestoreStockRequest
{
    // Chosen by the caller; sending the same id again is a no-op.
    public Guid OperationId { get; set; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }
}

public class RestoreStockResult
{
    // true = stock was added by this call, false = this operation had already been applied.
    public bool Applied { get; set; }
    public int StockQuantity { get; set; }
}

// "Set the stock to StockQuantity, but only if it is still ExpectedStockQuantity
// (the value I was shown)". Anything else is a collision and is refused.
public class SetStockRequest
{
    [Range(0, int.MaxValue)]
    public int ExpectedStockQuantity { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "Stock quantity cannot be negative.")]
    public int StockQuantity { get; set; }
}

public class StockError
{
    public const string ProductNotFound = "product_not_found";
    public const string InsufficientStock = "insufficient_stock";
    public const string InvalidOperationId = "invalid_operation_id";
    public const string StockChanged = "stock_changed";
    public const string Busy = "busy";

    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int? StockQuantity { get; set; }
}