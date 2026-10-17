using Microsoft.Extensions.Logging;

namespace DesktopHost.Files;

/// <summary>
/// How long <see cref="FileRetry"/> keeps trying an operation that a transient lock refuses.
/// </summary>
/// <remarks>
/// The defaults — 10 attempts, waiting 1 s, 2 s, 3 s, 3 s … between them, about 25 s in all — outlast the 10–20 s a
/// virus scanner can hold a just-written, just-moved or just-extracted file while it scans it.
/// </remarks>
/// <param name="MaxAttempts">How many times the operation runs in all; the last attempt's failure propagates. At least 1.</param>
/// <param name="DelayStep">The wait after the first failed attempt; the n-th wait is n times this, up to <paramref name="MaxDelay"/>.</param>
/// <param name="MaxDelay">The longest single wait between attempts.</param>
public sealed record FileRetryOptions(int MaxAttempts, TimeSpan DelayStep, TimeSpan MaxDelay)
{
    /// <summary>10 attempts, waits of 1 s, 2 s, 3 s … capped at 3 s.</summary>
    public static FileRetryOptions Default { get; } = new(10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));

    internal TimeSpan DelayAfter(int attempt)
    {
        var delay = DelayStep * attempt;
        return delay < MaxDelay ? delay : MaxDelay;
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1, nameof(MaxAttempts));
        ArgumentOutOfRangeException.ThrowIfLessThan(DelayStep, TimeSpan.Zero, nameof(DelayStep));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDelay, TimeSpan.Zero, nameof(MaxDelay));
    }
}

/// <summary>
/// File and directory deletes and moves that retry through a transient lock instead of failing at once.
/// </summary>
/// <remarks>
/// <para>
/// On Windows a virus scanner, sync client or indexer can hold a share-mode lock on a file for seconds right after it
/// is written, moved or extracted. A bare call in that window throws — <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/>, and both occur: <c>File.Move</c> onto a locked destination throws the
/// latter, not the former. Depending on the caller that either escapes past its cleanup or is swallowed by a
/// best-effort catch, and either way the file stays where the operation was meant to remove it from.
/// </para>
/// <para>
/// Only those two exception types are retried; anything else propagates at once. The last attempt is not guarded, so
/// its failure reaches the caller unchanged. Each retry is logged at Warning when a logger is given.
/// </para>
/// </remarks>
public static partial class FileRetry
{
    /// <summary>Deletes a file, retrying through a transient lock. A missing file is not an error.</summary>
    /// <param name="path">The file to delete.</param>
    /// <param name="logger">Receives a Warning per retry; optional.</param>
    /// <param name="options">The retry budget; <see cref="FileRetryOptions.Default"/> when omitted.</param>
    public static void DeleteFile(string path, ILogger? logger = null, FileRetryOptions? options = null) =>
        Run(() => File.Delete(path), "File.Delete", path, logger, options);

    /// <summary>Moves a file, retrying through a transient lock on either side.</summary>
    /// <param name="source">The file to move.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="overwrite">Whether an existing destination is replaced.</param>
    /// <param name="logger">Receives a Warning per retry; optional.</param>
    /// <param name="options">The retry budget; <see cref="FileRetryOptions.Default"/> when omitted.</param>
    public static void MoveFile(string source, string destination, bool overwrite = true, ILogger? logger = null, FileRetryOptions? options = null) =>
        Run(() => File.Move(source, destination, overwrite), "File.Move", $"{source} -> {destination}", logger, options);

    /// <summary>Deletes a directory and everything in it, retrying through a transient lock on any file inside.</summary>
    /// <param name="path">The directory to delete.</param>
    /// <param name="logger">Receives a Warning per retry; optional.</param>
    /// <param name="options">The retry budget; <see cref="FileRetryOptions.Default"/> when omitted.</param>
    public static void DeleteDirectory(string path, ILogger? logger = null, FileRetryOptions? options = null) =>
        Run(() => Directory.Delete(path, recursive: true), "Directory.Delete", path, logger, options);

