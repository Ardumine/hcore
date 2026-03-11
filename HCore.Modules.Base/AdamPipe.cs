namespace HCore.Modules.Base;

/// <summary>
/// Interconnect the module and HCore
/// </summary>
/// <typeparam name="T"></typeparam>
public class AdamPipe<T>
{
    private readonly SemaphoreSlim _semaphore = new(0);
    private readonly Queue<T> _queue = new();
    private readonly Lock _lock = new();

    public void SendSignal(T item)
    {
        lock (_lock)
        {
            _queue.Enqueue(item);
        }
        _semaphore.Release();
    }

    public T Wait(CancellationToken ct = default)
    {
        _semaphore.Wait(ct);
        
        lock (_lock)
        {
            return _queue.Dequeue();
        }
    }
}
