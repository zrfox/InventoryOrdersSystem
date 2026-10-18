namespace OrdersService.Services;

// Serialises every cancellation state change (cancel, undo, background retry) inside this
// process. That is what stops a double cancel, makes the "max 5 pending" count reliable,
// and keeps an undo from interleaving with a retry that is talking to InventoryService.
public sealed class CancellationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        return new Lease(_semaphore);
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Lease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
