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

public class StockError
{
    public const string ProductNotFound = "product_not_found";
    public const string InsufficientStock = "insufficient_stock";
    public const string InvalidOperationId = "invalid_operation_id";
    public const string Busy = "busy";

    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int? StockQuantity { get; set; }
}
