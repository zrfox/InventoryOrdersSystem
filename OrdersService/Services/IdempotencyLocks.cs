namespace OrdersService.Services;

// One lock per Idempotency-Key, so two simultaneous submissions of the same order are
// handled one after the other: the second finds the order the first created.
public sealed class IdempotencyLocks
{
    // Case-insensitive to match SQL Server's default collation on the key column.
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out var existing))
            {
                existing = new Entry();
                _entries[key] = existing;
            }
            existing.Users++;
            entry = existing;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Forget(key, entry);
            throw;
        }

        return new Lease(this, key, entry);
    }

    private void Forget(string key, Entry entry)
    {
        lock (_entries)
        {
            if (--entry.Users == 0) _entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    private sealed class Lease : IDisposable
    {
        private readonly IdempotencyLocks _owner;
        private readonly string _key;
        private readonly Entry _entry;
        private int _disposed;

        public Lease(IdempotencyLocks owner, string key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _entry.Semaphore.Release();
            _owner.Forget(_key, _entry);
        }
    }
}
