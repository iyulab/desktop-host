namespace DesktopHost.Diagnostics;

/// <summary>How often a <see cref="ThreadPoolStallMonitor"/> probes the pool, what counts as a stall, and who hears of one.</summary>
public sealed class ThreadPoolStallMonitorOptions
{
    /// <summary>
    /// A probe that has not started after this long is a stall. A healthy pool starts a work item in well under a millisecond;
    /// set it to what an outside watcher will tolerate (a supervisor's health-check timeout, for instance) to learn about a
    /// stall before it is acted on. Default 1 s.
    /// </summary>
    public TimeSpan Threshold { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The time between one probe's start and the next probe being queued. Default 1 s.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Called once per stall when it is declared, on the monitor's own thread — the pool may be the thing that is stuck, so the
    /// call must not wait on pool work. An exception from it is logged and the monitor goes on.
    /// </summary>
    public Action<ThreadPoolStallStarted>? StallStarted { get; set; }

    /// <summary>
    /// Called once per stall when it ends, on the monitor's own thread. Not called for a stall still going on when the monitor
    /// is disposed. An exception from it is logged and the monitor goes on.
    /// </summary>
    public Action<ThreadPoolStall>? StallEnded { get; set; }
}
