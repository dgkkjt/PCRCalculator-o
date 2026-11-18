using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PCRCalculatorServer;

internal readonly record struct IdleWorkerPoolSnapshot(string State, int Queued, int Active,
    int Workers, long Generation, int ReleaseCount);

/// <summary>Owns the admission/idle boundary. Execution includes the result callback.</summary>
internal sealed class IdleWorkerPool<T> : IDisposable
{
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private readonly object gate = new();
    private readonly Queue<T> queue = new();
    private readonly List<Thread> workers = new();
    private readonly int workerCount;
    private readonly Action<int> initialize;
    private readonly Action<T> execute;
    private readonly Action<T, Exception> failed;
    private readonly Action release;
    private readonly TimeProvider clock;
    private readonly TimeSpan idleTimeout;
    private readonly ITimer timer;
    private long generation;
    private long lastCompletion;
    private int active;
    private int releaseCount;
    private bool dirty;
    private bool releasing;
    private bool disposed;
    private Exception fault;

    public IdleWorkerPool(int workerCount, TimeSpan idleTimeout, Action<int> initialize,
        Action<T> execute, Action<T, Exception> failed, Action release, TimeProvider clock = null)
    {
        if (workerCount < 1) throw new ArgumentOutOfRangeException(nameof(workerCount));
        if (idleTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        this.workerCount = workerCount;
        this.idleTimeout = idleTimeout;
        this.initialize = initialize;
        this.execute = execute;
        this.failed = failed;
        this.release = release;
        this.clock = clock ?? TimeProvider.System;
        timer = this.clock.CreateTimer(OnIdle, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (gate) StartWorkers();
    }

    public IdleWorkerPoolSnapshot Snapshot
    {
        get
        {
            lock (gate) return new(disposed ? "Stopped" : fault != null ? "Faulted" :
                releasing ? "Releasing" : workers.Count == 0 ? "Dormant" : "Running",
                queue.Count, active, workers.Count, generation, releaseCount);
        }
    }

    public void Dispatch(T item)
    {
        Exception error;
        lock (gate)
        {
            error = disposed ? new ObjectDisposedException(nameof(IdleWorkerPool<T>)) : fault;
            if (error == null)
            {
                queue.Enqueue(item);
                dirty = true;
                timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                if (!releasing && workers.Count == 0) StartWorkers();
                Monitor.PulseAll(gate);
                return;
            }
        }
        Fail(item, error);
    }

    // Caller holds gate; a generation is never started while another is being released.
    private void StartWorkers()
    {
        long epoch = ++generation;
        for (int index = 0; index < workerCount; index++)
        {
            int slot = index;
            var thread = new Thread(() => Worker(slot, epoch), 5 * 1024 * 1024)
            { IsBackground = true, Name = $"BattleWorker-{epoch}-{slot}" };
            workers.Add(thread);
            thread.Start();
        }
    }

    private void Worker(int slot, long epoch)
    {
        try
        {
            initialize(slot);
            for (;;)
            {
                T item;
                lock (gate)
                {
                    while (!disposed && fault == null && epoch == generation && queue.Count == 0)
                        Monitor.Wait(gate);
                    if (disposed || fault != null || epoch != generation) return;
                    item = queue.Dequeue();
                    active++;
                }
                try { execute(item); }
                catch (Exception error) { Fail(item, error); }
                finally
                {
                    item = default;
                    lock (gate)
                    {
                        active--;
                        lastCompletion = clock.GetTimestamp();
                        if (!disposed && fault == null && queue.Count == 0 && active == 0 &&
                            dirty && idleTimeout > TimeSpan.Zero)
                            timer.Change(TimerDelay(idleTimeout), Timeout.InfiniteTimeSpan);
                    }
                }
            }
        }
        catch (Exception error)
        {
            T[] abandoned;
            lock (gate)
            {
                fault = error;
                generation++;
                if (!disposed) timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                abandoned = queue.ToArray();
                queue.Clear();
                Monitor.PulseAll(gate);
            }
            foreach (var item in abandoned) Fail(item, error);
        }
    }

    private void OnIdle(object state)
    {
        Thread[] retired;
        lock (gate)
        {
            if (disposed || fault != null || releasing || !dirty || active != 0 || queue.Count != 0 ||
                idleTimeout == TimeSpan.Zero) return;
            var remaining = idleTimeout - clock.GetElapsedTime(lastCompletion);
            if (remaining > TimeSpan.Zero)
            {
                timer.Change(TimerDelay(remaining), Timeout.InfiniteTimeSpan);
                return;
            }
            releasing = true;
            generation++;
            retired = workers.ToArray();
            workers.Clear();
            Monitor.PulseAll(gate);
        }
        T[] abandoned = Array.Empty<T>();
        Exception failure = null;
        try
        {
            foreach (var thread in retired) thread.Join();
            // No old worker, ThreadStatic cache or running callback can access the assets now.
            release();
            lock (gate)
            {
                releaseCount++;
                dirty = queue.Count != 0;
            }
        }
        catch (Exception error)
        {
            failure = error;
            Console.Error.WriteLine($"[IdleResourceRelease] failed: {error}");
            lock (gate)
            {
                fault = error;
                abandoned = queue.ToArray();
                queue.Clear();
            }
        }
        finally
        {
            foreach (var item in abandoned) Fail(item, failure);
            lock (gate)
            {
                releasing = false;
                if (!disposed && fault == null && queue.Count != 0) StartWorkers();
                Monitor.PulseAll(gate);
            }
        }
    }

    private void Fail(T item, Exception error)
    {
        try { failed(item, error); }
        catch (Exception callbackError) { Console.Error.WriteLine(callbackError); }
    }

    private static TimeSpan TimerDelay(TimeSpan delay) => delay > MaxTimerDelay ? MaxTimerDelay : delay;

    public void Dispose()
    {
        Thread[] retired;
        T[] abandoned;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            generation++;
            timer.Dispose();
            abandoned = queue.ToArray();
            queue.Clear();
            retired = workers.ToArray();
            workers.Clear();
            Monitor.PulseAll(gate);
        }
        foreach (var item in abandoned) Fail(item, new ObjectDisposedException(nameof(IdleWorkerPool<T>)));
        foreach (var thread in retired)
            if (thread != Thread.CurrentThread) thread.Join();
        lock (gate)
            while (releasing) Monitor.Wait(gate);
    }
}
