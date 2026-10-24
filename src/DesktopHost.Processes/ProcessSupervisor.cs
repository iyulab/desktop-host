using System.ComponentModel;
using System.Diagnostics;
using ChildProcessGuard;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopHost.Processes;

/// <summary>Where a <see cref="ProcessSupervisor"/> stands.</summary>
public enum ProcessState
{
    /// <summary>Not started, or stopped on request.</summary>
    Stopped,

    /// <summary>A process was started and its <see cref="ProcessSupervisorOptions.Ready"/> hook is running.</summary>
    Starting,

    /// <summary>The process is ready.</summary>
    Running,

    /// <summary>The process exited unexpectedly and a new start is scheduled.</summary>
    Restarting,

    /// <summary>The supervisor gave up; <see cref="ProcessSupervisor.LastError"/> says why. A new <see cref="ProcessSupervisor.StartAsync"/> tries again.</summary>
    Failed,
}

/// <summary>
/// Starts a long-lived child process, waits until it is ready, starts it again after an unexpected exit within a
/// <see cref="RestartPolicy"/>, and stops it — the caller's graceful step, the window's close, then the whole tree.
/// </summary>
/// <remarks>
/// <para>
/// The child is started through a ChildProcessGuard <see cref="ProcessGuardian"/>, which on Windows places it in a job
/// object and elsewhere in its own process group: the child and everything it starts end with this process even when
/// this process is killed, and a tree kill reaches grandchildren. Its environment is exactly what the
/// <see cref="SupervisedCommand"/> names. Standard input, output and error are redirected; the
/// <see cref="ProcessSupervisorOptions.Ready"/> hook binds input and output, and standard error is read line by line
/// into the log, <see cref="StderrLine"/> and <see cref="StderrTail"/>.
/// </para>
/// <para>
/// Each process the supervisor starts is a new generation. An exit is acted on only if it belongs to the current
/// generation and the supervisor did not ask for it, so the end of a process replaced by <see cref="RestartAsync"/> or
/// stopped by <see cref="StopAsync"/> never schedules a restart.
/// </para>
/// <para>
/// Stopping is not final: <see cref="StopAsync"/> followed by <see cref="StartAsync"/> starts a new process that is
/// watched like the first. Only <see cref="DisposeAsync"/> ends the supervisor; it is safe to call while a stop is in
/// progress, and a stop that arrives after it does nothing. Events are raised after the supervisor's own lock is
/// released, so a handler may call back into it.
/// </para>
/// </remarks>
public sealed partial class ProcessSupervisor : IAsyncDisposable
{
    private readonly ProcessSupervisorOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly ProcessGuardian _guardian;
    private readonly bool _ownsGuardian;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _tail = new();
    private readonly List<Action> _pendingEvents = [];
    private CancellationTokenSource _lifetime = new();
    private Process? _process;
    private volatile bool _disposed;
    private int _disposeStarted;
    private int _generation;
    private int _consecutiveFailures;
    private DateTimeOffset _lastStart;
    private string? _runFailure;
    private int? _exitCodeAtKill;

    /// <summary>Creates a supervisor. Nothing starts until <see cref="StartAsync"/>.</summary>
    /// <exception cref="ArgumentException">A required option is missing or out of range.</exception>
    public ProcessSupervisor(ProcessSupervisorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _logger = options.Logger ?? NullLogger.Instance;
        _clock = options.TimeProvider;
        _ownsGuardian = options.Guardian is null;
        _guardian = options.Guardian ?? new ProcessGuardian();
    }

    /// <summary>Where the supervisor stands.</summary>
    public ProcessState State { get; private set; } = ProcessState.Stopped;

    /// <summary>Why the last start failed or the last process exited; <c>null</c> while all is well.</summary>
    public string? LastError { get; private set; }

