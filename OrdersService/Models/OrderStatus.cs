namespace OrdersService.Models;

public static class OrderStatus
{
    public const string Pending = "Pending";
    public const string Confirmed = "Confirmed";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
    // Cancel was requested while InventoryService was unreachable. Stock has NOT been
    // restored yet; the retry worker keeps trying until it succeeds.
    public const string CancellationPending = "CancellationPending";
}
