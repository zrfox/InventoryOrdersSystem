namespace OrdersService.Services;

// Bound from the optional "Cancellation" config section.
public class CancellationOptions
{
    public int MaxPending { get; set; } = 5;
    public double RetryIntervalSeconds { get; set; } = 5;
    public double InventoryTimeoutSeconds { get; set; } = 10;
}
