using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopHost.Files.Tests;

// The lock tests are deterministic, without a thread race: the lock is taken on this thread BEFORE the operation is
// called, and released by a background task after a fixed delay comfortably inside the retry budget. A bare,
// non-retrying call would throw once and never see the file unlocked.
public sealed class FileRetryTests : IDisposable
{
    private readonly string _root;
    private readonly ILogger _logger = NullLogger.Instance;

    public FileRetryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "desktophost-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DeleteDirectory_NoLock_DeletesImmediately()
    {
        var dir = Path.Combine(_root, "plain");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");

        FileRetry.DeleteDirectory(dir, _logger);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteDirectory_RetriesThroughATransientLock_AndEventuallySucceeds()
    {
        var dir = Path.Combine(_root, "locked");
        Directory.CreateDirectory(dir);
        var lockedFile = Path.Combine(dir, "locked.bin");
        File.WriteAllText(lockedFile, "x");

        var handle = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            await handle.DisposeAsync();
        });

        FileRetry.DeleteDirectory(dir, _logger); // must not throw

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void MoveDirectory_NoLock_MovesImmediately()
    {
        var source = Path.Combine(_root, "src-dir");
        var destination = Path.Combine(_root, "dst-dir");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "x");

        FileRetry.MoveDirectory(source, destination, _logger);

        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(destination, "a.txt")));
    }

    [Fact]
    public void MoveDirectory_FileInsideSourceLocked_RetriesUntilReleasedAndSucceeds()
    {
        // A file *inside* the directory being moved is locked when the move is attempted, then released within the
        // retry budget.
        var source = Path.Combine(_root, "src-dir2");
        var destination = Path.Combine(_root, "dst-dir2");
        Directory.CreateDirectory(source);
        var lockedFile = Path.Combine(source, "locked.bin");
        File.WriteAllText(lockedFile, "x");

        var handle = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            await handle.DisposeAsync();
        });

        FileRetry.MoveDirectory(source, destination, _logger); // must not throw

        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(destination, "locked.bin")));
    }

    [Fact]
    public void TryDeleteDirectory_MissingDirectory_AnswersFalseInsteadOfThrowing()
    {
        Assert.False(FileRetry.TryDeleteDirectory(Path.Combine(_root, "never-created"), _logger));
    }

    [Fact]
    public void TryDeleteFile_MissingFile_IsNotAFailure()
    {
        Assert.True(FileRetry.TryDeleteFile(Path.Combine(_root, "never-created.txt"), _logger));
    }

    [Fact]
    public void A_single_attempt_budget_lets_the_lock_failure_propagate()
    {
        // Windows only: an open handle does not refuse unlink(2) elsewhere.
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "held.bin");
        File.WriteAllText(path, "x");
        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var once = FileRetryOptions.Default with { MaxAttempts = 1 };

        Assert.ThrowsAny<IOException>(() => FileRetry.DeleteFile(path, _logger, once));
        Assert.False(FileRetry.TryDeleteFile(path, _logger, retry: true, options: once));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void A_budget_below_one_attempt_is_refused()
    {
        var none = FileRetryOptions.Default with { MaxAttempts = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => FileRetry.DeleteFile(Path.Combine(_root, "x"), _logger, none));
    }
}
