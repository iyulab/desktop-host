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
    // The table holds whatever the pauses are; run it under a pause that does not divide the windows as well as the default.
    private static readonly int[] PauseStepsMs = [7, 300, 1000];

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var c in Table().GetProperty("cases").EnumerateArray())
            foreach (var step in PauseStepsMs)
                data.Add(c.GetProperty("name").GetString()!, step);
        return data;
    }

    private static JsonElement Table() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cases", "file-retry.json"))).RootElement;

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_shared_file_retry_table(string name, int pauseStepMs)
    {
        var c = Table().GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var refused = TimeSpan.FromMilliseconds(c.GetProperty("refused_ms").GetInt64());
        var transient = c.GetProperty("refusal").GetString() == "transient";
        var patience = TimeSpan.FromMilliseconds(c.GetProperty("patience_ms").GetInt64());
        var clock = TimeSpan.Zero;
        var attempts = 0;
        var options = FileRetryOptions.Default with { Patience = patience, PauseStep = TimeSpan.FromMilliseconds(pauseStepMs) };

        string got;
        try
        {
            FileRetry.Patiently(
                () =>
                {
                    attempts++;
                    if (clock < refused)
                        throw transient ? new IOException("refused") : new InvalidOperationException("refused");
                },
                options,
                ex => ex is IOException or UnauthorizedAccessException,
                () => clock,
                pause => clock += pause);
            got = "ok";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            got = "error";
        }

        Assert.Equal(c.GetProperty("expect").GetString(), got);
        if (c.TryGetProperty("attempts", out var expected)) Assert.Equal(expected.GetInt32(), attempts);
        Assert.True(clock <= patience, $"{name} (pause step {pauseStepMs} ms): waited past the window ({clock} > {patience})");
    }

    [Fact]
    public void The_table_is_there_and_not_empty() => Assert.NotEmpty(Table().GetProperty("cases").EnumerateArray());
}
