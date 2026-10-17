using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DesktopHost.Files;

/// <summary>
/// Writes a small text file so that no reader ever observes a partial document, and reads one without standing in
/// the way of such a write. The new content goes to a sibling temp file, is flushed to disk, and is then renamed onto
/// the target in a single operation.
/// </summary>
/// <remarks>
/// <para>
/// <c>File.WriteAllText</c> truncates the target and streams the new bytes into it. A reader that opens the file
/// sharing write access — which it has to, or every save is refused while it reads — can observe that truncated state.
/// Measured: a reader looping over a 256 KB document saw lengths 0, 4 096, 16 384 … while
/// <c>File.WriteAllText</c> rewrote it. For a JSON document that is a parse failure; for a policy file it is a run
/// that silently goes without its policy.
/// </para>
/// <para>
/// On Windows the swap is a POSIX-semantics rename (<c>SetFileInformationByHandle</c> with
/// <c>FileRenameInfoEx</c>, Windows 10 1607 and later on NTFS): one operation, after which a handle already open on
/// the old file keeps reading the old bytes. The obvious alternatives both fall short. <c>MoveFileEx</c> (what
/// <c>File.Move(overwrite: true)</c> calls) is refused while any handle has the target open, even one that shares
/// delete. <c>ReplaceFile</c> (what <c>File.Replace</c> calls) is two renames with a gap between them in which the
/// name does not exist — three readers saw it missing 12 065 times across 400 replaces. It remains only as the
/// fallback for a volume that does not support the rename flags. Elsewhere the swap is <c>rename(2)</c>, which is
/// atomic and leaves an open handle on the old inode.
/// </para>
/// <para>
/// A reader that opens the target <i>without</i> <c>FILE_SHARE_DELETE</c> (<c>File.ReadAllText</c> is one) still
/// blocks the rename on Windows for as long as its read lasts, and so may a virus scanner or sync client that opens
/// the just-written file. The swap is therefore retried within a time budget. Readers that should never block a write
/// use <see cref="ReadAllTextShared"/> and its siblings, which share read, write and delete.
/// </para>
/// <para>
/// The temp file is a dot-file beside the target (<c>.name.&lt;guid&gt;.tmp</c>): on the same volume, so the swap is
/// a rename rather than a copy, and unique per write, so two writers never share one. A watcher filtered on the
/// target's own name still sees the rename onto it.
/// </para>
/// </remarks>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // The swap's retry is budgeted in time, not in attempts. The refusal a short ladder cannot outlast comes from
    // outside the process — something that opens the just-written file as it is closed and has let go again before
    // anyone can ask who it was; about 225 ms of back-off was not enough for it. Waits of 15, 30, 60 … ms, none longer
    // than 500 ms, about 2 s in all; the refusal after that is the caller's.
    private const int ReplaceBudgetMs = 2_000;
    private const int MaxReplaceWaitMs = 500;

    private static int? NextReplaceWaitMs(int attempt, int waitedMs)
        => waitedMs >= ReplaceBudgetMs ? null : Math.Min(MaxReplaceWaitMs, 15 << Math.Min(attempt - 1, 10));

    // Writers to one path take turns within this process. Two swaps racing on the same target make the rename fail
    // faster than the bounded retry can absorb (eight concurrent writers exhausted it). The retry stays for what a
    // gate cannot see: another process, or a reader without FILE_SHARE_DELETE. One small semaphore per distinct path
    // ever written. Case-insensitive on every OS: on a case-sensitive file system that only makes two different files
    // take turns.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    private static SemaphoreSlim GateFor(string path)
        => Gates.GetOrAdd(Path.GetFullPath(path), static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Reads a whole text file, opening it shared for read, write <b>and delete</b>, so that neither an in-place save
    /// nor the swap of <see cref="WriteAllText"/> is refused while this read is in progress.
    /// <c>File.ReadAllText</c> shares read only.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The file's text; the encoding is detected from a byte order mark, UTF-8 otherwise.</returns>
    public static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <inheritdoc cref="ReadAllTextShared"/>
    /// <param name="path">The file to read.</param>
    /// <param name="ct">Cancels the read.</param>
    public static async Task<string> ReadAllTextSharedAsync(string path, CancellationToken ct = default)
    {
        var stream = OpenAsyncRead(path);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <see cref="ReadAllTextShared"/> for a file that may not exist yet: <c>null</c> when the file or its directory is
    /// absent, the text when it is there, and <b>every other failure thrown</b>.
    /// </summary>
    /// <remarks>
    /// Prefer this to <c>File.Exists(path) ? read : default</c>. <c>File.Exists</c> answers <c>false</c> for a query
    /// it was refused as well as for an absent file, so a store that takes "absent" for "empty" and then saves its
    /// whole document replaces a file it never saw. A caller that writes back what it loaded must let a failure here
    /// stop the write.
    /// </remarks>
    /// <param name="path">The file to read.</param>
    /// <returns>The file's text, or <c>null</c> when it does not exist.</returns>
    public static string? ReadAllTextSharedIfExists(string path)
    {
        try
        {
            return ReadAllTextShared(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Absence is the documented null answer, not a failure.
            return null;
        }
    }

    /// <inheritdoc cref="ReadAllTextSharedIfExists"/>
    /// <param name="path">The file to read.</param>
    /// <param name="ct">Cancels the read.</param>
    public static async Task<string?> ReadAllTextSharedIfExistsAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await ReadAllTextSharedAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Absence is the documented null answer, not a failure.
            return null;
        }
    }

    /// <summary>
    /// Reads a text file line by line, opening it shared for read, write and delete. <c>File.ReadLines</c> shares read
    /// only, so it is refused while another process holds the file open for writing — a log or transcript that a
    /// writer appends to for its whole lifetime is exactly that case.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The lines, read lazily; the file is opened at the call and closed when enumeration ends.</returns>
    public static IEnumerable<string> ReadLinesShared(string path)
        => Lines(new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.SequentialScan));

    /// <inheritdoc cref="ReadLinesShared"/>
    /// <param name="path">The file to read.</param>
    /// <param name="ct">Cancels the enumeration.</param>
    public static IAsyncEnumerable<string> ReadLinesSharedAsync(string path, CancellationToken ct = default)
        => LinesAsync(OpenAsyncRead(path), ct);

    /// <summary>
    /// <see cref="ReadLinesShared"/> for a file that may not exist yet: no lines when the file or its directory is
    /// absent. Every other failure is thrown at the call, not at the first line — the file is opened before anything
    /// is enumerated, so a caller can decide before it starts reading. The line form of
    /// <see cref="ReadAllTextSharedIfExists"/>, for the same reason.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The lines, or none when the file does not exist.</returns>
    public static IEnumerable<string> ReadLinesSharedIfExists(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Absence is the documented empty answer, not a failure.
            return [];
        }
        return Lines(stream);
    }

    /// <inheritdoc cref="ReadLinesSharedIfExists"/>
    /// <param name="path">The file to read.</param>
    /// <param name="ct">Cancels the enumeration.</param>
    public static IAsyncEnumerable<string> ReadLinesSharedIfExistsAsync(string path, CancellationToken ct = default)
    {
        FileStream stream;
        try
        {
            stream = OpenAsyncRead(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Absence is the documented empty answer, not a failure.
            return AsyncEnumerable.Empty<string>();
        }
        return LinesAsync(stream, ct);
    }

    private static FileStream OpenAsyncRead(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

    // The iterators own the stream they are handed and dispose of it when enumeration ends or is abandoned.
    private static IEnumerable<string> Lines(FileStream stream)
    {
        using (stream)
        using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true))
        {
            while (reader.ReadLine() is { } line) yield return line;
        }
    }

    private static async IAsyncEnumerable<string> LinesAsync(
        FileStream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line) yield return line;
        }
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="content"/> (UTF-8, no byte order mark) so that a reader
    /// sees either the whole old document or the whole new one, never a part of either.
    /// </summary>
    /// <remarks>
    /// The content is written to a temp sibling, flushed to disk, and renamed onto the target. Writers to one path in
    /// this process take turns. A refused rename is retried for about two seconds; the last refusal is thrown with
    /// the original exception type and HResult, and a message that names the processes holding the file where the
    /// platform can tell. The temp file never outlives the call.
    /// </remarks>
    /// <param name="path">The file to create or replace. Its directory must exist.</param>
    /// <param name="content">The complete new content.</param>
    /// <exception cref="IOException">The file could not be replaced within the retry budget.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file was refused within the retry budget.</exception>
    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var temp = TempSiblingOf(path);
        var gate = GateFor(path);
        gate.Wait();
        try
        {
            using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.None))
            {
                stream.Write(Utf8NoBom.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }

            var waited = 0;
            string? firstHolders = null;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Swap(temp, path);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A bounded retry, not a swallow: the refusal that exhausts the budget is thrown to the caller.
                    firstHolders ??= HoldersOf(path, temp);
                    if (NextReplaceWaitMs(attempt, waited) is not { } wait) throw Exhausted(path, attempt, waited, firstHolders, ex);
                    Thread.Sleep(wait);
                    waited += wait;
                }
            }
        }
        finally
        {
            DeleteLeftoverTemp(temp);
            gate.Release();
        }
    }

    /// <inheritdoc cref="WriteAllText"/>
    /// <param name="path">The file to create or replace. Its directory must exist.</param>
    /// <param name="content">The complete new content.</param>
    /// <param name="ct">Cancels the wait for the per-path gate, the write and the retry waits.</param>
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var temp = TempSiblingOf(path);
        var gate = GateFor(path);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                var bytes = Utf8NoBom.GetBytes(content);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true); // the rename must not publish a name whose bytes are still only in cache
            }

            var waited = 0;
            string? firstHolders = null;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Swap(temp, path);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A bounded retry, not a swallow. A reader without FILE_SHARE_DELETE holds the target for the
                    // length of its read and is gone within a wait or two; a holder in another process may need the
                    // rest of the budget.
                    firstHolders ??= HoldersOf(path, temp);
                    if (NextReplaceWaitMs(attempt, waited) is not { } wait) throw Exhausted(path, attempt, waited, firstHolders, ex);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    waited += wait;
                }
            }
        }
        finally
        {
            DeleteLeftoverTemp(temp);
            gate.Release();
        }
    }

    // Asked at the first refusal, while the holder still has the file: asked only after the last attempt, the answer
    // was almost always "nobody", because a brief holder had let go by then. Both names are asked, because a refused
    // rename does not say which side was held.
    private static string HoldersOf(string path, string temp)
    {
        var target = FileLockHolders.Describe(path);
        var source = FileLockHolders.Describe(temp);
        return target.Count == 0 && source.Count == 0
            ? "none named"
            : $"target {(target.Count > 0 ? string.Join(", ", target) : "none")}, "
              + $"temp {(source.Count > 0 ? string.Join(", ", source) : "none")}";
    }

    // The last failure, carrying what a bare refusal never says: who held the file. The exception type and HResult
    // are kept, so a caller that catches UnauthorizedAccessException still does. A holder may have let go between the
    // last attempt and the question; the message then says so rather than guessing.
    private static Exception Exhausted(string path, int attempts, int waitedMs, string firstHolders, Exception last)
    {
        var holders = FileLockHolders.Describe(path);
        var who = holders.Count > 0
            ? $"held by {string.Join(", ", holders)}"
            : "no holder could be named — it may have let go since, or this is not Windows";
        var message = $"Could not replace '{path}' after {attempts} attempts over {waitedMs} ms ({who}; at the first refusal: "
            + $"{firstHolders}): {last.Message} (HResult 0x{last.HResult:X8})";
        Statistics.RecordExhaustion(message);
        return last is UnauthorizedAccessException
            ? new UnauthorizedAccessException(message, last) { HResult = last.HResult }
            : new IOException(message, last) { HResult = last.HResult };
    }

    // A dot-file beside the target, unique per write: two writers (two threads, two processes) never share a temp.
    private static string TempSiblingOf(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>
    /// Process-wide counters of how the writes of <see cref="AtomicFile"/> went, for diagnosing a refused or gapped
    /// replace from a test or a log line: a test that observes a missing name or a failed write can print which path
    /// the writer took and what the last exhausted write said. They count from process start and are never reset;
    /// compare a value before and after the code under observation rather than reading it absolutely.
    /// </summary>
    public static class Statistics
    {
        private static int _twoStepSwaps;
        private static int _exhaustedWrites;
        private static string? _lastExhaustion;

        /// <summary>
        /// How many Windows swaps fell back to the two-step replace — the one with a moment in which the name does not
        /// exist — because the volume did not support the POSIX-semantics rename.
        /// </summary>
        public static int TwoStepSwaps => Volatile.Read(ref _twoStepSwaps);

        /// <summary>How many writes gave up after the retry budget ran out.</summary>
        public static int ExhaustedWrites => Volatile.Read(ref _exhaustedWrites);

        /// <summary>The message of the most recent write that gave up, or <c>null</c> when none has.</summary>
        public static string? LastExhaustion => Volatile.Read(ref _lastExhaustion);

        internal static void RecordTwoStepSwap() => Interlocked.Increment(ref _twoStepSwaps);

        internal static void RecordExhaustion(string message)
        {
            Interlocked.Increment(ref _exhaustedWrites);
            Volatile.Write(ref _lastExhaustion, message);
        }
    }

    private static void Swap(string temp, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (TryPosixRename(temp, path)) return;
            // A volume without the rename flags (FAT, some network shares): the two-step replace. Its gap is why it is
            // the fallback and not the rule.
            Statistics.RecordTwoStepSwap();
            if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
            else File.Move(temp, path, overwrite: false);
            return;
        }

        File.Move(temp, path, overwrite: true); // rename(2): atomic, and an open handle keeps the old inode
    }

    // ── Windows: SetFileInformationByHandle(FileRenameInfoEx) ────────────────────────────────────────────

    private const uint DeleteAccess = 0x00010000;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;        // FILE_SHARE_READ | WRITE | DELETE
    private const uint OpenExisting = 3;
    private const int FileRenameInfoEx = 22;
    private const uint RenameReplaceIfExists = 0x1;
    private const uint RenamePosixSemantics = 0x2;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidFunction = 1;

    // False only when the file system does not support the operation, so the caller falls back; any other failure is
    // thrown as the IOException the retry loop expects.
    [SupportedOSPlatform("windows")]
    private static bool TryPosixRename(string source, string destination)
    {
        var fullDestination = Path.GetFullPath(destination);
        var target = fullDestination.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullDestination : @"\\?\" + fullDestination;

        using var handle = CreateFileW(Path.GetFullPath(source), DeleteAccess, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"Could not open '{source}' for rename.", new Win32Exception(Marshal.GetLastWin32Error()));

        // FILE_RENAME_INFO, laid out by hand because FileName is variable-length:
        //   DWORD Flags · (pad to pointer) · HANDLE RootDirectory · DWORD FileNameLength · WCHAR FileName[]
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var nameBytes = Encoding.Unicode.GetBytes(target);
        var size = nameOffset + nameBytes.Length + 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, 0, (int)(RenameReplaceIfExists | RenamePosixSemantics));
            Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, buffer + nameOffset, nameBytes.Length);

            if (SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)size))
                return true;

            var error = Marshal.GetLastWin32Error();
            if (error is ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction)
                return false;
            throw new IOException($"Could not replace '{destination}'.", new Win32Exception(error));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file, int fileInformationClass, IntPtr fileInformation, uint bufferSize);

    private static void DeleteLeftoverTemp(string temp)
    {
        // Present only when the write or the rename failed; removing it must not mask that failure.
        if (!File.Exists(temp)) return;
        try { File.Delete(temp); }
        catch (IOException) { /* best-effort cleanup of our own temp; the original failure is what matters */ }
        catch (UnauthorizedAccessException) { /* as above */ }
    }
}
