using System.Collections.Concurrent;

namespace InventoryService.Services;

// Serializes stock adjustments per product inside this process so that
// "read stock -> check -> write stock" can't interleave between two requests.
public sealed class ProductLocks
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(int productId, CancellationToken ct = default)
    {
        var semaphore = _locks.GetOrAdd(productId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        return new Releaser(semaphore);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