    /// <summary>The current process's id, while it runs.</summary>
    public int? Pid
    {
        get
        {
            var p = _process;
            try
            {
                return p is { HasExited: false } ? p.Id : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>Processes started after the first — restarts on request included.</summary>
    public int Restarts { get; private set; }

    /// <summary>Exits the supervisor did not ask for.</summary>
    public int UnexpectedExits { get; private set; }

    /// <summary>The last standard-error lines of the current or most recent process, oldest first.</summary>
    public IReadOnlyList<string> StderrTail
    {
        get
        {
            lock (_tail) return [.. _tail];
        }
    }

    /// <summary>Raised when <see cref="State"/> changes, with the new state.</summary>
    public event Action<ProcessState>? StateChanged;

    /// <summary>Raised after each unexpected exit, with the exit code and whether a new start is scheduled.</summary>
    public event Action<int, bool>? Exited;

    /// <summary>Raised when the supervisor gives up, with the reason (also in <see cref="LastError"/>).</summary>
    public event Action<string>? GaveUp;

    /// <summary>Raised for every non-empty standard-error line of the current process, after it was logged.</summary>
    public event Action<string>? StderrLine;

    /// <summary>
    /// Starts the child and returns once it is ready, failed, or scheduled to start again. Does nothing while a process
    /// is starting or running.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller cancelled before the process was ready; it was killed.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is ProcessState.Running or ProcessState.Starting) return;
            if (_lifetime.IsCancellationRequested)
            {
                // A stop ended the last process's watchers through this source; the next one needs a live source.
                _lifetime.Dispose();
                _lifetime = new CancellationTokenSource();
            }
            _consecutiveFailures = 0;
            await SpawnAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate();
        }
    }

    /// <summary>
    /// Replaces the current process with a new one — stopped the way <see cref="StopAsync"/> stops it — and starts
    /// the count of restarts over. Does nothing when the supervisor is stopped.
    /// </summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == ProcessState.Stopped) return;
            await StopCurrentAsync(graceful: true).ConfigureAwait(false);
            _consecutiveFailures = 0;
            await SpawnAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate();
        }
    }

    /// <summary>Stops the current process — graceful step, window close, tree kill — and any scheduled restart.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default) => _disposed ? Task.CompletedTask : StopCoreAsync();

    private async Task StopCoreAsync()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The dispose won the race and has already stopped everything.
            return;
        }
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            SetState(ProcessState.Stopped);
            await StopCurrentAsync(graceful: true).ConfigureAwait(false);
        }
        finally
        {
            ReleaseGate();
        }
    }

    // Under the gate.
    private async Task SpawnAsync(CancellationToken cancellationToken)
    {
        var command = _options.Command!();
        if (command is null)
        {
            GiveUp("there is no command to run");
            LogNoCommand(_logger, _options.Name);
            return;
        }

        SetState(ProcessState.Starting);
        var psi = new ProcessStartInfo
        {
            FileName = command.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = command.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in command.Arguments) psi.ArgumentList.Add(argument);
        psi.Environment.Clear();
        foreach (var (key, value) in command.Environment) psi.Environment[key] = value;

        Process process;
        try
        {
            process = _guardian.StartProcessWithStartInfo(psi)
                ?? throw new InvalidOperationException("the process could not be started");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStartFailed(_logger, ex, _options.Name, command.FileName);
            GiveUp($"could not start {command.FileName}: {ex.Message}");
            return;
        }

        var generation = ++_generation;
        _process = process;
        _lastStart = _clock.GetUtcNow();
        _runFailure = null;
        _exitCodeAtKill = null;
        LastError = null;
        Restarts = generation - 1;
        lock (_tail) _tail.Clear();
        LogStarted(_logger, _options.Name, process.Id, generation);

        var lifetime = _lifetime.Token;
        _ = PumpStderrAsync(process, generation);
        _ = WatchExitAsync(process, generation, lifetime);

        if (_options.Ready is { } ready)
        {
            using var timeout = _options.ReadyTimeout is { } limit
                ? new CancellationTokenSource(limit, _clock)
                : new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime, timeout.Token);
            try
            {
                await ready(process, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // A stop is waiting for the gate; it ends this process.
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await StopCurrentAsync(graceful: false).ConfigureAwait(false);
                SetState(ProcessState.Stopped);
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                FailRun(generation, $"not ready within {_options.ReadyTimeout!.Value.TotalSeconds:0.#} s");
                await StopCurrentAsync(graceful: false).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                LogReadyFailed(_logger, ex, _options.Name);
                FailRun(generation, $"not ready: {ex.Message}");
                await StopCurrentAsync(graceful: false).ConfigureAwait(false);
                return;
            }
        }

        SetState(ProcessState.Running);
        if (_options.Liveness is { } liveness) _ = WatchLivenessAsync(process, generation, liveness, lifetime);
    }

    // Checks the running child every period. Misses in a row up to the threshold end it the way a stop does, as a failed
    // run named for the checks — the exit watcher then applies the restart policy. Ends with the process's generation.
    private async Task WatchLivenessAsync(Process process, int generation, LivenessPolicy policy, CancellationToken lifetime)
    {
        var readyAt = _clock.GetUtcNow();
        var misses = 0;
        try
        {
            while (true)
            {
                await Task.Delay(policy.Period, _clock, lifetime).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _generation) || process.HasExited) return;

                bool alive;
                using (var timeout = new CancellationTokenSource(policy.Timeout, _clock))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, timeout.Token))
                {
                    try
                    {
                        alive = await policy.Probe!(process, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        alive = false;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        LogLivenessProbeFailed(_logger, ex, _options.Name, generation);
                        alive = false;
                    }
                }

                if (alive)
                {
                    misses = 0;
                    continue;
                }
                if (_clock.GetUtcNow() - readyAt < policy.StartupGrace) continue;
                misses++;
                LogLivenessMissed(_logger, _options.Name, generation, misses, policy.FailureThreshold);
                if (misses < policy.FailureThreshold) continue;

                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (generation != _generation || State != ProcessState.Running || !ReferenceEquals(_process, process)) return;
                    FailRun(generation, $"stopped answering: {misses} liveness checks missed in a row");
                    await StopCurrentAsync(graceful: true).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseGate();
                }
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // A stop ended this process's watchers.
        }
        catch (InvalidOperationException)
        {
            // The process was released (replaced or stopped) between two checks.
        }
    }

    // The process is killed next; its watcher sees the exit and applies the restart policy with this reason (a run that
    // failed this way is never a permanent exit — the exit code is the kill's, not the child's).
    private void FailRun(int generation, string reason)
    {
        if (generation != _generation) return;
        _runFailure = reason;
        LastError = reason;
    }

    private async Task PumpStderrAsync(Process process, int generation)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;
                lock (_tail)
                {
                    if (generation != Volatile.Read(ref _generation)) continue;
                    _tail.Enqueue(line);
                    while (_tail.Count > _options.StderrTailLines) _tail.Dequeue();
                }
                LogStderr(_logger, _options.Name, generation, line);
                StderrLine?.Invoke(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            LogStderrEnded(_logger, ex, _options.Name, generation);
        }
    }

    private async Task WatchExitAsync(Process process, int generation, CancellationToken lifetime)
    {
        try
        {
            await process.WaitForExitAsync(lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A stop cancelled the wait and owns the process's end; there is no restart to decide.
            return;
        }
        catch (InvalidOperationException)
        {
            // The handle was released by a kill before this watcher started waiting; the kill recorded the exit code.
        }

        int code;
        try
        {
            code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = _exitCodeAtKill ?? -1;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (generation != _generation || State == ProcessState.Stopped) return;

            if (ReferenceEquals(_process, process))
            {
                _process = null;
                Release(process);
            }

            UnexpectedExits++;
            var uptime = _clock.GetUtcNow() - _lastStart;
            if (uptime >= _options.Restart.StableAfter) _consecutiveFailures = 0;
            var permanent = _runFailure is null && _options.Restart.IsPermanentExit?.Invoke(code) == true;
            var delays = _options.Restart.Delays;
            var restart = !permanent && _consecutiveFailures < delays.Count;
            var cause = _runFailure ?? $"exited with code {code}";
            LogExited(_logger, _options.Name, generation, code, uptime, restart);
            var willRestart = restart;
            _pendingEvents.Add(() => Exited?.Invoke(code, willRestart));

            if (!restart)
            {
                GiveUp(permanent
                    ? $"{cause}, which a new start would not change"
                    : delays.Count == 0
                        ? cause
                        : $"{cause}; {_consecutiveFailures + 1} failed runs in a row");
                return;
            }

            var delay = delays[_consecutiveFailures];
            _consecutiveFailures++;
            SetState(ProcessState.Restarting);
            LastError = $"{cause}; starting again in {delay.TotalSeconds:0.#} s";
            _ = RestartAfterAsync(delay, generation, lifetime);
        }
        finally
        {
            ReleaseGate();
        }
    }

    private async Task RestartAfterAsync(TimeSpan delay, int afterGeneration, CancellationToken lifetime)
    {
        try
        {
            await Task.Delay(delay, _clock, lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping: the scheduled start is dropped.
            return;
        }
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_generation != afterGeneration || State != ProcessState.Restarting) return;
            await SpawnAsync(lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping while starting; the stop reports the final state.
        }
        finally
        {
            ReleaseGate();
        }
    }

    // Under the gate. Ends the current process, if any: the graceful step and the window's close when asked, then the
    // guardian's tree kill — and Process.Kill on the tree if a guardian configured not to force it left it running.
    private async Task StopCurrentAsync(bool graceful)
    {
        var p = _process;
        _process = null;
        if (p is null) return;
        try
        {
            var stop = _options.Stop;
            if (graceful && !p.HasExited && stop.GracefulStop is { } step)
            {
                using var budget = new CancellationTokenSource(stop.GracefulTimeout, _clock);
                try
                {
                    await step(p, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
                    await p.WaitForExitAsync(budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LogGracefulTimedOut(_logger, _options.Name, stop.GracefulTimeout);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LogGracefulFailed(_logger, ex, _options.Name);
                }
            }
            if (graceful && !p.HasExited && stop.CloseMainWindow)
            {
                using var budget = new CancellationTokenSource(stop.CloseMainWindowTimeout, _clock);
                try
                {
                    if (p.CloseMainWindow()) await p.WaitForExitAsync(budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LogGracefulTimedOut(_logger, _options.Name, stop.CloseMainWindowTimeout);
                }
            }
            if (!p.HasExited)
            {
                await _guardian.TerminateProcessAsync(p, TimeSpan.Zero).ConfigureAwait(false);
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            _exitCodeAtKill = p.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            LogKillFailed(_logger, ex, _options.Name);
        }
        finally
        {
            Release(p);
        }
    }

    private void Release(Process p)
    {
        _guardian.RemoveProcess(p);
        p.Dispose();
    }

    private void GiveUp(string reason)
    {
        LastError = reason;
        SetState(ProcessState.Failed);
        LogGaveUp(_logger, _options.Name, reason);
        _pendingEvents.Add(() => GaveUp?.Invoke(reason));
    }

    private void SetState(ProcessState state)
    {
        if (State == state) return;
        State = state;
        _pendingEvents.Add(() => StateChanged?.Invoke(state));
    }

    // Releases the gate, then raises what happened while it was held — outside the lock, so a handler can call back in.
    private void ReleaseGate()
    {
        Action[] events = [.. _pendingEvents];
        _pendingEvents.Clear();
        _gate.Release();
        foreach (var raise in events) raise();
    }

    /// <summary>Stops the current process and ends the supervisor. Disposes the guardian only if it created it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 1) return;
        await StopCoreAsync().ConfigureAwait(false);
        _disposed = true;
        _lifetime.Dispose();
        if (_ownsGuardian) await _guardian.DisposeAsync().ConfigureAwait(false);
        // The gate is deliberately not disposed: a StopAsync or RestartAsync that passed its disposed check just before
        // this method set it may still be waiting on it or about to release it. A SemaphoreSlim whose AvailableWaitHandle
        // was never read owns no handle, so nothing leaks.
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Name}] not started — there is no command to run")]
    private static partial void LogNoCommand(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "[{Name}] could not start {FileName}")]
    private static partial void LogStartFailed(ILogger logger, Exception ex, string name, string fileName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Name}] started pid {Pid} (generation {Generation})")]
    private static partial void LogStarted(ILogger logger, string name, int pid, int generation);

    [LoggerMessage(Level = LogLevel.Error, Message = "[{Name}] the new process did not become ready")]
    private static partial void LogReadyFailed(ILogger logger, Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Name}#{Generation}] {Line}")]
    private static partial void LogStderr(ILogger logger, string name, int generation, string line);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[{Name}#{Generation}] standard error closed")]
    private static partial void LogStderrEnded(ILogger logger, Exception ex, string name, int generation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Name}#{Generation}] exited with code {Code} after {Uptime}; starting again: {Restart}")]
    private static partial void LogExited(ILogger logger, string name, int generation, int code, TimeSpan uptime, bool restart);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Name}#{Generation}] liveness check missed ({Misses}/{Threshold} in a row)")]
    private static partial void LogLivenessMissed(ILogger logger, string name, int generation, int misses, int threshold);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Name}#{Generation}] the liveness check failed; counted as a miss")]
    private static partial void LogLivenessProbeFailed(ILogger logger, Exception ex, string name, int generation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Name}] gave up: {Reason}")]
    private static partial void LogGaveUp(ILogger logger, string name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Name}] did not exit within {Timeout} of being asked; going on to the next step")]
    private static partial void LogGracefulTimedOut(ILogger logger, string name, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Name}] the graceful stop step failed; going on to the next step")]
    private static partial void LogGracefulFailed(ILogger logger, Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{Name}] could not end the process")]
    private static partial void LogKillFailed(ILogger logger, Exception ex, string name);
}
