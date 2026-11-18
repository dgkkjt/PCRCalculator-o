using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#if ENABLE_THREADING
internal class ConcurrentCache<TKey, TValue>
{
    [ThreadStatic]
    private static Dictionary<TKey, TValue> threadLocalCache;

    private static Dictionary<TKey, TValue> globalCache = new();
    private static readonly object cacheLock = new();
    private static long generation;
    [ThreadStatic] private static long localGeneration;

    private static void EnsureLocalCache()
    {
        var current = System.Threading.Volatile.Read(ref generation);
        if (threadLocalCache == null || localGeneration != current)
        {
            threadLocalCache = new();
            localGeneration = current;
        }
    }

    // Server calls this only after all resource-owning worker threads have exited.
    public void Clear()
    {
        lock (cacheLock)
        {
            globalCache = new();
            System.Threading.Interlocked.Increment(ref generation);
            threadLocalCache = null;
        }
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        EnsureLocalCache();
        if (threadLocalCache.TryGetValue(key, out value)) return true;

        lock (cacheLock)
        {
            EnsureLocalCache();
            if (globalCache.TryGetValue(key, out value))
            {
                threadLocalCache[key] = value;
                return true;
            }
        }

        return false;
    }

    public TValue this[TKey key]
    {
        set => Add(key, value);
    }

    public void Add(TKey key, TValue value)
    {
        lock (cacheLock)
        {
            EnsureLocalCache();
            threadLocalCache.Add(key, value);
            globalCache.TryAdd(key, value);
        }
    }
}
#else

internal class ConcurrentCache<TKey, TValue> : Dictionary<TKey, TValue> {}

#endif
