namespace DesktopHost.Processes;

/// <summary>What made a <see cref="ProcessSupervisor"/> stop starting its child.</summary>
public enum GiveUpKind
{
    /// <summary><see cref="ProcessSupervisorOptions.Command"/> answered no command to run.</summary>
    NoCommand,

    /// <summary>The operating system could not start the command (a missing file, no permission).</summary>
    StartFailed,

    /// <summary>
    /// The policy says a new start would end the same way: the child exited with a code <see cref="RestartPolicy.IsPermanentExit"/>
    /// calls permanent, or it never became ready and <see cref="RestartPolicy.RestartAfterNotReady"/> is off
    /// (<see cref="GiveUpReason.LastRunFailure"/> tells the two apart).
    /// </summary>
    Permanent,

    /// <summary>The child's runs failed more times in a row than <see cref="RestartPolicy.Delays"/> allows starts.</summary>
    RestartLimit,
}

/// <summary>How the supervisor judged a run failed while its process still ran — the supervisor then stopped it.</summary>
public enum RunFailure
{
    /// <summary>The run ended on its own (the process exited).</summary>
    None,

    /// <summary>The child was not ready: <see cref="ProcessSupervisorOptions.Ready"/> failed or <see cref="ProcessSupervisorOptions.ReadyTimeout"/> passed.</summary>
    NotReady,

    /// <summary>The child stopped answering <see cref="ProcessSupervisorOptions.Liveness"/>'s checks.</summary>
    Unresponsive,
}

/// <summary>
/// Why the supervisor gave up, as a value a caller can act on: the kind, the last run's exit code and how that run failed, and a
/// sentence for a log (also <see cref="ProcessSupervisor.LastError"/>). <see cref="ToString"/> is the sentence.
/// </summary>
/// <param name="Kind">What made the supervisor stop.</param>
/// <param name="Message">The reason in one sentence.</param>
/// <param name="ExitCode">The last run's exit code; null when no run ended (<see cref="GiveUpKind.NoCommand"/>, <see cref="GiveUpKind.StartFailed"/>).</param>
/// <param name="LastRunFailure">How the last run failed while it ran, or <see cref="RunFailure.None"/> when it exited on its own.</param>
public sealed record GiveUpReason(GiveUpKind Kind, string Message, int? ExitCode = null, RunFailure LastRunFailure = RunFailure.None)
{
    /// <inheritdoc />
    public override string ToString() => Message;
}

/// <summary>
/// How one run of the child ended, for a caller that reports it (a status, a crash record): raised by
/// <see cref="ProcessSupervisor.Exited"/> after each exit the supervisor did not ask for.
/// </summary>
/// <param name="Pid">The process id the run had.</param>
/// <param name="ExitCode">Its exit code — when the supervisor ended the run, the code the kill left.</param>
/// <param name="WillRestart">Whether a new start is scheduled.</param>
/// <param name="Failure">How the supervisor judged the run failed while it ran (and then ended it), or <see cref="RunFailure.None"/> when it exited on its own.</param>
/// <param name="StopStage">The step that ended it when the supervisor did (<see cref="Failure"/> is not <see cref="RunFailure.None"/>); <c>null</c> otherwise.</param>
/// <param name="Message">The cause in one sentence.</param>
public sealed record ExitReport(int Pid, int ExitCode, bool WillRestart, RunFailure Failure, StopStage? StopStage, string Message)
{
    /// <inheritdoc />
    public override string ToString() => Message;
}