    /// <summary>Moves a directory, retrying through a transient lock on any file inside.</summary>
    /// <param name="source">The directory to move.</param>
    /// <param name="destination">Where it goes; must not exist.</param>
    /// <param name="logger">Receives a Warning per retry; optional.</param>
    /// <param name="options">The retry budget; <see cref="FileRetryOptions.Default"/> when omitted.</param>
    public static void MoveDirectory(string source, string destination, ILogger? logger = null, FileRetryOptions? options = null) =>
        Run(() => Directory.Move(source, destination), "Directory.Move", $"{source} -> {destination}", logger, options);

    /// <summary>
    /// Best-effort file delete: one attempt, or the whole retry budget when <paramref name="retry"/> is set. A failure
    /// is logged at Debug and answered with <c>false</c> instead of thrown, so optional cleanup stays optional while a
    /// left-behind file can still be seen in the log. A missing file is not a failure.
    /// </summary>
    /// <param name="path">The file to delete.</param>
    /// <param name="logger">Receives the failure at Debug; optional.</param>
    /// <param name="retry">Whether to retry through a transient lock before giving up.</param>
    /// <param name="options">The retry budget when <paramref name="retry"/> is set; <see cref="FileRetryOptions.Default"/> when omitted.</param>
    /// <returns><c>true</c> when the file is gone.</returns>
    public static bool TryDeleteFile(string path, ILogger? logger = null, bool retry = false, FileRetryOptions? options = null) =>
        TryRun(() => { if (retry) DeleteFile(path, logger, options); else File.Delete(path); }, "File.Delete", path, logger);

    /// <summary>
    /// Best-effort directory delete: one attempt, or the whole retry budget when <paramref name="retry"/> is set. A
    /// failure is logged at Debug and answered with <c>false</c> instead of thrown. A missing directory is a failure.
    /// </summary>
    /// <param name="path">The directory to delete.</param>
    /// <param name="logger">Receives the failure at Debug; optional.</param>
    /// <param name="retry">Whether to retry through a transient lock before giving up.</param>
    /// <param name="recursive">Whether the contents go too; a retried delete is always recursive.</param>
    /// <param name="options">The retry budget when <paramref name="retry"/> is set; <see cref="FileRetryOptions.Default"/> when omitted.</param>
    /// <returns><c>true</c> when the directory was deleted.</returns>
    public static bool TryDeleteDirectory(string path, ILogger? logger = null, bool retry = false, bool recursive = true, FileRetryOptions? options = null) =>
        TryRun(() => { if (retry) DeleteDirectory(path, logger, options); else Directory.Delete(path, recursive); }, "Directory.Delete", path, logger);

    private static bool TryRun(Action operation, string operationName, string context, ILogger? logger)
    {
        try
        {
            operation();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort by contract: the failure is logged and answered with false.
            if (logger is not null) LogLeftBehind(logger, ex, operationName, context);
            return false;
        }
    }

    private static void Run(Action operation, string operationName, string context, ILogger? logger, FileRetryOptions? options)
    {
        options ??= FileRetryOptions.Default;
        options.Validate();
        for (var attempt = 1; attempt < options.MaxAttempts; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var delay = options.DelayAfter(attempt);
                if (logger is not null)
                    LogRetrying(logger, ex, operationName, attempt, options.MaxAttempts, (int)delay.TotalMilliseconds, context);
                Thread.Sleep(delay);
            }
        }
        operation(); // the last attempt: its failure propagates
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Operation} failed (best effort, left behind): {Context}")]
    private static partial void LogLeftBehind(ILogger logger, Exception exception, string operation, string context);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Operation} failed (attempt {Attempt}/{Max}), retrying in {Delay} ms: {Context}")]
    private static partial void LogRetrying(ILogger logger, Exception exception, string operation, int attempt, int max, int delay, string context);
}
