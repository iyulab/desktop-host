using System.Text;
using System.Text.RegularExpressions;

namespace DesktopHost.Files.Tests;

/// <summary>
/// A file that something reads at any moment is <b>replaced</b>, never truncated in place. A truncating write lets a
/// reader that shares write access — the only kind of reader that does not block the save — see an empty or
/// half-written document.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "desktophost-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup; a leftover directory fails nothing */ }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Writes_the_content_as_utf8_without_a_bom_and_leaves_no_temp_file_behind()
    {
        var path = Path.Combine(_dir, "document.json");

        await AtomicFile.WriteAllTextAsync(path, "{ \"name\": \"한글\" }");

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.NotEqual(Encoding.UTF8.GetPreamble(), bytes.Take(3).ToArray());
        Assert.Equal("{ \"name\": \"한글\" }", Encoding.UTF8.GetString(bytes));
        // The temp sibling is renamed onto the target, not left beside it.
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Replaces_an_existing_file()
    {
        var path = Path.Combine(_dir, "instructions.md");
        await File.WriteAllTextAsync(path, "old instructions\n");

        await AtomicFile.WriteAllTextAsync(path, "new instructions\n");

        Assert.Equal("new instructions\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_reader_that_holds_the_file_does_not_stop_the_replace_and_keeps_reading_what_it_opened()
    {
        var path = Path.Combine(_dir, "document.json");
        await File.WriteAllTextAsync(path, "the old document");
        await using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        await AtomicFile.WriteAllTextAsync(path, "the new document");

        Assert.Equal("the new document", ReadShared(path));
        using var reader = new StreamReader(held);
        // A replace swaps the name, not the bytes under an open handle.
        Assert.Equal("the old document", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task A_concurrent_shared_reader_never_sees_a_partial_document()
    {
        // The property itself. Two documents of very different length; the reader accepts either whole one and
        // nothing else. With an in-place truncating write this fails within milliseconds (empty reads, and the long
        // document cut short).
        var path = Path.Combine(_dir, "rules.json");
        var shortDoc = "{\"v\":1}";
        var longDoc = "{\"v\":2,\"pad\":\"" + new string('x', 256 * 1024) + "\"}";
        await AtomicFile.WriteAllTextAsync(path, shortDoc);

        using var stop = new CancellationTokenSource();
        var torn = new List<int>();
        var reads = 0;
        var readerTask = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                string text;
                try { text = ReadShared(path); }
                catch (IOException) { continue; }            // the instant of the swap on Windows; not a torn read
                catch (UnauthorizedAccessException) { continue; }
                reads++;
                if (text != shortDoc && text != longDoc) lock (torn) torn.Add(text.Length);
            }
        });

        for (var i = 0; i < 150; i++)
            await AtomicFile.WriteAllTextAsync(path, i % 2 == 0 ? longDoc : shortDoc);

        await stop.CancelAsync();
        await readerTask;
        Assert.True(reads > 0, "the reader never completed a read");
        Assert.True(torn.Count == 0,
            $"a reader must see one whole document or the other — lengths seen: {string.Join(",", torn.Take(8))}");
    }

    [Fact]
    public async Task A_watcher_filtered_on_the_file_name_still_hears_the_swap()
    {
        // A watcher filtered on one file name hears an in-place write as Changed. A swap is a different file-system
        // operation; if it raised nothing such a watcher would silently stop noticing saves. The configuration is the
        // usual one for "tell me when this file changes".
        var path = Path.Combine(_dir, "instructions.md");
        await File.WriteAllTextAsync(path, "old instructions\n");

        var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(_dir)
        {
            Filter = "instructions.md",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        watcher.Created += (_, e) => heard.TrySetResult("Created " + e.Name);
        watcher.Changed += (_, e) => heard.TrySetResult("Changed " + e.Name);
        watcher.Renamed += (_, e) => heard.TrySetResult("Renamed " + e.OldName + " -> " + e.Name);
        watcher.EnableRaisingEvents = true;

        await AtomicFile.WriteAllTextAsync(path, "new instructions\n");

        var first = await Task.WhenAny(heard.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(heard.Task, first);
        // Which event arrives is the platform's business (a rename onto the name on Windows, the new name arriving on
        // Linux); that an event about the name is raised at all is the contract.
        Assert.Contains("instructions.md", await heard.Task, StringComparison.Ordinal);
    }

    [Fact]
    public void The_synchronous_write_replaces_under_a_held_reader_and_leaves_no_temp_behind()
    {
        var path = Path.Combine(_dir, "marker.json");
        AtomicFile.WriteAllText(path, "first");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        AtomicFile.WriteAllText(path, "second — 한글");

        Assert.Equal("second — 한글", AtomicFile.ReadAllTextShared(path));
        using var reader = new StreamReader(held);
        Assert.Equal("first", reader.ReadToEnd());
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_holder_that_lets_go_within_a_second_does_not_fail_the_write()
    {
        // The retry is budgeted in time: a handle without FILE_SHARE_DELETE held for 600 ms must not fail the write.
        // Windows only — rename(2) is not refused by an open handle.
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "briefly-held.json");
        AtomicFile.WriteAllText(path, "first");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // A thread of its own: a pool task may not be scheduled in time on a loaded machine, which would measure the
        // pool rather than the budget.
        var release = new Thread(() =>
        {
            Thread.Sleep(600);
            held.Dispose();
        }) { IsBackground = true };
        release.Start();

        AtomicFile.WriteAllText(path, "second");

        release.Join();
        await Task.CompletedTask;
        Assert.Equal("second", AtomicFile.ReadAllTextShared(path));
        // No temp sibling is left behind.
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void When_the_retry_budget_runs_out_the_exception_names_who_held_the_file()
    {
        // A handle WITHOUT FILE_SHARE_DELETE is what refuses the rename; hold one for longer than the budget and the
        // failure must say who — here, this test process. Windows only: rename(2) is not refused by an open handle,
        // so there is no such failure to describe elsewhere.
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "held.json");
        AtomicFile.WriteAllText(path, "first");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var exhaustedBefore = AtomicFile.Statistics.ExhaustedWrites;

        var failure = Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "second"));

        // Callers catch these two, so the type is kept.
        Assert.True(failure is IOException or UnauthorizedAccessException, $"unexpected type {failure.GetType()}");
        // The budget is time, and the message says how much ran out.
        Assert.Matches(new Regex(@"after \d+ attempts over \d+ ms"), failure.Message);
        // The holder is asked while it still holds the file.
        Assert.Contains("at the first refusal: target", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"({Environment.ProcessId})", failure.Message, StringComparison.Ordinal);
        Assert.NotNull(failure.InnerException);
        Assert.True(AtomicFile.Statistics.ExhaustedWrites > exhaustedBefore);
        Assert.NotNull(AtomicFile.Statistics.LastExhaustion);
        held.Dispose();
        // A refused replace leaves the old document whole, and the temp sibling is cleaned up on this path too.
        Assert.Equal("first", AtomicFile.ReadAllTextShared(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task The_async_write_names_the_holder_too()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "held-async.json");
        await AtomicFile.WriteAllTextAsync(path, "first");
        await using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => AtomicFile.WriteAllTextAsync(path, "second"));

        Assert.Contains($"({Environment.ProcessId})", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writers_racing_on_one_path_never_share_a_temp_and_the_file_is_always_one_whole_document()
    {
        // A fixed "<name>.tmp" lets two writers truncate each other's temp, and the loser's move then publishes a
        // half-written file or throws.
        var path = Path.Combine(_dir, "index.json");
        var documents = Enumerable.Range(0, 8).Select(i => new string((char)('a' + i), 20_000)).ToArray();

        await Task.WhenAll(documents.Select((doc, i) => Task.Run(async () =>
        {
            for (var round = 0; round < 25; round++)
            {
                if (i % 2 == 0) AtomicFile.WriteAllText(path, doc);
                else await AtomicFile.WriteAllTextAsync(path, doc);
                Assert.Contains(AtomicFile.ReadAllTextShared(path), documents);
            }
        })));

        Assert.Contains(await AtomicFile.ReadAllTextSharedAsync(path), documents);
        // Every temp was renamed onto the target or cleaned up.
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task The_name_never_points_at_nothing_while_it_is_being_replaced()
    {
        // ReplaceFile (what File.Replace calls) is two renames — the old file away, the new one in — and between them
        // the name does not exist. A reader in that gap reads "no file": an empty store, a missing policy. The
        // POSIX-semantics rename has no such gap.
        var path = Path.Combine(_dir, "store.dat");
        AtomicFile.WriteAllText(path, "{\"v\":0}");
        using var stop = new CancellationTokenSource();
        var missing = 0; var opens = 0; var openFailures = 0; var probeRefused = 0; var lastRefusal = 0; var badContent = 0;
        string? lastOpenFailure = null;
        string? lastBadContent = null;
        // Dedicated threads, and the writer waits for them to start: on a saturated pool a Task.Run reader could be
        // scheduled only after every write had finished, and would observe nothing.
        using var started = new CountdownEvent(3);
        var readers = Enumerable.Range(0, 3).Select(reader => new Thread(() =>
        {
            started.Signal();
            while (!stop.IsCancellationRequested)
            {
                // Not File.Exists: it answers false both for "the name is absent" and for "the query was refused".
                // This tells them apart.
                try { _ = File.GetAttributes(path); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { Interlocked.Increment(ref missing); continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref probeRefused); Volatile.Write(ref lastRefusal, ex.HResult); continue;
                }
                try
                {
                    var text = AtomicFile.ReadAllTextShared(path);
                    // Counted here and asserted after Join: an assertion thrown on a raw thread is not a test failure.
                    if (!text.StartsWith("{\"v\":", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref badContent);
                        Volatile.Write(ref lastBadContent, text.Length > 32 ? text[..32] : text);
                    }
                    Interlocked.Increment(ref opens);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref openFailures);
                    Volatile.Write(ref lastOpenFailure, $"{ex.GetType().Name} 0x{ex.HResult:X8}");
                }
            }
        }) { IsBackground = true }).ToArray();
        foreach (var t in readers) t.Start();
        started.Wait();

        var twoStepBefore = AtomicFile.Statistics.TwoStepSwaps;
        var exhaustedBefore = AtomicFile.Statistics.ExhaustedWrites;
        for (var i = 1; i <= 400; i++) AtomicFile.WriteAllText(path, $"{{\"v\":{i}}}");
        // Whether the writer fell back to the two-step replace (a volume or filter driver refusing the POSIX rename)
        // is what a red here must say.
        var twoStep = AtomicFile.Statistics.TwoStepSwaps - twoStepBefore;
        var exhausted = AtomicFile.Statistics.ExhaustedWrites - exhaustedBefore;
        await stop.CancelAsync();
        foreach (var t in readers) t.Join();

        Assert.True(opens > 0, "the readers never completed a read");
        Assert.True(badContent == 0, $"a reader saw content that is not a whole document {badContent} times (last: '{lastBadContent}')");
        Assert.True(missing == 0,
            $"a reader must never find the name missing — {missing} misses, {opens} opens, {openFailures} open failures, "
            + $"{probeRefused} attribute queries refused (last HRESULT 0x{lastRefusal:X8}), {twoStep} two-step (gap) swaps while the 400 writes ran");
        Assert.True(probeRefused == 0,
            $"a reader must never be refused the name's attributes while it is replaced (File.Exists reads that as absent) — "
            + $"last HRESULT 0x{lastRefusal:X8}, {opens} opens");
        Assert.True(openFailures == 0,
            $"a reader must never be refused the name while it is replaced — {opens} opens, last refusal {lastOpenFailure ?? "none"}; "
            + $"{exhausted} exhausted writes (last: {AtomicFile.Statistics.LastExhaustion ?? "none"})");
    }

    [Fact]
    public async Task IfExists_answers_null_only_when_the_file_or_its_directory_is_not_there()
    {
        var path = Path.Combine(_dir, "index.json");

        Assert.Null(AtomicFile.ReadAllTextSharedIfExists(path));
        Assert.Null(AtomicFile.ReadAllTextSharedIfExists(Path.Combine(_dir, "no-such-dir", "index.json")));
        Assert.Null(await AtomicFile.ReadAllTextSharedIfExistsAsync(path));

        await AtomicFile.WriteAllTextAsync(path, "{\"items\":{}}");
        Assert.Equal("{\"items\":{}}", AtomicFile.ReadAllTextSharedIfExists(path));
        Assert.Equal("{\"items\":{}}", await AtomicFile.ReadAllTextSharedIfExistsAsync(path));
    }

    [Fact]
    public async Task The_line_forms_answer_no_lines_for_a_missing_file_and_throw_at_the_call_for_a_refused_one()
    {
        var path = Path.Combine(_dir, "log.jsonl");
        Assert.Empty(AtomicFile.ReadLinesSharedIfExists(path));
        Assert.Empty(AtomicFile.ReadLinesSharedIfExists(Path.Combine(_dir, "no-such-dir", "log.jsonl")));
        Assert.Empty(await ToListAsync(AtomicFile.ReadLinesSharedIfExistsAsync(path)));

        await File.WriteAllTextAsync(path, "a\nb\n");
        Assert.Equal(["a", "b"], AtomicFile.ReadLinesSharedIfExists(path));
        Assert.Equal(["a", "b"], await ToListAsync(AtomicFile.ReadLinesSharedIfExistsAsync(path)));

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // Thrown by the call itself, not on the first line: a caller decides before it starts reading.
        Assert.Throws<IOException>(() => AtomicFile.ReadLinesSharedIfExists(path));
        Assert.Throws<IOException>(() => AtomicFile.ReadLinesSharedIfExistsAsync(path));
    }

    [Fact]
    public async Task The_shared_line_readers_read_a_file_another_handle_holds_open_for_writing()
    {
        // File.ReadLines shares read only and is refused here; the shared forms share write and delete.
        var path = Path.Combine(_dir, "appended.jsonl");
        await File.WriteAllTextAsync(path, "one\ntwo\n");
        await using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(["one", "two"], AtomicFile.ReadLinesShared(path));
        Assert.Equal(["one", "two"], await ToListAsync(AtomicFile.ReadLinesSharedAsync(path)));
    }

    private static async Task<List<string>> ToListAsync(IAsyncEnumerable<string> lines)
    {
        var list = new List<string>();
        await foreach (var line in lines) list.Add(line);
        return list;
    }

    [Fact]
    public async Task IfExists_throws_for_a_file_that_is_there_but_cannot_be_read()
    {
        // A store that saves its whole document after loading must not read "unreadable" as "absent" — File.Exists
        // does, and an empty load saved back replaces the real file.
        var path = Path.Combine(_dir, "state.json");
        await File.WriteAllTextAsync(path, "{}");
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Exact type: a sharing violation is an IOException, and FileNotFoundException would not match.
        Assert.Throws<IOException>(() => AtomicFile.ReadAllTextSharedIfExists(path));
        await Assert.ThrowsAsync<IOException>(() => AtomicFile.ReadAllTextSharedIfExistsAsync(path));
    }
}
