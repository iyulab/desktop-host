using System.Collections.Concurrent;
using System.Diagnostics;

namespace DesktopHost.Diagnostics.Tests;

public sealed class ThreadPoolStallMonitorTests
{
    private static readonly ThreadPoolSnapshot Pool = new(PendingWorkItems: 12, ThreadCount: 9, MinWorkerThreads: 8, ProcessorCount: 8);

    // Runs each probe on a thread of its own after the delay the test gives for it (the last delay repeats).
    private sealed class DelayedQueue(params TimeSpan[] delays)
    {
        private int _next;
        public ConcurrentBag<Thread> Threads { get; } = [];

        public void Queue(Action work)
        {
            var index = Interlocked.Increment(ref _next) - 1;
            var delay = delays[Math.Min(index, delays.Length - 1)];
            var thread = new Thread(() => { Thread.Sleep(delay); work(); }) { IsBackground = true };
            Threads.Add(thread);
            thread.Start();
        }
    }

    private static ThreadPoolStallMonitorOptions Options(List<ThreadPoolStallStarted> started, List<ThreadPoolStall> ended) => new()
    {
        Threshold = TimeSpan.FromMilliseconds(150),
        Interval = TimeSpan.FromMilliseconds(20),
        StallStarted = s => { lock (started) started.Add(s); },
        StallEnded = s => { lock (ended) ended.Add(s); },
    };

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout) Thread.Sleep(10);
    }

    [Fact]
    public void A_pool_that_starts_probes_at_once_reports_nothing()
    {
        List<ThreadPoolStallStarted> started = [];
        List<ThreadPoolStall> ended = [];
        var queue = new DelayedQueue(TimeSpan.Zero);
        using (var monitor = new ThreadPoolStallMonitor(Options(started, ended), null, queue.Queue, () => Pool))
        {
            monitor.Start();
            WaitUntil(() => queue.Threads.Count >= 10, TimeSpan.FromSeconds(5));
        }

        Assert.True(queue.Threads.Count >= 10);
        Assert.Empty(started);
        Assert.Empty(ended);
    }

    [Fact]
    public void A_probe_that_waits_past_the_threshold_is_one_stall_with_its_length_and_the_pool()
    {
        List<ThreadPoolStallStarted> started = [];
        List<ThreadPoolStall> ended = [];
        var queue = new DelayedQueue(TimeSpan.FromMilliseconds(600), TimeSpan.Zero);
        using (var monitor = new ThreadPoolStallMonitor(Options(started, ended), null, queue.Queue, () => Pool))
        {
            monitor.Start();
            WaitUntil(() => { lock (ended) return ended.Count > 0; }, TimeSpan.FromSeconds(5));
            WaitUntil(() => queue.Threads.Count >= 5, TimeSpan.FromSeconds(5));
        }

        var start = Assert.Single(started);
        Assert.InRange(start.Waited, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(550));
        Assert.Equal(Pool, start.Pool);
        var stall = Assert.Single(ended);
        Assert.InRange(stall.Duration, TimeSpan.FromMilliseconds(550), TimeSpan.FromSeconds(3));
        Assert.Equal(Pool, stall.Peak);
    }

    [Fact]
    public void The_peak_is_the_deepest_queue_and_the_most_threads_seen_during_the_stall()
    {
        List<ThreadPoolStallStarted> started = [];
        List<ThreadPoolStall> ended = [];
        var reads = 0;
        ThreadPoolSnapshot Snapshot()
        {
            var n = Interlocked.Increment(ref reads);
            return n == 2 ? Pool with { PendingWorkItems = 40, ThreadCount = 11 } : Pool;
        }

        var queue = new DelayedQueue(TimeSpan.FromMilliseconds(600), TimeSpan.Zero);
        using (var monitor = new ThreadPoolStallMonitor(Options(started, ended), null, queue.Queue, Snapshot))
        {
            monitor.Start();
            WaitUntil(() => { lock (ended) return ended.Count > 0; }, TimeSpan.FromSeconds(5));
        }

        Assert.Equal(Pool, Assert.Single(started).Pool);
        Assert.Equal(Pool with { PendingWorkItems = 40, ThreadCount = 11 }, Assert.Single(ended).Peak);
    }

    [Fact]
    public void Dispose_during_a_stall_returns_promptly_and_reports_no_end()
    {
        List<ThreadPoolStallStarted> started = [];
        List<ThreadPoolStall> ended = [];
        var queue = new DelayedQueue(TimeSpan.FromSeconds(1));
        var monitor = new ThreadPoolStallMonitor(Options(started, ended), null, queue.Queue, () => Pool);
        monitor.Start();
        WaitUntil(() => { lock (started) return started.Count > 0; }, TimeSpan.FromSeconds(5));

        var sw = Stopwatch.StartNew();
        monitor.Dispose();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500), $"dispose took {sw.Elapsed}");

        foreach (var thread in queue.Threads) thread.Join(); // the late probe starts after the monitor gave up on it
        Assert.Single(started);
        Assert.Empty(ended);
    }

    [Fact]
    public void A_failing_callback_does_not_stop_the_monitor()
    {
        List<ThreadPoolStall> ended = [];
        var calls = 0;
        var options = new ThreadPoolStallMonitorOptions
        {
            Threshold = TimeSpan.FromMilliseconds(100),
            Interval = TimeSpan.FromMilliseconds(20),
            StallStarted = _ => throw new InvalidOperationException("listener failed"),
            StallEnded = s =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("listener failed");
                lock (ended) ended.Add(s);
            },
        };
        var queue = new DelayedQueue(TimeSpan.FromMilliseconds(300), TimeSpan.Zero, TimeSpan.FromMilliseconds(300), TimeSpan.Zero);
        using (var monitor = new ThreadPoolStallMonitor(options, null, queue.Queue, () => Pool))
        {
            monitor.Start();
            WaitUntil(() => { lock (ended) return ended.Count > 0; }, TimeSpan.FromSeconds(5));
        }

        Assert.Equal(2, calls);
        Assert.Single(ended);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(-1, 1000)]
    [InlineData(1000, 0)]
    public void Threshold_and_interval_must_be_positive(int thresholdMs, int intervalMs)
    {
        var options = new ThreadPoolStallMonitorOptions
        {
            Threshold = TimeSpan.FromMilliseconds(thresholdMs),
            Interval = TimeSpan.FromMilliseconds(intervalMs),
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThreadPoolStallMonitor(options));
    }

    [Fact]
    public void Start_after_dispose_throws_and_start_twice_is_one_monitor()
    {
        var queue = new DelayedQueue(TimeSpan.Zero);
        var monitor = new ThreadPoolStallMonitor(new ThreadPoolStallMonitorOptions(), null, queue.Queue, () => Pool);
        monitor.Start();
        monitor.Start();
        monitor.Dispose();
        monitor.Dispose();
        Assert.Throws<ObjectDisposedException>(monitor.Start);
    }
}

