namespace DesktopHost.Diagnostics;

/// <summary>The thread pool as it stood at one moment.</summary>
/// <param name="PendingWorkItems">Work items queued and not yet started (<see cref="ThreadPool.PendingWorkItemCount"/>).</param>
/// <param name="ThreadCount">Pool threads that exist (<see cref="ThreadPool.ThreadCount"/>).</param>
/// <param name="MinWorkerThreads">The minimum the pool creates on demand without waiting (<see cref="ThreadPool.GetMinThreads"/>).</param>
/// <param name="ProcessorCount">Processors the process may use (<see cref="Environment.ProcessorCount"/>).</param>
public readonly record struct ThreadPoolSnapshot(long PendingWorkItems, int ThreadCount, int MinWorkerThreads, int ProcessorCount)
{
    /// <summary>The pool now.</summary>
    public static ThreadPoolSnapshot Take()
    {
        ThreadPool.GetMinThreads(out var minWorker, out _);
        return new ThreadPoolSnapshot(ThreadPool.PendingWorkItemCount, ThreadPool.ThreadCount, minWorker, Environment.ProcessorCount);
    }

    /// <summary>The larger queue and the larger thread count of the two — the worst a stall reached.</summary>
    public ThreadPoolSnapshot Max(ThreadPoolSnapshot other) => this with
    {
        PendingWorkItems = Math.Max(PendingWorkItems, other.PendingWorkItems),
        ThreadCount = Math.Max(ThreadCount, other.ThreadCount),
    };
}

/// <summary>A probe has waited longer than the threshold to start: the pool is stalled from here on.</summary>
/// <param name="Waited">How long the probe had waited when the stall was declared — the threshold, give or take the monitor's step.</param>
/// <param name="Pool">The pool at that moment.</param>
public sealed record ThreadPoolStallStarted(TimeSpan Waited, ThreadPoolSnapshot Pool);

/// <summary>A stall that ended: the probe finally started.</summary>
/// <param name="Duration">From queuing the probe to its start.</param>
/// <param name="Peak">The deepest queue and the most threads seen while the probe waited.</param>
public sealed record ThreadPoolStall(TimeSpan Duration, ThreadPoolSnapshot Peak);
