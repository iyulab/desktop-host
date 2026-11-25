namespace DesktopHost.Files;

/// <summary>
/// Moves a file or folder to the platform's trash — the Recycle Bin on Windows, the freedesktop.org home trash on Linux, the Trash on macOS — so the
/// person can restore it. Never deletes: an item the platform would not keep in its trash is left where it is, and the call
/// throws.
/// </summary>
/// <remarks>
/// <para>
/// On Windows the move is the shell's own (<c>IFileOperation</c>, the operation File Explorer runs), with every dialog
/// suppressed, on a single-threaded apartment of its own. The shell deletes an item permanently when recycling is requested
/// but impossible — too large for the bin, a drive without one, a network path — unless a progress sink refuses that deletion;
/// this sink refuses it, so the item stays and <see cref="TrashException"/> says why.
/// </para>
/// <para>
/// On Linux the item is renamed into <c>$XDG_DATA_HOME/Trash/files</c> (default <c>~/.local/share/Trash</c>) with a
/// <c>.trashinfo</c> record of its original path and the time, as the Trash specification describes; a name already in the
/// trash gets a numbered suffix. A folder on another file system than the home trash is refused (the specification's
/// per-volume trash is not implemented); a file there is copied and removed by the runtime's own move.
/// </para>
/// <para>
/// On macOS the move is the system's own (<c>NSFileManager trashItemAtURL</c>, reached through the Objective-C runtime): the
/// system keeps the item's original place, so Finder's «Put Back» works, and no permission prompt is involved. A volume with no
/// Trash leaves the item where it is and <see cref="TrashException"/> says <see cref="TrashException.NotRecyclable"/>.
/// </para>
/// </remarks>
public static class Trash
{
    /// <summary>Moves <paramref name="path"/> (a file or a folder) to the trash.</summary>
    /// <param name="path">An absolute path to an existing file or folder.</param>
    /// <exception cref="ArgumentException">The path is empty or not absolute.</exception>
    /// <exception cref="FileNotFoundException">Nothing is at the path.</exception>
    /// <exception cref="TrashException">The platform did not move it to its trash; the item is where it was.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform has no trash this package can use.</exception>
    public static void Move(string path) => Move(path, progress: null, CancellationToken.None);

    /// <summary>
    /// Moves <paramref name="path"/> (a file or a folder) to the trash, reporting how far it has come and stopping when asked.
    /// </summary>
    /// <remarks>
    /// On Windows the shell first walks a folder's every item — the long part for a folder with many files: <paramref name="progress"/>
    /// receives one report per item it has seen (<see cref="TrashProgress.WorkDone"/> growing while <see cref="TrashProgress.WorkTotal"/>
    /// is still 0) — and then moves the folder to the Recycle Bin, ending on a report of the whole. A cancel is taken at the shell's next
    /// report: during the walk the item stays where it was, whole; a cancel that comes after the move changes nothing. Elsewhere the move
    /// is one step: the progress is reported at its start and its end, and a cancel is taken only before it starts.
    /// </remarks>
    /// <param name="path">An absolute path to an existing file or folder.</param>
    /// <param name="progress">Receives <see cref="TrashProgress"/> reports, on a thread of the call's own; may be null.</param>
    /// <param name="cancellationToken">Stops the move; the call then throws <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="ArgumentException">The path is empty or not absolute.</exception>
    /// <exception cref="FileNotFoundException">Nothing is at the path.</exception>
    /// <exception cref="TrashException">The platform did not move it to its trash; the item is where it was.</exception>
    /// <exception cref="OperationCanceledException">The move was cancelled; the item is where it was.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform has no trash this package can use.</exception>
    public static void Move(string path, IProgress<TrashProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The path must be absolute.", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException("Nothing is at this path.", full);

        cancellationToken.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
        {
            WindowsRecycleBin.Move(full, progress, cancellationToken);
            return;
        }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Moving to the trash is not supported on this platform yet.");
        progress?.Report(new TrashProgress(0, 1));
        if (OperatingSystem.IsLinux())
            FreedesktopTrash.Move(full, FreedesktopTrash.HomeTrash());
        else if (OperatingSystem.IsMacOS())
            _ = MacTrash.Move(full);
        progress?.Report(new TrashProgress(1, 1));
    }
}

/// <summary>
/// How far a move to the trash has come: <see cref="WorkDone"/> of <see cref="WorkTotal"/>, in the platform's own units. On Windows the
/// shell walks a folder's items first, reported as the items seen so far with the total still 0 (a caller that counted the folder
/// itself can show a share), then reports the whole; elsewhere it is 0 of 1, then 1 of 1.
/// </summary>
/// <param name="WorkDone">The work done so far — on Windows, while <paramref name="WorkTotal"/> is 0, the items seen so far.</param>
/// <param name="WorkTotal">All the work the move will take; 0 while the platform is still sizing it.</param>
public readonly record struct TrashProgress(long WorkDone, long WorkTotal)
{
    /// <summary>The share done, from 0 to 1; 0 while <see cref="WorkTotal"/> is unknown.</summary>
    public double Fraction => WorkTotal > 0 ? Math.Clamp((double)WorkDone / WorkTotal, 0, 1) : 0;
}

/// <summary>Why <see cref="Trash.Move(string)"/> did not move an item; the item is where it was.</summary>
public sealed class TrashException : IOException
{
    /// <summary>The platform would not keep this item in its trash (too large, a drive or file system without one).</summary>
    public const string NotRecyclable = "not_recyclable";

    /// <summary>The platform refused the move (permission, the item in use).</summary>
    public const string Refused = "refused";

    /// <summary>Creates the exception.</summary>
    public TrashException(string reason, string message, Exception? inner = null) : base(message, inner) => Reason = reason;

    /// <summary><see cref="NotRecyclable"/> or <see cref="Refused"/>.</summary>
    public string Reason { get; }
}
