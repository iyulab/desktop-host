using System.Collections.Concurrent;
using System.Diagnostics;
using ChildProcessGuard;

namespace DesktopHost.Processes.Tests;

public sealed class ProcessSupervisorTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private static SupervisedCommand Child(params string[] args) => Child(SupervisedCommand.SafeEnvironment(), args);

    private static SupervisedCommand Child(Dictionary<string, string> env, params string[] args)
    {
        var exe = Path.Combine(AppContext.BaseDirectory,
            "DesktopHost.Processes.TestChild" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return new SupervisedCommand(exe, args, env);
    }

    private static ProcessSupervisorOptions Options(Func<SupervisedCommand?> command, Action<ProcessSupervisorOptions>? configure = null)
    {
        var options = new ProcessSupervisorOptions
        {
            Name = "test-child",
            Command = command,
            Restart = new RestartPolicy { Delays = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)] },
        };
        configure?.Invoke(options);
        return options;
    }

    // A Ready hook that waits for the child's "ready" line and keeps the process for the test.
    private static Func<Process, CancellationToken, Task> ReadyLine(ConcurrentQueue<Process> started) => async (p, ct) =>
    {
        started.Enqueue(p);
        var line = await p.StandardOutput.ReadLineAsync(ct);
        Assert.Equal("ready", line);
    };

    private static bool IsGone(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("condition not met in time");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_started_child_is_running_once_ready_returns_and_its_input_and_output_are_the_hooks_to_use()
    {
        var started = new ConcurrentQueue<Process>();
        await using var supervisor = new ProcessSupervisor(Options(() => Child("ready", "echo"), o => o.Ready = ReadyLine(started)));

        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Running, supervisor.State);
        Assert.NotNull(supervisor.Pid);
        Assert.True(started.TryPeek(out var p));
        await p.StandardInput.WriteLineAsync("hello");
        await p.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal("hello", await p.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_child_sees_only_the_environment_its_command_names()
    {
        const string name = "DESKTOPHOST_TEST_SECRET";
        Environment.SetEnvironmentVariable(name, "from-the-parent");
        try
        {
            var lines = new ConcurrentQueue<string>();
            Task Read(Process p, CancellationToken ct) => Task.Run(async () => lines.Enqueue((await p.StandardOutput.ReadLineAsync(ct))!), ct);

            await using (var plain = new ProcessSupervisor(Options(() => Child("env", name, "echo"), o => o.Ready = Read)))
                await plain.StartAsync(TestContext.Current.CancellationToken);

            var env = SupervisedCommand.SafeEnvironment();
            env[name] = "named";
            await using (var named = new ProcessSupervisor(Options(() => Child(env, "env", name, "echo"), o => o.Ready = Read)))
                await named.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal(["<unset>", "named"], lines.ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task A_child_that_honours_the_graceful_step_is_not_killed()
    {
        var started = new ConcurrentQueue<Process>();
        var asked = false;
        await using var supervisor = new ProcessSupervisor(Options(() => Child("ready", "echo"), o =>
        {
            o.Ready = ReadyLine(started);
            o.Stop.GracefulTimeout = TimeSpan.FromSeconds(30);
            o.Stop.GracefulStop = async (p, ct) =>
            {
                asked = true;
                await p.StandardInput.WriteLineAsync("stop".AsMemory(), ct);
                await p.StandardInput.FlushAsync(ct);
            };
        }));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var pid = supervisor.Pid!.Value;

        var clock = Stopwatch.StartNew();
        await supervisor.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(asked);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"stop took {clock.Elapsed}");
        Assert.True(IsGone(pid));
        Assert.Equal(ProcessState.Stopped, supervisor.State);
        Assert.Equal(0, supervisor.UnexpectedExits);
    }

    [Fact]
    public async Task Stop_names_the_step_that_ended_the_child()
    {
        var started = new ConcurrentQueue<Process>();

        // A child that honours the graceful step: Graceful.
        await using (var graceful = new ProcessSupervisor(Options(() => Child("ready", "echo"), o =>
        {
            o.Ready = ReadyLine(started);
            o.Stop.GracefulTimeout = TimeSpan.FromSeconds(30);
            o.Stop.GracefulStop = async (p, ct) =>
            {
                await p.StandardInput.WriteLineAsync("stop".AsMemory(), ct);
                await p.StandardInput.FlushAsync(ct);
            };
        })))
        {
            await graceful.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(StopStage.Graceful, await graceful.StopAsync(TestContext.Current.CancellationToken));
            Assert.Equal(StopStage.NotRunning, await graceful.StopAsync(TestContext.Current.CancellationToken));
        }

        // A child with no graceful step is ended by force: Killed.
        await using (var forced = new ProcessSupervisor(Options(() => Child("ready", "sleep"), o => o.Ready = ReadyLine(started))))
        {
            await forced.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(StopStage.Killed, await forced.StopAsync(TestContext.Current.CancellationToken));
        }

        // A child that ignores the graceful step within its time is ended by force too.
        await using (var stubborn = new ProcessSupervisor(Options(() => Child("ready", "ignore-stop"), o =>
        {
            o.Ready = ReadyLine(started);
            o.Stop.GracefulTimeout = TimeSpan.FromMilliseconds(300);
            o.Stop.GracefulStop = async (p, ct) =>
            {
                await p.StandardInput.WriteLineAsync("stop".AsMemory(), ct);
                await p.StandardInput.FlushAsync(ct);
            };
        })))
        {
            await stubborn.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(StopStage.Killed, await stubborn.StopAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task A_child_that_ignores_the_graceful_step_is_killed_when_its_time_is_up()
    {
        var started = new ConcurrentQueue<Process>();
        await using var supervisor = new ProcessSupervisor(Options(() => Child("ready", "ignore-stop"), o =>
        {
            o.Ready = ReadyLine(started);
            o.Stop.GracefulTimeout = TimeSpan.FromMilliseconds(500);
            o.Stop.GracefulStop = async (p, ct) =>
            {
                await p.StandardInput.WriteLineAsync("stop".AsMemory(), ct);
                await p.StandardInput.FlushAsync(ct);
            };
        }));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var pid = supervisor.Pid!.Value;

        var clock = Stopwatch.StartNew();
        await supervisor.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(450), $"stop took {clock.Elapsed}");
        Assert.True(IsGone(pid));
    }

    [Fact]
    public async Task Stopping_kills_the_whole_tree()
    {
        var grandchild = 0;
        await using var supervisor = new ProcessSupervisor(Options(() => Child("spawn-grandchild", "ready", "echo"), o =>
            o.Ready = async (p, ct) =>
            {
                grandchild = int.Parse((await p.StandardOutput.ReadLineAsync(ct))!, System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal("ready", await p.StandardOutput.ReadLineAsync(ct));
            }));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(IsGone(grandchild));

        await supervisor.StopAsync(TestContext.Current.CancellationToken);

        await Eventually(() => IsGone(grandchild));
    }

    [Fact]
    public async Task Unexpected_exits_start_the_child_again_until_the_delays_run_out()
    {
        var exits = new ConcurrentQueue<(int Code, bool WillRestart)>();
        var gaveUp = new TaskCompletionSource<GiveUpReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("exit", "3")));
        supervisor.Exited += r => exits.Enqueue((r.ExitCode, r.WillRestart));
        supervisor.GaveUp += reason => gaveUp.TrySetResult(reason);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var reason = await gaveUp.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal([(3, true), (3, true), (3, false)], exits.ToArray());
        Assert.Equal(ProcessState.Failed, supervisor.State);
        Assert.Equal(3, supervisor.UnexpectedExits);
        Assert.Equal(2, supervisor.Restarts);
        Assert.Contains("exited with code 3", reason.Message, StringComparison.Ordinal);
        Assert.Equal(reason.Message, supervisor.LastError);
        Assert.Equal(new GiveUpReason(GiveUpKind.RestartLimit, reason.Message, 3, RunFailure.None), reason);
    }

    // A probe that answers from a switch the test flips: alive while it is on, a miss while it is off.
    private sealed class Switch
    {
        public volatile bool Alive = true;
        public int Calls;
        public Func<Process, CancellationToken, Task<bool>> Probe => (_, _) =>
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Alive);
        };
    }

    private static LivenessPolicy Liveness(Func<Process, CancellationToken, Task<bool>> probe, int threshold = 3) => new()
    {
        Probe = probe,
        Period = TimeSpan.FromMilliseconds(40),
        Timeout = TimeSpan.FromMilliseconds(500),
        FailureThreshold = threshold,
        StartupGrace = TimeSpan.Zero,
    };

    [Fact]
    public async Task A_child_that_stops_answering_its_liveness_check_is_restarted_and_the_reason_names_the_check()
    {
        var probe = new Switch();
        var reasons = new ConcurrentQueue<string>();
        await using var supervisor = new ProcessSupervisor(Options(() => Child("ready", "echo"), o =>
        {
            o.Ready = ReadyLine(new ConcurrentQueue<Process>());
            o.Liveness = Liveness(probe.Probe);
            o.Restart.RestartAfterNotReady = false; // concerns a run that was never ready, not one that stopped answering
        }));
        supervisor.StateChanged += s => { if (s == ProcessState.Restarting) reasons.Enqueue(supervisor.LastError ?? ""); };

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var first = supervisor.Pid!.Value;
        probe.Alive = false;

        await Eventually(() => !reasons.IsEmpty);
        probe.Alive = true;
        await Eventually(() => supervisor.State == ProcessState.Running && supervisor.Pid is { } pid && pid != first);

        Assert.True(IsGone(first));
        Assert.Equal(1, supervisor.UnexpectedExits);
        Assert.True(reasons.TryPeek(out var reason));
        Assert.Contains("3 liveness checks missed in a row", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_child_that_stops_answering_with_no_starts_left_gives_up_as_unresponsive()
    {
        var probe = new Switch();
        var gaveUp = new TaskCompletionSource<GiveUpReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("ready", "echo"), o =>
        {
            o.Ready = ReadyLine(new ConcurrentQueue<Process>());
            o.Liveness = Liveness(probe.Probe);
            o.Restart.Delays = [];
        }));
        supervisor.GaveUp += reason => gaveUp.TrySetResult(reason);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        probe.Alive = false;
        var reason = await gaveUp.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((GiveUpKind.RestartLimit, RunFailure.Unresponsive), (reason.Kind, reason.LastRunFailure));
        Assert.Contains("liveness checks missed in a row", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_between_misses_starts_the_count_over()
    {
        var calls = 0;
        // miss, miss, answer, miss, miss, answer … — never three in a row.
        Task<bool> Probe(Process _, CancellationToken __) => Task.FromResult(Interlocked.Increment(ref calls) % 3 == 0);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("ready", "echo"), o =>
        {
            o.Ready = ReadyLine(new ConcurrentQueue<Process>());
            o.Liveness = Liveness(Probe);
        }));

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var pid = supervisor.Pid;
        await Eventually(() => Volatile.Read(ref calls) >= 15);

        Assert.Equal(ProcessState.Running, supervisor.State);
        Assert.Equal(pid, supervisor.Pid);
        Assert.Equal(0, supervisor.UnexpectedExits);
    }

    [Fact]
    public async Task A_check_that_does_not_answer_in_time_is_a_miss()
    {
        var reasons = new ConcurrentQueue<string>();
        await using var supervisor = new ProcessSupervisor(Options(() => Child("ready", "echo"), o =>
        {
            o.Ready = ReadyLine(new ConcurrentQueue<Process>());
            o.Liveness = Liveness(async (_, ct) => { await Task.Delay(System.Threading.Timeout.Infinite, ct); return true; });
            o.Liveness.Timeout = TimeSpan.FromMilliseconds(60);
        }));
        supervisor.StateChanged += s => { if (s == ProcessState.Restarting) reasons.Enqueue(supervisor.LastError ?? ""); };

        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        await Eventually(() => !reasons.IsEmpty);
        Assert.True(reasons.TryPeek(out var reason));
        Assert.Contains("liveness checks missed in a row", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Misses_inside_the_startup_grace_are_not_counted()
    {
        var probe = new Switch { Alive = false };
        await using var supervisor = new ProcessSupervisor(Options(() => Child("ready", "echo"), o =>
        {
            o.Ready = ReadyLine(new ConcurrentQueue<Process>());
            o.Liveness = Liveness(probe.Probe);
            o.Liveness.StartupGrace = TimeSpan.FromSeconds(30);
        }));

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await Eventually(() => Volatile.Read(ref probe.Calls) >= 8);

        Assert.Equal(ProcessState.Running, supervisor.State);
        Assert.Equal(0, supervisor.UnexpectedExits);
    }

    [Fact]
    public async Task No_check_runs_after_a_stop()
    {
        var probe = new Switch();
        await using var supervisor = new ProcessSupervisor(Options(() => Child("ready", "echo"), o =>
        {
            o.Ready = ReadyLine(new ConcurrentQueue<Process>());
            o.Liveness = Liveness(probe.Probe);
        }));

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await Eventually(() => Volatile.Read(ref probe.Calls) >= 2);
        await supervisor.StopAsync(TestContext.Current.CancellationToken);
        var after = Volatile.Read(ref probe.Calls);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.InRange(Volatile.Read(ref probe.Calls), after, after + 1);
    }

    [Fact]
    public void A_liveness_policy_out_of_range_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessSupervisor(Options(() => Child(), o =>
            o.Liveness = new LivenessPolicy { Probe = (_, _) => Task.FromResult(true), FailureThreshold = 0 })));
        Assert.Throws<ArgumentException>(() => new ProcessSupervisor(Options(() => Child(), o => o.Liveness = new LivenessPolicy())));
    }

    [Fact]
    public async Task A_permanent_exit_code_is_not_retried()
    {
        var gaveUp = new TaskCompletionSource<GiveUpReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("exit", "7"),
            o => o.Restart.IsPermanentExit = code => code == 7));
        supervisor.GaveUp += reason => gaveUp.TrySetResult(reason);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var reason = await gaveUp.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(0, supervisor.Restarts);
        Assert.Contains("would not change", reason.Message, StringComparison.Ordinal);
        Assert.Equal(GiveUpKind.Permanent, reason.Kind);
        Assert.Equal(RunFailure.None, reason.LastRunFailure);
    }

    [Fact]
    public void The_unhandled_managed_exception_code_is_named()
    {
        Assert.True(ExitCodes.IsUnhandledManagedException(unchecked((int)0xE0434352)));
        Assert.False(ExitCodes.IsUnhandledManagedException(1));
    }

    [Fact]
    public async Task A_run_that_lasted_past_the_stable_window_starts_the_count_over()
    {
        var restarts = 0;
        await using var supervisor = new ProcessSupervisor(Options(() => Child("sleep", "600", "exit", "1"), o =>
        {
            o.Restart.Delays = [TimeSpan.FromMilliseconds(20)];
            o.Restart.StableAfter = TimeSpan.FromMilliseconds(200);
        }));
        supervisor.Exited += r => { if (r.WillRestart) Interlocked.Increment(ref restarts); };

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        // One delay allows one restart in a row; three restarts mean each long run reset the count.
        await Eventually(() => Volatile.Read(ref restarts) >= 3);

        Assert.NotEqual(ProcessState.Failed, supervisor.State);
    }

    [Fact]
    public async Task A_ready_hook_that_throws_kills_the_child_and_counts_as_a_failed_run()
    {
        var started = new ConcurrentQueue<Process>();
        var gaveUp = new TaskCompletionSource<GiveUpReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("echo"), o =>
        {
            o.Restart.Delays = [];
            o.Ready = (p, _) =>
            {
                started.Enqueue(p);
                throw new InvalidOperationException("boom");
            };
        }));
        supervisor.GaveUp += reason => gaveUp.TrySetResult(reason);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var reason = await gaveUp.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal("not ready: boom", reason.Message);
        Assert.Equal((GiveUpKind.RestartLimit, RunFailure.NotReady), (reason.Kind, reason.LastRunFailure));
        Assert.Equal(ProcessState.Failed, supervisor.State);
        Assert.Null(supervisor.Pid);
    }

    [Fact]
    public async Task A_run_that_never_became_ready_is_not_started_again_when_the_policy_says_so()
    {
        var gaveUp = new TaskCompletionSource<GiveUpReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("echo"), o =>
        {
            o.Restart.RestartAfterNotReady = false; // the delays below would otherwise start it twice more
            o.ReadyTimeout = TimeSpan.FromMilliseconds(300);
            o.Ready = (_, _) => new TaskCompletionSource().Task;
        }));
        supervisor.GaveUp += reason => gaveUp.TrySetResult(reason);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var reason = await gaveUp.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((GiveUpKind.Permanent, RunFailure.NotReady), (reason.Kind, reason.LastRunFailure));
        Assert.Equal(0, supervisor.Restarts);
        Assert.Equal(ProcessState.Failed, supervisor.State);
    }

    [Fact]
    public async Task A_ready_hook_that_never_returns_times_out()
    {
        var gaveUp = new TaskCompletionSource<GiveUpReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("echo"), o =>
        {
            o.Restart.Delays = [];
            o.ReadyTimeout = TimeSpan.FromMilliseconds(300);
            o.Ready = (_, _) => new TaskCompletionSource().Task; // ignores its token on purpose
        }));
        supervisor.GaveUp += reason => gaveUp.TrySetResult(reason);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var reason = await gaveUp.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal("not ready within 0.3 s", reason.Message);
        Assert.Equal((GiveUpKind.RestartLimit, RunFailure.NotReady), (reason.Kind, reason.LastRunFailure));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_child_that_exits_while_it_is_awaited_is_judged_by_its_exit_code_not_as_not_ready(bool permanentCode)
    {
        var gaveUp = new TaskCompletionSource<GiveUpReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new ConcurrentQueue<ExitReport>();
        await using var supervisor = new ProcessSupervisor(Options(() => Child("exit", "7"), o =>
        {
            o.Restart.Delays = [];
            o.Restart.RestartAfterNotReady = false; // a not-ready verdict would be Permanent + NotReady
            if (permanentCode) o.Restart.IsPermanentExit = code => code == 7;
            o.ReadyTimeout = null; // no limit: only the exit can end this wait
            o.Ready = (_, _) => new TaskCompletionSource().Task; // ignores its token on purpose
        }));
        supervisor.GaveUp += reason => gaveUp.TrySetResult(reason);
        supervisor.Exited += reports.Enqueue;

        await supervisor.StartAsync(TestContext.Current.CancellationToken).WaitAsync(Patience, TestContext.Current.CancellationToken);
        var reason = await gaveUp.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(permanentCode ? GiveUpKind.Permanent : GiveUpKind.RestartLimit, reason.Kind);
        Assert.Equal((7, RunFailure.None), (reason.ExitCode, reason.LastRunFailure));
        Assert.True(reports.TryDequeue(out var report));
        Assert.Equal((7, false, RunFailure.None, (StopStage?)null), (report.ExitCode, report.WillRestart, report.Failure, report.StopStage));
    }

    [Fact]
    public async Task A_run_the_supervisor_ended_is_reported_with_its_failure_and_the_step_that_ended_it()
    {
        var probe = new Switch();
        var report = new TaskCompletionSource<ExitReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("ready", "ignore-stop"), o =>
        {
            o.Ready = ReadyLine(new ConcurrentQueue<Process>());
            o.Liveness = Liveness(probe.Probe);
            o.Restart.Delays = [];
        }));
        supervisor.Exited += r => report.TrySetResult(r);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var pid = supervisor.Pid!.Value;
        probe.Alive = false;
        var r = await report.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(pid, r.Pid);
        Assert.Equal((RunFailure.Unresponsive, StopStage.Killed, false), (r.Failure, r.StopStage, r.WillRestart));
        Assert.Contains("liveness checks missed", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_is_decoded_with_the_encoding_the_options_name()
    {
        var lines = new ConcurrentQueue<string>();
        // The child writes UTF-8; read as Latin-1, «é» (C3 A9) arrives as two characters — proof the option reached the pipe.
        await using var supervisor = new ProcessSupervisor(Options(() => Child("stderr", "caf\u00e9", "echo"),
            o => o.OutputEncoding = System.Text.Encoding.Latin1));
        supervisor.StderrLine += lines.Enqueue;

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await Eventually(() => !lines.IsEmpty);

        Assert.Equal("caf\u00c3\u00a9", lines.Single());
    }

    [Fact]
    public async Task Starting_is_raised_while_the_ready_hook_still_waits_and_a_second_start_waits_for_the_first()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentQueue<ProcessState>();
        await using var supervisor = new ProcessSupervisor(Options(() => Child("echo"), o => o.Ready = (_, ct) => release.Task.WaitAsync(ct)));
        supervisor.StateChanged += s =>
        {
            states.Enqueue(s);
            if (s == ProcessState.Starting) starting.TrySetResult();
        };

        var first = supervisor.StartAsync(TestContext.Current.CancellationToken);
        await starting.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var second = supervisor.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(ProcessState.Starting, supervisor.State);

        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Running, supervisor.State);
        Assert.Equal([ProcessState.Starting, ProcessState.Running], states.ToArray());
        Assert.Equal(0, supervisor.Restarts);
    }

    [Fact]
    public async Task A_stop_while_the_ready_hook_waits_ends_the_child_at_once()
    {
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("echo"), o =>
        {
            o.ReadyTimeout = null;
            o.Ready = (_, _) => new TaskCompletionSource().Task; // ignores its token on purpose
        }));
        supervisor.StateChanged += s => { if (s == ProcessState.Starting) starting.TrySetResult(); };

        var start = supervisor.StartAsync(TestContext.Current.CancellationToken);
        await starting.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var pid = supervisor.Pid!.Value;

        var stage = await supervisor.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Patience, TestContext.Current.CancellationToken);
        await start.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(StopStage.Killed, stage);
        Assert.Equal(ProcessState.Stopped, supervisor.State);
        Assert.True(IsGone(pid));
    }

    [Fact]
    public async Task A_restart_on_request_replaces_the_child_without_counting_an_unexpected_exit()
    {
        var started = new ConcurrentQueue<Process>();
        await using var supervisor = new ProcessSupervisor(Options(() => Child("ready", "echo"), o => o.Ready = ReadyLine(started)));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var first = supervisor.Pid!.Value;

        await supervisor.RestartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Running, supervisor.State);
        Assert.NotEqual(first, supervisor.Pid);
        Assert.True(IsGone(first));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(0, supervisor.UnexpectedExits);
        Assert.Equal(1, supervisor.Restarts);
    }

    [Fact]
    public async Task After_a_stop_a_new_start_is_watched_like_the_first()
    {
        var args = new[] { "ready", "echo" };
        var started = new ConcurrentQueue<Process>();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child(args), o => o.Ready = ReadyLine(started)));
        supervisor.Exited += r => exited.TrySetResult(r.ExitCode);
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await supervisor.StopAsync(TestContext.Current.CancellationToken);

        args = ["ready", "sleep", "100", "exit", "5"];
        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, await exited.Task.WaitAsync(Patience, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task No_command_fails_with_the_reason()
    {
        await using var supervisor = new ProcessSupervisor(Options(() => null));
        GiveUpReason? reason = null;
        supervisor.GaveUp += r => reason = r;

        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Failed, supervisor.State);
        Assert.Equal("there is no command to run", supervisor.LastError);
        Assert.Equal(new GiveUpReason(GiveUpKind.NoCommand, "there is no command to run"), reason);
    }

    [Fact]
    public async Task A_program_that_cannot_start_fails_with_the_reason()
    {
        await using var supervisor = new ProcessSupervisor(Options(() =>
            new SupervisedCommand(Path.Combine(AppContext.BaseDirectory, "no-such-program"), [], SupervisedCommand.SafeEnvironment())));
        GiveUpReason? reason = null;
        supervisor.GaveUp += r => reason = r;

        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Failed, supervisor.State);
        Assert.StartsWith("could not start ", supervisor.LastError, StringComparison.Ordinal);
        Assert.Equal(GiveUpKind.StartFailed, reason?.Kind);
        Assert.Null(reason?.ExitCode);
    }

    [Fact]
    public async Task Standard_error_lines_are_raised_and_the_last_ones_kept()
    {
        var lines = new ConcurrentQueue<string>();
        await using var supervisor = new ProcessSupervisor(Options(() => Child("stderr", "a", "stderr", "b", "stderr", "c", "echo"),
            o => o.StderrTailLines = 2));
        supervisor.StderrLine += lines.Enqueue;

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await Eventually(() => lines.Count == 3);

        Assert.Equal(["a", "b", "c"], lines.ToArray());
        Assert.Equal(["b", "c"], supervisor.StderrTail);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    [Theory]
    [InlineData(Microsoft.Extensions.Logging.LogLevel.Debug)]
    [InlineData(Microsoft.Extensions.Logging.LogLevel.None)]
    public async Task Standard_error_lines_are_logged_at_the_level_the_options_name(Microsoft.Extensions.Logging.LogLevel level)
    {
        var logger = new CapturingLogger();
        var lines = new ConcurrentQueue<string>();
        await using var supervisor = new ProcessSupervisor(Options(() => Child("stderr", "chatty", "echo"), o =>
        {
            o.Logger = logger;
            o.StderrLogLevel = level;
        }));
        supervisor.StderrLine += lines.Enqueue;

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        await Eventually(() => !lines.IsEmpty);

        var logged = logger.Entries.Where(e => e.Text.EndsWith("chatty", StringComparison.Ordinal)).ToArray();
        if (level == Microsoft.Extensions.Logging.LogLevel.None) Assert.Empty(logged);
        else Assert.Equal(level, Assert.Single(logged).Level);
    }

    [Fact]
    public async Task A_handler_may_call_back_into_the_supervisor()
    {
        var stoppedFromHandler = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("exit", "2"), o => o.Restart.Delays = []));
        supervisor.GaveUp += _ => stoppedFromHandler.TrySetResult(supervisor.StopAsync().Wait(Patience));

        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(await stoppedFromHandler.Task.WaitAsync(Patience, TestContext.Current.CancellationToken));
        Assert.Equal(ProcessState.Stopped, supervisor.State);
    }

    [Fact]
    public async Task Dispose_and_stop_may_race_and_a_stop_after_dispose_does_nothing()
    {
        var started = new ConcurrentQueue<Process>();
        var supervisor = new ProcessSupervisor(Options(() => Child("ready", "echo"), o => o.Ready = ReadyLine(started)));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var pid = supervisor.Pid!.Value;

        await Task.WhenAll(supervisor.StopAsync(), supervisor.DisposeAsync().AsTask(), supervisor.StopAsync());
        await supervisor.StopAsync();

        Assert.True(IsGone(pid));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => supervisor.StartAsync());
    }

    private static void KillQuietly(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disposing_the_supervisor_leaves_the_child_only_when_it_was_asked_to_outlive_it(bool outlives)
    {
        var started = new ConcurrentQueue<Process>();
        var supervisor = new ProcessSupervisor(Options(() => Child("ready", "sleep", "600000"), o =>
        {
            o.ChildOutlivesSupervisor = outlives;
            o.Ready = ReadyLine(started);
        }));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var pid = supervisor.Pid!.Value;
        try
        {
            await supervisor.DisposeAsync();

            if (outlives) Assert.False(IsGone(pid), "the child must still be running after the supervisor ended");
            else await Eventually(() => IsGone(pid));
        }
        finally
        {
            KillQuietly(pid);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_child_meant_to_outlive_its_supervisor_survives_the_starting_process_being_killed(bool outlives)
    {
        // The case ChildOutlivesSupervisor exists for, and the one disposing cannot show: the process that started the child is
        // killed outright (a crash, a task manager), with no dispose and no stop.
        if (!outlives && !OperatingSystem.IsWindows())
            Assert.Skip("only Windows' job object ends a child with its killed owner; elsewhere nothing does");
        var starter = Process.Start(new ProcessStartInfo(Child().FileName)
        {
            ArgumentList = { "supervise", outlives ? "true" : "false" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        int childPid;
        try
        {
            var line = await starter.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);
            childPid = int.Parse(line!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(IsGone(childPid));

            starter.Kill(entireProcessTree: false);
            await starter.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            if (!starter.HasExited) starter.Kill(entireProcessTree: true);
            starter.Dispose();
        }

        try
        {
            if (outlives)
            {
                await Task.Delay(500, TestContext.Current.CancellationToken);
                Assert.False(IsGone(childPid), "the child ended with its starter");
            }
            else
            {
                await Eventually(() => IsGone(childPid));
            }
        }
        finally
        {
            KillQuietly(childPid);
        }
    }

    [Fact]
    public async Task A_child_left_running_is_not_started_again_when_it_later_exits()
    {
        var starts = 0;
        var started = new ConcurrentQueue<Process>();
        var supervisor = new ProcessSupervisor(Options(() => { Interlocked.Increment(ref starts); return Child("ready", "sleep", "600000"); }, o =>
        {
            o.ChildOutlivesSupervisor = true;
            o.Ready = ReadyLine(started);
        }));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var pid = supervisor.Pid!.Value;
        try
        {
            await supervisor.DisposeAsync();
            Assert.Equal(ProcessState.Stopped, supervisor.State);
            Assert.Null(supervisor.Pid);

            KillQuietly(pid);
            await Eventually(() => IsGone(pid));
            await Task.Delay(400, TestContext.Current.CancellationToken); // longer than every restart delay of the test options

            Assert.Equal(1, starts);
        }
        finally
        {
            KillQuietly(pid);
        }
    }

    [Fact]
    public async Task Stopping_still_stops_a_child_that_may_outlive_the_supervisor()
    {
        var started = new ConcurrentQueue<Process>();
        await using var supervisor = new ProcessSupervisor(Options(() => Child("ready", "sleep", "600000"), o =>
        {
            o.ChildOutlivesSupervisor = true;
            o.Ready = ReadyLine(started);
        }));
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var pid = supervisor.Pid!.Value;
        try
        {
            await supervisor.StopAsync(TestContext.Current.CancellationToken);

            Assert.True(IsGone(pid));
        }
        finally
        {
            KillQuietly(pid);
        }
    }

    [Fact]
    public async Task A_guardian_that_kills_its_children_cannot_serve_a_child_meant_to_outlive_the_supervisor()
    {
        await using var killing = new ProcessGuardian();
        Assert.Throws<ArgumentException>(() => new ProcessSupervisor(Options(() => Child("ready"), o =>
        {
            o.Guardian = killing;
            o.ChildOutlivesSupervisor = true;
        })));

        await using var leaving = new ProcessGuardianBuilder().WithChildrenOutliveOwner().Build();
        await using var ok = new ProcessSupervisor(Options(() => Child("ready"), o =>
        {
            o.Guardian = leaving;
            o.ChildOutlivesSupervisor = true;
        }));
    }

    [Fact]
    public async Task A_guardian_that_was_passed_in_is_left_to_its_owner()
    {
        await using var guardian = new ProcessGuardian();
        var started = new ConcurrentQueue<Process>();
        await using (var supervisor = new ProcessSupervisor(Options(() => Child("ready", "echo"), o =>
        {
            o.Guardian = guardian;
            o.Ready = ReadyLine(started);
        })))
        {
            await supervisor.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, guardian.ManagedProcessCount);
        }

        Assert.Equal(0, guardian.ManagedProcessCount);
        await using var second = new ProcessSupervisor(Options(() => Child("ready", "echo"), o =>
        {
            o.Guardian = guardian;
            o.Ready = ReadyLine(started);
        }));
        await second.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProcessState.Running, second.State);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("name", false)]
    public void Options_without_a_name_or_a_command_are_refused(string name, bool withCommand)
    {
        var options = new ProcessSupervisorOptions { Name = name, Command = withCommand ? () => null : null };
        Assert.Throws<ArgumentException>(() => new ProcessSupervisor(options));
    }

    [Fact]
    public void A_command_line_splits_on_whitespace_and_keeps_quoted_segments()
    {
        Assert.Equal(["node", "C:/Program Files/x/run.js", "--flag"],
            SupervisedCommand.SplitCommandLine("node \"C:/Program Files/x/run.js\"  --flag"));
        Assert.Equal(["a", ""], SupervisedCommand.SplitCommandLine("a \"\""));
    }
}
