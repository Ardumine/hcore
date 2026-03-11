using System.Collections.Concurrent;

namespace HCore.Main.Extras;

public class ObjectPool<T> where T : new()
{
    private static readonly ConcurrentQueue<T?> Pool = new();

    public T GetObject()
    {
        lock (Pool)
            if (Pool.TryDequeue(out var obj))
                return obj!;

        return new T();
    }

    public void Return(T obj)
    {
        lock (Pool)
            Pool.Enqueue(obj);
    }
}