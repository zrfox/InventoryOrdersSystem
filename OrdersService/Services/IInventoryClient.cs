namespace OrdersService.Services;

public enum StockAdjustmentStatus
{
    Applied,
    AlreadyApplied,      // key seen before; inventory did NOT change again
    InsufficientStock,
    ProductNotFound,
    Unauthorized,
    Unavailable,         // connection refused / DNS failure / timeout / 5xx
    UnexpectedError
}

public sealed record StockAdjustmentOutcome(StockAdjustmentStatus Status, int? StockQuantity = null, string? Message = null)
{
    public bool Succeeded => Status is StockAdjustmentStatus.Applied or StockAdjustmentStatus.AlreadyApplied;
}

public enum AdjustmentLookup { Applied, NotApplied, Unavailable }

public interface IInventoryClient
{
    /// <summary>Atomically change stock by <paramref name="quantityChange"/> (negative = reserve, positive = restore).</summary>
    Task<StockAdjustmentOutcome> AdjustStockAsync(int productId, int quantityChange, string referenceKey, CancellationToken ct = default);

    /// <summary>Has an adjustment with this reference key already been applied?</summary>
    Task<AdjustmentLookup> GetAdjustmentStateAsync(string referenceKey, CancellationToken ct = default);
}
