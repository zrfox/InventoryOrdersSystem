namespace OrdersService.Services;

public sealed class CancellationRetryOptions
{
    public const string SectionName = "CancellationRetry";

    public bool Enabled { get; set; } = true;
    public int InitialDelaySeconds { get; set; } = 3;
    public int IntervalSeconds { get; set; } = 15;
    public int MaxPending { get; set; } = 5;
}
