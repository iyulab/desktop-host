using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopHost.Diagnostics;

/// <summary>
/// Notices when the thread pool stops starting work. Every <see cref="ThreadPoolStallMonitorOptions.Interval"/> it queues one
/// small work item and waits for it on a thread of its own — a dedicated thread, so a starved pool cannot starve the watcher.
/// A probe that has not started within <see cref="ThreadPoolStallMonitorOptions.Threshold"/> is a stall: it is logged with the
/// pool's queue and thread count, and logged again with its length when the probe finally runs.
/// </summary>
/// <remarks>
/// Past its minimum the pool adds threads only slowly (about two a second), so blocking work queued in a burst — a synchronous
/// wait on a task, a long synchronous walk on a request thread — delays every queued item behind it, request handlers and
/// health endpoints included. A process in that state answers nothing, yet looks alive to itself. This is the signal it gives.
/// </remarks>
public sealed partial class ThreadPoolStallMonitor : IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    private readonly ThreadPoolStallMonitorOptions _options;
    private readonly ILogger _logger;
    private readonly Action<Action> _queue;
    private readonly Func<ThreadPoolSnapshot> _snapshot;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Lock _gate = new();
    private Thread? _thread;
    private bool _disposed;

    /// <summary>Creates a monitor; <see cref="Start"/> begins probing.</summary>
    public ThreadPoolStallMonitor(ThreadPoolStallMonitorOptions options, ILogger<ThreadPoolStallMonitor>? logger = null)
        : this(options, logger, static work => ThreadPool.UnsafeQueueUserWorkItem(static state => ((Action)state!)(), work),
            ThreadPoolSnapshot.Take)
    {
    }

    internal ThreadPoolStallMonitor(ThreadPoolStallMonitorOptions options, ILogger? logger, Action<Action> queue,
        Func<ThreadPoolSnapshot> snapshot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Threshold, TimeSpan.Zero, nameof(options.Threshold));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Interval, TimeSpan.Zero, nameof(options.Interval));
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _queue = queue;
        _snapshot = snapshot;
    }

    /// <summary>Starts probing on the monitor's own background thread. Calling it again does nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "thread-pool-stall-monitor" };
            _thread.Start();
        }
    }

    /// <summary>Stops probing and waits briefly for the monitor's thread to end. A stall still going on is not reported as ended.</summary>
    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
        }
        _stop.Set();
        if (thread is null || thread.Join(TimeSpan.FromSeconds(2))) _stop.Dispose();
    }

    private void Run()
    {
        while (!_stop.IsSet)
        {
            // A fresh probe each time, never disposed: one that starts after the monitor gave up on it sets an event nobody reads.
            var probe = new Probe();
            var queuedAt = Stopwatch.GetTimestamp();
            try
            {
                _queue(probe.Run);
            }
            catch (Exception ex) when (ex is NotSupportedException or OutOfMemoryException)
            {
                LogQueueFailed(_logger, ex);
                return;
            }

            if (!WaitForProbe(probe, _options.Threshold))
            {
                if (_stop.IsSet) return;
                if (!FollowStall(probe, queuedAt)) return;
            }

            _stop.Wait(_options.Interval);
        }
    }

    private bool FollowStall(Probe probe, long queuedAt)
    {
        var pool = _snapshot();
        var waited = Stopwatch.GetElapsedTime(queuedAt);
        LogStallStarted(_logger, waited.TotalMilliseconds, pool.PendingWorkItems, pool.ThreadCount, pool.MinWorkerThreads);
        Notify(_options.StallStarted, new ThreadPoolStallStarted(waited, pool));

        var peak = pool;
        while (!WaitForProbe(probe, Step))
        {
            if (_stop.IsSet) return false;
            peak = peak.Max(_snapshot());
        }

        var duration = Stopwatch.GetElapsedTime(queuedAt, probe.StartedAt);
        LogStallEnded(_logger, duration.TotalMilliseconds, peak.PendingWorkItems, peak.ThreadCount);
        Notify(_options.StallEnded, new ThreadPoolStall(duration, peak));
        return true;
    }

    // Waits for the probe in steps so a dispose is seen within one step.
    private bool WaitForProbe(Probe probe, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            var left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            if (left <= TimeSpan.Zero) return probe.Started.IsSet;
            if (probe.Started.Wait(left < Step ? left : Step)) return true;
            if (_stop.IsSet) return false;
        }
    }

    private void Notify<T>(Action<T>? callback, T value)
    {
        if (callback is null) return;
        try
        {
            callback(value);
        }
        catch (Exception ex)
        {
            LogCallbackFailed(_logger, ex);
        }
    }

    private sealed class Probe
    {
        public ManualResetEventSlim Started { get; } = new(false);
        public long StartedAt { get; private set; }

        public void Run()
        {
            StartedAt = Stopwatch.GetTimestamp();
            Started.Set();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The thread pool has not started a work item for {WaitedMs:F0} ms: {Pending} queued, {Threads} threads (minimum {MinThreads})")]
    private static partial void LogStallStarted(ILogger logger, double waitedMs, long pending, int threads, int minThreads);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The thread pool started work again after {DurationMs:F0} ms; at worst {Pending} queued, {Threads} threads")]
    private static partial void LogStallEnded(ILogger logger, double durationMs, long pending, int threads);

    [LoggerMessage(Level = LogLevel.Error, Message = "A thread-pool stall callback failed")]
    private static partial void LogCallbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The thread-pool stall monitor could not queue its probe and has stopped")]
    private static partial void LogQueueFailed(ILogger logger, Exception exception);
}
