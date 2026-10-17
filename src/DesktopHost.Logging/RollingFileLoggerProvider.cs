using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace DesktopHost.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that appends one line per entry to a file under a directory, starts a new file each
/// UTC day and at a size limit, and removes old files by age and by count (<see cref="RollingFileLoggerOptions"/>).
/// </summary>
/// <remarks>
/// <para>
/// A line is <c>{UTC timestamp} [{LEVEL}] [{category}] {message}</c>, the category shortened to its last segment and an
/// exception appended after <c> | </c>. Every write — line, newline, and any roll — happens under one lock, and the
/// writer flushes each line, so entries from many threads come out whole and in the order they were written, and a file
/// read while the process runs ends on a complete line.
/// </para>
/// <para>
/// The file is opened for append, sharing read and write, so another process can read it, or append to it, as it grows.
/// Entries written after <see cref="Dispose"/> are dropped instead of thrown — a callback from native code or a timer
/// that fires during shutdown must not fault on its log.
/// </para>
/// <para>
/// Removing old files is best effort. A provider cannot log its own housekeeping, so a file that cannot be deleted —
/// another process has it open — stays on disk, where it is visible, and is tried again at the next roll.
/// </para>
/// </remarks>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly RollingFileLoggerOptions _options;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private string _day = "";
    private int _part;
    private bool _disposed;

    /// <summary>Creates a provider writing under <see cref="RollingFileLoggerOptions.Directory"/>.</summary>
    /// <exception cref="ArgumentException">A required option is missing or out of range.</exception>
    public RollingFileLoggerProvider(RollingFileLoggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        System.IO.Directory.CreateDirectory(options.Directory);
        RemoveOldFiles();
    }

    /// <summary>The directory the files are written to.</summary>
    public string Directory => _options.Directory;

    /// <summary>The file currently written, or <c>null</c> before the first entry and after <see cref="Dispose"/>.</summary>
    public string? CurrentFile { get; private set; }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(ShortCategory(name), this));

    internal void Write(string category, LogLevel level, string message)
    {
        var now = _options.TimeProvider.GetUtcNow();
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-ddTHH:mm:ss.fffZ} [{LevelName(level)}] [{category}] {message}");
        lock (_gate)
        {
            if (_disposed) return;
            if (_writer is null || day != _day)
            {
                Open(day, part: 0);
            }
            else if (_options.FileSizeLimitBytes is { } limit && _writer.BaseStream.Length >= limit)
            {
                Open(day, _part + 1);
            }
            _writer!.WriteLine(line);
        }
    }

    // Opens the day's file at `part` or the first part after it that is not already full — a restart on the same day
    // appends to where the last run stopped instead of growing a full file further.
    private void Open(string day, int part)
    {
        _writer?.Dispose();
        _writer = null;
        while (true)
        {
            var path = Path.Combine(_options.Directory, FileName(day, part));
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            if (_options.FileSizeLimitBytes is { } limit && stream.Length >= limit)
            {
                stream.Dispose();
                part++;
                continue;
            }
            _writer = new StreamWriter(stream) { AutoFlush = true };
            _day = day;
            _part = part;
            CurrentFile = path;
            break;
        }
        RemoveOldFiles();
    }

    private string FileName(string day, int part)
        => part == 0 ? $"{_options.FilePrefix}-{day}.log" : $"{_options.FilePrefix}-{day}.{part}.log";

    private void RemoveOldFiles()
    {
        var now = _options.TimeProvider.GetUtcNow().UtcDateTime;
        var retentionPrefix = _options.RetentionPrefix ?? _options.FilePrefix;
        foreach (var file in Enumerate(retentionPrefix + "*.log"))
        {
            if (now - file.LastWriteTimeUtc > _options.RetainedFileTimeLimit) TryDelete(file);
        }

        if (_options.RetainedFileCountLimit is not { } keep) return;
        var current = CurrentFile is null ? null : Path.GetFullPath(CurrentFile);
        var own = Enumerate(_options.FilePrefix + "-*.log")
            .Where(f => f.Exists && IsOwnFileName(f.Name))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(keep);
        foreach (var file in own)
        {
            if (!string.Equals(file.FullName, current, StringComparison.OrdinalIgnoreCase)) TryDelete(file);
        }
    }

    // `{FilePrefix}-yyyy-MM-dd.log` or `{FilePrefix}-yyyy-MM-dd.{n}.log` — the names this provider writes. Another writer's
    // `{FilePrefix}-something.log` beside them is left to the age limit, not counted against this provider's files.
    private bool IsOwnFileName(string name)
    {
        // A Windows search pattern can also match on a file's short name, so the long name is checked again here.
        if (!name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(_options.FilePrefix + "-", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = name.AsSpan(_options.FilePrefix.Length + 1, name.Length - _options.FilePrefix.Length - 1 - ".log".Length);
        if (rest.Length < 10 || !DateOnly.TryParseExact(rest[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            return false;
        rest = rest[10..];
        if (rest.IsEmpty) return true;
        return rest[0] == '.' && rest.Length > 1 && int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private FileInfo[] Enumerate(string pattern)
    {
        try
        {
            return new DirectoryInfo(_options.Directory).GetFiles(pattern);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (IOException)
        {
            // Held open by another process; it stays visible on disk and is tried again at the next roll.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: left on disk, tried again later.
        }
    }

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        _ => "NONE",
    };

    /// <summary>Closes the current file. Entries written afterwards are dropped.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
            CurrentFile = null;
        }
        _loggers.Clear();
    }

    private sealed class RollingFileLogger(string category, RollingFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            ArgumentNullException.ThrowIfNull(formatter);
            var message = formatter(state, exception);
            if (exception is not null) message = $"{message} | {exception}";
            provider.Write(category, logLevel, message);
        }
    }
}
