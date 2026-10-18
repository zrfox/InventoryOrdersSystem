namespace OrdersService.Models;

public static class OrderStatus
{
    public const string Pending = "Pending";
    public const string Confirmed = "Confirmed";
    public const string Rejected = "Rejected";
    public const string CancellationPending = "CancellationPending";
    public const string Cancelled = "Cancelled";
}
