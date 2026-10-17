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
        var gaveUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("exit", "3")));
        supervisor.Exited += (code, restart) => exits.Enqueue((code, restart));
        supervisor.GaveUp += reason => gaveUp.TrySetResult(reason);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var reason = await gaveUp.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal([(3, true), (3, true), (3, false)], exits.ToArray());
        Assert.Equal(ProcessState.Failed, supervisor.State);
        Assert.Equal(3, supervisor.UnexpectedExits);
        Assert.Equal(2, supervisor.Restarts);
        Assert.Contains("exited with code 3", reason, StringComparison.Ordinal);
        Assert.Equal(reason, supervisor.LastError);
    }

    [Fact]
    public async Task A_permanent_exit_code_is_not_retried()
    {
        var gaveUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("exit", "7"),
            o => o.Restart.IsPermanentExit = code => code == 7));
        supervisor.GaveUp += reason => gaveUp.TrySetResult(reason);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var reason = await gaveUp.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(0, supervisor.Restarts);
        Assert.Contains("would not change", reason, StringComparison.Ordinal);
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
        supervisor.Exited += (_, restart) => { if (restart) Interlocked.Increment(ref restarts); };

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        // One delay allows one restart in a row; three restarts mean each long run reset the count.
        await Eventually(() => Volatile.Read(ref restarts) >= 3);

        Assert.NotEqual(ProcessState.Failed, supervisor.State);
    }

    [Fact]
    public async Task A_ready_hook_that_throws_kills_the_child_and_counts_as_a_failed_run()
    {
        var started = new ConcurrentQueue<Process>();
        var gaveUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        Assert.Equal("not ready: boom", reason);
        Assert.Equal(ProcessState.Failed, supervisor.State);
        Assert.Null(supervisor.Pid);
    }

    [Fact]
    public async Task A_ready_hook_that_never_returns_times_out()
    {
        var gaveUp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor(Options(() => Child("echo"), o =>
        {
            o.Restart.Delays = [];
            o.ReadyTimeout = TimeSpan.FromMilliseconds(300);
            o.Ready = (_, _) => new TaskCompletionSource().Task; // ignores its token on purpose
        }));
        supervisor.GaveUp += reason => gaveUp.TrySetResult(reason);

        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        var reason = await gaveUp.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal("not ready within 0.3 s", reason);
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
        supervisor.Exited += (code, _) => exited.TrySetResult(code);
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

        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Failed, supervisor.State);
        Assert.Equal("there is no command to run", supervisor.LastError);
    }

    [Fact]
    public async Task A_program_that_cannot_start_fails_with_the_reason()
    {
        await using var supervisor = new ProcessSupervisor(Options(() =>
            new SupervisedCommand(Path.Combine(AppContext.BaseDirectory, "no-such-program"), [], SupervisedCommand.SafeEnvironment())));

        await supervisor.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProcessState.Failed, supervisor.State);
        Assert.StartsWith("could not start ", supervisor.LastError, StringComparison.Ordinal);
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
