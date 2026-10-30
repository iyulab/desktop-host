using System.Diagnostics;
using ChildProcessGuard;
using Microsoft.Extensions.Logging;

namespace DesktopHost.Processes;

/// <summary>When an unexpected exit is followed by a new start, and when the supervisor gives up.</summary>
public sealed class RestartPolicy
{
    /// <summary>
    /// The wait before each restart in a row: the first exit waits <c>Delays[0]</c>, the next <c>Delays[1]</c>, and an
    /// exit after the last one is the end — the supervisor gives up. Empty means never restart. Default 2 s, 8 s, 32 s.
    /// </summary>
    public IReadOnlyList<TimeSpan> Delays { get; set; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(32)];

    /// <summary>A run that lasted at least this long before it exited starts the count of restarts over. Default 2 min.</summary>
    public TimeSpan StableAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Says that an exit code means a new start would fail the same way — the supervisor gives up at once. <c>null</c>
    /// treats every exit code as worth a restart. <see cref="ExitCodes.IsUnhandledManagedException"/> is one such
    /// predicate.
    /// </summary>
    public Func<int, bool>? IsPermanentExit { get; set; }
}

/// <summary>How a running child is stopped: the caller's own way first, then the window's close, then the tree.</summary>
public sealed class StopPolicy
{
    /// <summary>
    /// Asks the child to end in its own way — a shutdown request, a message on its input. <c>null</c> skips the step.
    /// An exception from it is logged and the stop goes on to the next step.
    /// </summary>
    public Func<Process, CancellationToken, Task>? GracefulStop { get; set; }

    /// <summary>How long the child has to exit after <see cref="GracefulStop"/>. Default 5 s.</summary>
    public TimeSpan GracefulTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether to close the child's main window before the tree is killed — for a child that has one. Default off.</summary>
    public bool CloseMainWindow { get; set; }

    /// <summary>How long the child has to exit after its window was closed. Default 3 s.</summary>
    public TimeSpan CloseMainWindowTimeout { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// How a running child that has not exited is judged alive: a check the caller supplies, made every <see cref="Period"/>,
/// and a restart once <see cref="FailureThreshold"/> checks in a row are missed — any answer starts the count over. The
/// shape of a container orchestrator's liveness probe; the check itself (an HTTP path, a ping on a pipe) is the caller's.
/// </summary>
public sealed class LivenessPolicy
{
    /// <summary>
    /// The check: <c>true</c> when the child answered as alive. <c>false</c>, an exception, or no answer within
    /// <see cref="Timeout"/> is a miss. Required.
    /// </summary>
    public Func<Process, CancellationToken, Task<bool>>? Probe { get; set; }

    /// <summary>The time between checks. Default 5 s.</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long one check may take before it counts as a miss. Default 5 s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Misses in a row that make the child unresponsive — it is stopped and the restart policy applies. Default 3.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>
    /// A window after the child became ready in which a miss is not counted — for a child still warming up after it said
    /// it was ready. Default 10 s.
    /// </summary>
    public TimeSpan StartupGrace { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (Probe is null) throw new ArgumentException("A liveness policy needs a probe.", nameof(Probe));
        if (Period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Period), "The period must be positive.");
        if (Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Timeout), "The timeout must be positive.");
        if (FailureThreshold < 1) throw new ArgumentOutOfRangeException(nameof(FailureThreshold), "At least one miss is needed.");
        if (StartupGrace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(StartupGrace), "The grace cannot be negative.");
    }
}

/// <summary>What a <see cref="ProcessSupervisor"/> runs and how it watches it.</summary>
public sealed class ProcessSupervisorOptions
{
    /// <summary>A label for log lines and messages. Required.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Resolves what to run, at every start and restart — so a changed setting takes effect at the next start.
    /// <c>null</c> means there is nothing to run: the supervisor fails with <see cref="ProcessSupervisor.LastError"/>
    /// saying so. Required.
    /// </summary>
    public Func<SupervisedCommand?>? Command { get; set; }

