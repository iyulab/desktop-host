using System.Text.Json;

namespace DesktopHost.Files.Tests;

/// <summary>
/// The file-retry window's case table, shared with the Tauri kit's file crate (`tauri-kit` `crates/fs/tests/cases/
/// file-retry.json`): the two retry the same way — a refusal that ends within the window is outlasted, the last attempt is
/// made when the window closes, nothing waits past it — so both run the same cases with the same results. Keep
/// `cases/file-retry.json` identical to that file; the pauses are each implementation's own and the table does not depend
/// on them.
/// </summary>
public sealed class FileRetryWindowTests
{
    // The table holds whatever the pauses are; run it under linear steps that do not divide the windows as well as the
    // default, and under AtomicFile's swap schedule (doubling from 15 ms, capped at 500 ms) — through both the blocking
    // window and its awaited twin.
    private static readonly string[] Schedules = ["linear 7", "linear 300", "linear 1000", "swap"];

    public static TheoryData<string, string, bool> Cases()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var c in Table().GetProperty("cases").EnumerateArray())
            foreach (var schedule in Schedules)
                foreach (var awaited in new[] { false, true })
                    data.Add(c.GetProperty("name").GetString()!, schedule, awaited);
        return data;
    }

    private static JsonElement Table() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cases", "file-retry.json"))).RootElement;

    private static Func<int, TimeSpan> PauseAfter(string schedule) => schedule == "swap"
        ? AtomicFile.SwapPauseAfter
        : (FileRetryOptions.Default with { PauseStep = TimeSpan.FromMilliseconds(int.Parse(schedule["linear ".Length..], System.Globalization.CultureInfo.InvariantCulture)) }).PauseAfter;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_shared_file_retry_table(string name, string schedule, bool awaited)
    {
        var c = Table().GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var refused = TimeSpan.FromMilliseconds(c.GetProperty("refused_ms").GetInt64());
        var transient = c.GetProperty("refusal").GetString() == "transient";
        var patience = TimeSpan.FromMilliseconds(c.GetProperty("patience_ms").GetInt64());
        var clock = TimeSpan.Zero;
        var attempts = 0;

        void Operation()
        {
            attempts++;
            if (clock < refused)
                throw transient ? new IOException("refused") : new InvalidOperationException("refused");
        }

        static bool Transient(Exception ex) => ex is IOException or UnauthorizedAccessException;

        string got;
        try
        {
            if (awaited)
                await FileRetry.PatientlyAsync(Operation, patience, PauseAfter(schedule), Transient, () => clock,
                    (pause, _) => { clock += pause; return Task.CompletedTask; });
            else
                FileRetry.Patiently(Operation, patience, PauseAfter(schedule), Transient, () => clock, pause => clock += pause);
            got = "ok";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            got = "error";
        }

        Assert.Equal(c.GetProperty("expect").GetString(), got);
        if (c.TryGetProperty("attempts", out var expected)) Assert.Equal(expected.GetInt32(), attempts);
        Assert.True(clock <= patience, $"{name} ({schedule}, awaited {awaited}): waited past the window ({clock} > {patience})");
    }

    [Fact]
    public async Task The_awaited_window_cancels_its_pause()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileRetry.PatientlyAsync(
            () => { attempts++; throw new IOException("refused"); },
            TimeSpan.FromSeconds(30), _ => TimeSpan.FromSeconds(10), ex => ex is IOException, () => TimeSpan.Zero,
            (pause, ct) => { cts.Cancel(); return Task.Delay(pause, ct); }, ct: cts.Token));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void The_table_is_there_and_not_empty() => Assert.NotEmpty(Table().GetProperty("cases").EnumerateArray());
}
