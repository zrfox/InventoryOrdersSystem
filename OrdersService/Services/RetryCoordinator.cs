namespace OrdersService.Services;

// Prevents two retry runs (worker + manual trigger) from overlapping.
public sealed class RetryCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool TryEnter() => _gate.Wait(0);
    public void Exit() => _gate.Release();
}
