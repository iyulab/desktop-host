using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DesktopHost.Logging.Tests;

public sealed partial class RollingFileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dh-log-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 3, 14, 23, 59, 0, TimeSpan.Zero));

    public RollingFileLoggerProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp directory fails nothing */ }
    }

    private RollingFileLoggerOptions Options(Action<RollingFileLoggerOptions>? configure = null)
    {
        var options = new RollingFileLoggerOptions { Directory = _dir, FilePrefix = "app", TimeProvider = _clock };
        configure?.Invoke(options);
        return options;
    }

    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private string Touch(string name, TimeSpan age, int bytes = 1)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().UtcDateTime - age);
        return path;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z \[WARN\] \[Worker\] disk is slow \| System\.IO\.IOException: boom$")]
    private static partial Regex WarnLine();

    [Fact]
    public void A_line_carries_the_utc_time_the_level_the_short_category_and_the_exception()
    {
        using var provider = new RollingFileLoggerProvider(Options());
        provider.CreateLogger("Some.Namespace.Worker").LogWarning(new IOException("boom"), "disk is slow");

        var file = Path.Combine(_dir, "app-2026-03-14.log");
        Assert.Equal(file, provider.CurrentFile);
        var line = Assert.Single(ReadLines(file));
        Assert.StartsWith("2026-03-14T23:59:00.000Z [WARN] [Worker] disk is slow | System.IO.IOException: boom", line, StringComparison.Ordinal);
        Assert.Matches(WarnLine(), line);
    }

    [Fact]
    public async Task Lines_from_many_threads_come_out_whole()
    {
        using var provider = new RollingFileLoggerProvider(Options());
        var logger = provider.CreateLogger("Load");
        var payload = new string('x', 300);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++) logger.LogInformation("{Thread}:{Index}:{Payload}", t, i, payload);
        }, TestContext.Current.CancellationToken)));

        var lines = ReadLines(provider.CurrentFile!);
        Assert.Equal(2000, lines.Length);
        Assert.All(lines, l => Assert.EndsWith(payload, l, StringComparison.Ordinal));
    }

    [Fact]
    public void A_new_utc_day_starts_a_new_file()
    {
        using var provider = new RollingFileLoggerProvider(Options());
        var logger = provider.CreateLogger("Day");
        logger.LogInformation("before midnight");
        _clock.Advance(TimeSpan.FromMinutes(2));
        logger.LogInformation("after midnight");

        Assert.Equal("before midnight", ReadLines(Path.Combine(_dir, "app-2026-03-14.log")).Single()[^15..]);
        Assert.EndsWith("after midnight", ReadLines(Path.Combine(_dir, "app-2026-03-15.log")).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_file_continues_in_the_next_part_and_a_restart_appends_where_the_last_run_stopped()
    {
        var options = Options(o => o.FileSizeLimitBytes = 200);
        using (var provider = new RollingFileLoggerProvider(options))
        {
            var logger = provider.CreateLogger("Size");
            // About 100 bytes a line against a 200-byte limit: two lines a part, so five lines leave part 2 half full.
            for (var i = 0; i < 5; i++) logger.LogInformation("{Line}", new string('a', 60));
        }

        var first = Path.Combine(_dir, "app-2026-03-14.log");
        var second = Path.Combine(_dir, "app-2026-03-14.1.log");
        var last = Path.Combine(_dir, "app-2026-03-14.2.log");
        Assert.True(new FileInfo(first).Length >= 200);
        Assert.True(new FileInfo(second).Length >= 200);
        Assert.True(new FileInfo(last).Length < 200);
        var lastLines = ReadLines(last).Length;

        using var restarted = new RollingFileLoggerProvider(options);
        restarted.CreateLogger("Size").LogInformation("after restart");

        Assert.Equal(last, restarted.CurrentFile);
        Assert.Equal(lastLines + 1, ReadLines(last).Length);
    }

    [Fact]
    public void Files_older_than_the_age_limit_are_removed_at_start()
    {
        var old = Touch("app-2026-03-01.log", TimeSpan.FromDays(8));
        var recent = Touch("app-2026-03-12.log", TimeSpan.FromDays(2));
        var otherPrefix = Touch("other-2026-03-01.log", TimeSpan.FromDays(30));

        using var provider = new RollingFileLoggerProvider(Options());

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(otherPrefix));
    }

    [Fact]
    public void A_wider_retention_prefix_clears_the_old_files_of_every_writer_under_it()
    {
        var siblingOld = Touch("worker-b-2026-03-01.log", TimeSpan.FromDays(9));
        var siblingRecent = Touch("worker-c-2026-03-13.log", TimeSpan.FromHours(5));

        using var provider = new RollingFileLoggerProvider(Options(o =>
        {
            o.FilePrefix = "worker-a";
            o.RetentionPrefix = "worker";
        }));

        Assert.False(File.Exists(siblingOld));
        Assert.True(File.Exists(siblingRecent));
    }

    [Fact]
    public void Only_the_newest_files_are_kept_up_to_the_count_limit_and_another_writers_files_are_not_counted()
    {
        var files = Enumerable.Range(1, 5)
            .Select(d => Touch($"app-2026-03-{d:00}.log", TimeSpan.FromHours(10 - d)))
            .ToArray();
        var sibling = Touch("app-stdout-2026-03-01.log", TimeSpan.FromHours(20));

        using var provider = new RollingFileLoggerProvider(Options(o => o.RetainedFileCountLimit = 3));

        Assert.False(File.Exists(files[0]));
        Assert.False(File.Exists(files[1]));
        Assert.True(File.Exists(files[2]));
        Assert.True(File.Exists(files[3]));
        Assert.True(File.Exists(files[4]));
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public void The_count_limit_never_removes_the_file_being_written()
    {
        using var provider = new RollingFileLoggerProvider(Options(o => o.RetainedFileCountLimit = 1));
        var logger = provider.CreateLogger("Keep");
        logger.LogInformation("first");
        // The day's first file looks newer than the one about to be opened, so the count limit ranks the new one out.
        File.SetLastWriteTimeUtc(provider.CurrentFile!, DateTime.UtcNow + TimeSpan.FromDays(1));
        _clock.Advance(TimeSpan.FromMinutes(2));
        logger.LogInformation("second");

        Assert.Equal(Path.Combine(_dir, "app-2026-03-15.log"), provider.CurrentFile);

        Assert.True(File.Exists(provider.CurrentFile));
        Assert.EndsWith("second", ReadLines(provider.CurrentFile!).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_written_after_dispose_is_dropped()
    {
        var provider = new RollingFileLoggerProvider(Options());
        var logger = provider.CreateLogger("Late");
        logger.LogInformation("in time");
        var file = provider.CurrentFile!;
        provider.Dispose();

        logger.LogError("too late");

        Assert.Null(provider.CurrentFile);
        Assert.Single(ReadLines(file));
    }

    [Fact]
    public void Another_process_can_read_the_file_while_it_is_written()
    {
        using var provider = new RollingFileLoggerProvider(Options());
        var logger = provider.CreateLogger("Share");
        logger.LogInformation("one");
        using var reader = new FileStream(provider.CurrentFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        logger.LogInformation("two");

        Assert.Equal(2, ReadLines(provider.CurrentFile!).Length);
    }

    [Theory]
    [InlineData("", "app")]
    [InlineData("dir", "")]
    [InlineData("dir", "a/b")]
    public void A_missing_or_unusable_location_is_refused(string directory, string prefix)
    {
        var options = new RollingFileLoggerOptions { Directory = directory == "dir" ? _dir : directory, FilePrefix = prefix };
        Assert.ThrowsAny<ArgumentException>(() => new RollingFileLoggerProvider(options));
    }

    [Fact]
    public void Out_of_range_limits_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingFileLoggerProvider(Options(o => o.RetainedFileCountLimit = 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingFileLoggerProvider(Options(o => o.FileSizeLimitBytes = 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingFileLoggerProvider(Options(o => o.RetainedFileTimeLimit = TimeSpan.Zero)));
    }

    [Fact]
    public void The_builder_extension_registers_a_provider_that_writes()
    {
        using (var factory = LoggerFactory.Create(b => b.AddRollingFile(_dir, "built", o => o.TimeProvider = _clock)))
        {
            factory.CreateLogger("Built").LogInformation("registered");
        }

        Assert.EndsWith("registered", ReadLines(Path.Combine(_dir, "built-2026-03-14.log")).Single(), StringComparison.Ordinal);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