/// <summary>Blocks real pool threads, so it runs alone, after the parallel classes.</summary>
[CollectionDefinition(nameof(RealPool), DisableParallelization = true)]
public sealed class RealPool;

[Collection(nameof(RealPool))]
public sealed class ThreadPoolStallMonitorRealPoolTests
{
    [Fact]
    public void Blocking_more_pool_threads_than_the_minimum_is_reported_as_a_stall_and_its_end()
    {
        ThreadPoolStallStarted? started = null;
        ThreadPoolStall? ended = null;
        using var endedSignal = new ManualResetEventSlim(false);
        var options = new ThreadPoolStallMonitorOptions
        {
            Threshold = TimeSpan.FromMilliseconds(500),
            Interval = TimeSpan.FromMilliseconds(50),
            StallStarted = s => started = s,
            StallEnded = s => { ended = s; endedSignal.Set(); },
        };
        using var monitor = new ThreadPoolStallMonitor(options);
        monitor.Start();
        Thread.Sleep(200);

        // Every existing thread plus a margin the pool can only add slowly: the probe queues behind the blocked items.
        ThreadPool.GetMinThreads(out var minWorker, out _);
        var blockers = Math.Max(ThreadPool.ThreadCount, minWorker) + 8;
        using var release = new ManualResetEventSlim(false);
        for (var i = 0; i < blockers; i++)
            ThreadPool.UnsafeQueueUserWorkItem(_ => release.Wait(TimeSpan.FromSeconds(30)), null);
        try
        {
            Assert.True(endedSignal.Wait(TimeSpan.FromSeconds(25)), "the stall never ended");
        }
        finally
        {
            release.Set();
        }

        Assert.NotNull(started);
        Assert.NotNull(ended);
        Assert.True(ended.Duration >= TimeSpan.FromMilliseconds(500), $"duration {ended.Duration}");
        Assert.True(ended.Peak.ThreadCount >= started.Pool.ThreadCount);
    }
}
