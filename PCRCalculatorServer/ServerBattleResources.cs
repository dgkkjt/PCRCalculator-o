using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using PCRCaculator;
using UnityEngine;

namespace PCRCalculatorServer;

/// <summary>Only the idle worker-pool coordinator may release shared battle resources.</summary>
internal static class ServerBattleResources
{
    private static int releaseCount;
    internal static int ReleaseCount => Volatile.Read(ref releaseCount);

    internal static void Release()
    {
        var watch = Stopwatch.StartNew();
        var before = Memory();
        Console.WriteLine("[IdleResourceRelease] starting");
        var released = ClearReferences();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        var after = Memory();
        int count = Interlocked.Increment(ref releaseCount);
        Console.WriteLine("[IdleResourceRelease] completed " + JsonSerializer.Serialize(new
        {
            count, reason = "idle", elapsedMs = watch.Elapsed.TotalMilliseconds,
            bundles = released.Bundles, objects = released.Objects, effects = released.Effects, before, after
        }));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (int Bundles, int Objects, int Effects) ClearReferences()
    {
        ABExTool.ReleaseServerMemoryCaches();
        SpineCreator.ReleaseServerMemoryCache();
        IlEmitInstantiator.ReleaseServerMemoryCache();
        return AssetBundle.ReleaseServerMemoryCaches();
    }

    private static object Memory()
    {
        using var process = Process.GetCurrentProcess();
        return new { managed = GC.GetTotalMemory(false), privateBytes = process.PrivateMemorySize64,
            working = process.WorkingSet64, committed = GC.GetGCMemoryInfo().TotalCommittedBytes };
    }
}
