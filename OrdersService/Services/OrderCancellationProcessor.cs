using OrdersService.Models;

namespace OrdersService.Services;

public enum CancellationAttemptKind { Completed, InventoryUnavailable, Failed }

public sealed record CancellationAttempt(CancellationAttemptKind Kind, string Message, bool StockRestored);

// The single place that knows how to finish a cancellation: restore stock in
// Inventory (idempotently) and flip the order to Cancelled. Used by the
// controller (immediate cancel) and by the retry worker (pending cancels).
public sealed class OrderCancellationProcessor
{
    private readonly IInventoryClient _inventory;
    private readonly ILogger<OrderCancellationProcessor> _logger;

    public OrderCancellationProcessor(IInventoryClient inventory, ILogger<OrderCancellationProcessor> logger)
    {
        _inventory = inventory;
        _logger = logger;
    }

    // Deterministic per order: however many times we retry, Inventory sees the
    // same key and only restores the stock once.
    public static string CancellationReferenceKey(int orderId) => $"order-{orderId}-cancel";

    public async Task<CancellationAttempt> TryCompleteCancellationAsync(Order order, CancellationToken ct = default)
    {
        if (order.Status != OrderStatus.Confirmed && order.Status != OrderStatus.CancellationPending)
            throw new InvalidOperationException($"Order {order.Id} is '{order.Status}' and cannot be cancelled.");

        var key = CancellationReferenceKey(order.Id);
        var outcome = await _inventory.AdjustStockAsync(order.ProductId, +order.Quantity, key, ct);

        switch (outcome.Status)
        {
            case StockAdjustmentStatus.Applied:
                MarkCancelled(order);
                _logger.LogInformation("Order {OrderId} cancelled; {Quantity} unit(s) of product {ProductId} returned to inventory (stock now {Stock}).",
                    order.Id, order.Quantity, order.ProductId, outcome.StockQuantity);
                return new CancellationAttempt(CancellationAttemptKind.Completed,
                    $"Order {order.Id} cancelled; {order.Quantity} unit(s) returned to inventory.", StockRestored: true);

            case StockAdjustmentStatus.AlreadyApplied:
                MarkCancelled(order);
                _logger.LogInformation("Order {OrderId} cancelled; inventory had already restored its stock earlier, so nothing was restored twice.", order.Id);
                return new CancellationAttempt(CancellationAttemptKind.Completed,
                    $"Order {order.Id} cancelled (stock had already been restored).", StockRestored: false);

            case StockAdjustmentStatus.ProductNotFound:
                MarkCancelled(order);
                _logger.LogWarning("Order {OrderId} cancelled, but product {ProductId} no longer exists in inventory; no stock restored.", order.Id, order.ProductId);
                return new CancellationAttempt(CancellationAttemptKind.Completed,
                    $"Order {order.Id} cancelled; product no longer exists so no stock was restored.", StockRestored: false);

            case StockAdjustmentStatus.Unavailable:
                return new CancellationAttempt(CancellationAttemptKind.InventoryUnavailable,
                    outcome.Message ?? "InventoryService is unavailable.", StockRestored: false);

            default:
                return new CancellationAttempt(CancellationAttemptKind.Failed,
                    outcome.Message ?? "InventoryService returned an unexpected error.", StockRestored: false);
        }
    }

    private static void MarkCancelled(Order order)
    {
        order.Status = OrderStatus.Cancelled;
        order.DateUpdated = DateTime.UtcNow;
    }
}