    /// <summary>
    /// Runs once for each new process, after it starts: bind its standard input and output, then return when it is
    /// ready to be used. The supervisor is <see cref="ProcessState.Running"/> when this returns. If it throws, or does
    /// not return within <see cref="ReadyTimeout"/>, the process is killed and the start counts as a failed run under the
    /// <see cref="Restart"/> policy. <c>null</c> means a started process is ready.
    /// </summary>
    public Func<Process, CancellationToken, Task>? Ready { get; set; }

    /// <summary>How long <see cref="Ready"/> may take; <c>null</c> for no limit. Default 60 s.</summary>
    public TimeSpan? ReadyTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>When an unexpected exit is followed by a new start.</summary>
    public RestartPolicy Restart { get; set; } = new();

    /// <summary>How a running child is stopped.</summary>
    public StopPolicy Stop { get; set; } = new();

    /// <summary>
    /// How a running child is judged alive beyond not having exited; <c>null</c> (the default) watches the exit only.
    /// An unresponsive child is stopped the way <see cref="Stop"/> says and counts as a failed run.
    /// </summary>
    public LivenessPolicy? Liveness { get; set; }

    /// <summary>How many of the last standard-error lines to keep for <see cref="ProcessSupervisor.StderrTail"/>. Default 20.</summary>
    public int StderrTailLines { get; set; } = 20;

    /// <summary>
    /// The guardian that starts the child and kills its tree — on Windows it puts every child in a job object, so the
    /// children end with this process even when it is killed. Pass one guardian to every supervisor of a host; the
    /// supervisor does not dispose a guardian it was given. <c>null</c>: the supervisor creates its own and disposes it.
    /// </summary>
    public ProcessGuardian? Guardian { get; set; }

    /// <summary>
    /// For a supervisor that starts a long-lived child and then ends on purpose — a launcher that hands over to the program
    /// it started. Disposing the supervisor then ends the supervision (no more restarts, no more checks) and <b>leaves the
    /// running child alone</b> instead of stopping it; <see cref="ProcessSupervisor.StopAsync"/> still stops it. The
    /// guardian the supervisor creates is built so that its children outlive it; a guardian passed in through
    /// <see cref="Guardian"/> must already be (<c>ProcessGuardianOptions.ChildrenOutliveOwner</c>), or the options are
    /// refused — a job object that kills on close would take the child down with the guardian. Default off.
    /// <para>
    /// What the child outlives is the supervisor <i>ending</i>, not the owner process crashing: if the owner is killed on
    /// Windows the child is still tracked by nothing, and on Linux and macOS it is never cleaned up for the owner. The
    /// supervisor's pipes to the child (standard input, output and error) end with it, so the child must not depend on them.
    /// </para>
    /// </summary>
    public bool ChildOutlivesSupervisor { get; set; }

    /// <summary>Where lifecycle lines and the child's standard error are logged. Default: nowhere.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>The clock for restart waits and the <see cref="RestartPolicy.StableAfter"/> window. Default the system clock.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("A name is required.", nameof(Name));
        if (Command is null) throw new ArgumentException("A command resolver is required.", nameof(Command));
        ArgumentNullException.ThrowIfNull(Restart);
        ArgumentNullException.ThrowIfNull(Stop);
        if (Restart.Delays.Any(d => d < TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(Restart), "A restart delay cannot be negative.");
        if (ReadyTimeout is { } t && t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ReadyTimeout), "The ready timeout must be positive, or null.");
        if (StderrTailLines < 0) throw new ArgumentOutOfRangeException(nameof(StderrTailLines));
        if (ChildOutlivesSupervisor && Guardian is { Options.ChildrenOutliveOwner: false })
            throw new ArgumentException(
                "The child can outlive the supervisor only if the guardian lets its children outlive their owner.", nameof(Guardian));
        Liveness?.Validate();
    }
}

/// <summary>Exit codes with a known meaning.</summary>
public static class ExitCodes
{
    /// <summary>
    /// The code a .NET process exits with when an exception went unhandled (<c>0xE0434352</c>) — a start that will
    /// usually fail the same way again.
    /// </summary>
    public const int UnhandledManagedException = unchecked((int)0xE0434352);

    /// <summary>A <see cref="RestartPolicy.IsPermanentExit"/> predicate for <see cref="UnhandledManagedException"/>.</summary>
    public static bool IsUnhandledManagedException(int exitCode) => exitCode == UnhandledManagedException;
}
