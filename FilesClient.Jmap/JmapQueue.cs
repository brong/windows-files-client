namespace FilesClient.Jmap;

public enum QueuePriority { Background, Interactive }

public class JmapQueue : IDisposable
{
    private readonly SemaphoreSlim _backgroundSemaphore = new(4, 4);
    private readonly SemaphoreSlim _interactiveSemaphore = new(4, 4);
    private int _backgroundActive;
    private int _backgroundPending;
    private int _interactiveActive;
    private int _interactivePending;

    public int BackgroundActive => _backgroundActive;
    public int BackgroundPending => _backgroundPending;
    public int InteractiveActive => _interactiveActive;
    public int InteractivePending => _interactivePending;

    public async Task<T> EnqueueAsync<T>(QueuePriority priority, Func<Task<T>> work, CancellationToken ct = default)
    {
        SemaphoreSlim semaphore;
        if (priority == QueuePriority.Interactive)
        {
            semaphore = _interactiveSemaphore;
            Interlocked.Increment(ref _interactivePending);
            try { await semaphore.WaitAsync(ct); }
            catch { Interlocked.Decrement(ref _interactivePending); throw; }
            Interlocked.Decrement(ref _interactivePending);
            Interlocked.Increment(ref _interactiveActive);
        }
        else
        {
            semaphore = _backgroundSemaphore;
            Interlocked.Increment(ref _backgroundPending);
            try { await semaphore.WaitAsync(ct); }
            catch { Interlocked.Decrement(ref _backgroundPending); throw; }
            Interlocked.Decrement(ref _backgroundPending);
            Interlocked.Increment(ref _backgroundActive);
        }

        try
        {
            return await work();
        }
        finally
        {
            if (priority == QueuePriority.Interactive)
                Interlocked.Decrement(ref _interactiveActive);
            else
                Interlocked.Decrement(ref _backgroundActive);
            semaphore.Release();
        }
    }

    public async Task EnqueueAsync(QueuePriority priority, Func<Task> work, CancellationToken ct = default)
    {
        await EnqueueAsync<object?>(priority, async () => { await work(); return null; }, ct);
    }

    public void Dispose()
    {
        _backgroundSemaphore.Dispose();
        _interactiveSemaphore.Dispose();
    }
}
