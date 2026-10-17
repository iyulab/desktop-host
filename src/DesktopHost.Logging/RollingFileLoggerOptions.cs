namespace DesktopHost.Logging;

/// <summary>
/// Where a <see cref="RollingFileLoggerProvider"/> writes, and how much of it is kept.
/// </summary>
/// <remarks>
/// The log for a day is <c>{FilePrefix}-{yyyy-MM-dd}.log</c> (UTC). When it reaches
/// <see cref="FileSizeLimitBytes"/> the day continues in <c>{FilePrefix}-{yyyy-MM-dd}.1.log</c>, then <c>.2.log</c>, and
/// so on. Whenever a file is opened — at start, at midnight, at a size roll — old files are removed: any
/// <c>{RetentionPrefix}*.log</c> last written longer ago than <see cref="RetainedFileTimeLimit"/>, and the oldest of this
/// provider's own <c>{FilePrefix}-*.log</c> files beyond <see cref="RetainedFileCountLimit"/>.
/// </remarks>
public sealed class RollingFileLoggerOptions
{
    /// <summary>The directory the log files are written to. Created if it does not exist. Required.</summary>
    public string Directory { get; set; } = "";

    /// <summary>The start of every file name this provider writes, e.g. <c>host</c>. Required.</summary>
    public string FilePrefix { get; set; } = "";

    /// <summary>
    /// The start of the file names the age limit applies to. Defaults to <see cref="FilePrefix"/>. Set it wider when
    /// several processes each write their own prefix and any of them should clear the old files of all — for example
    /// workers writing <c>worker-{id}-…</c> with <c>RetentionPrefix = "worker"</c>.
    /// </summary>
    public string? RetentionPrefix { get; set; }

    /// <summary>Files under <see cref="RetentionPrefix"/> last written longer ago than this are deleted. Default 7 days.</summary>
    public TimeSpan RetainedFileTimeLimit { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The most of this provider's own files kept, newest first; <c>null</c> keeps any number within the age limit.
    /// Default 31.
    /// </summary>
    public int? RetainedFileCountLimit { get; set; } = 31;

    /// <summary>
    /// The size at which the day's file is closed and the next one started; <c>null</c> for no limit. A line is never
    /// split, so a file can end slightly over it. Default 64 MiB.
    /// </summary>
    public long? FileSizeLimitBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>The clock that dates each line and decides the day's file. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Directory))
            throw new ArgumentException("A log directory is required.", nameof(Directory));
        if (string.IsNullOrWhiteSpace(FilePrefix))
            throw new ArgumentException("A file prefix is required.", nameof(FilePrefix));
        if (FilePrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The file prefix contains a character a file name cannot.", nameof(FilePrefix));
        if (RetainedFileTimeLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RetainedFileTimeLimit), "The age limit must be positive.");
        if (RetainedFileCountLimit is < 1)
            throw new ArgumentOutOfRangeException(nameof(RetainedFileCountLimit), "Keep at least one file, or set null.");
        if (FileSizeLimitBytes is < 1)
            throw new ArgumentOutOfRangeException(nameof(FileSizeLimitBytes), "The size limit must be positive, or null.");
    }
}
