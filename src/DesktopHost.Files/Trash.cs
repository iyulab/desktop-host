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
    /// <summary>Moves <paramref name="path"/> (a file or a folder) to the trash, and says where it went.</summary>
    /// <param name="path">An absolute path to an existing file or folder.</param>
    /// <returns>Where the item is now and where it came from — what <see cref="PutBack(TrashedItem)"/> takes to undo the move.</returns>
    /// <exception cref="ArgumentException">The path is empty or not absolute.</exception>
    /// <exception cref="FileNotFoundException">Nothing is at the path.</exception>
    /// <exception cref="TrashException">The platform did not move it to its trash; the item is where it was.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform has no trash this package can use.</exception>
    public static TrashedItem Move(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The path must be absolute.", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException("Nothing is at this path.", full);

        if (OperatingSystem.IsWindows())
            return WindowsRecycleBin.Move(full);
        if (OperatingSystem.IsLinux())
            return FreedesktopTrash.Move(full, FreedesktopTrash.HomeTrash());
        if (OperatingSystem.IsMacOS())
            return new TrashedItem(full, MacTrash.Move(full), null);
        throw new PlatformNotSupportedException("Moving to the trash is not supported on this platform yet.");
    }

    /// <summary>
    /// Puts an item <see cref="Move"/> moved back where it was — the undo of a move to the trash, as File Explorer's and Finder's
    /// Undo do. The trash's own record of the item (the Recycle Bin's <c>$I</c> file, the <c>.trashinfo</c> record) is removed.
    /// A folder that held the item and has since gone is made again.
    /// </summary>
    /// <param name="item">What <see cref="Move"/> returned.</param>
    /// <exception cref="ArgumentException">The item's place in the trash is not inside a trash (the handle is not one
    /// <see cref="Move"/> gave), or a path is not absolute.</exception>
    /// <exception cref="FileNotFoundException">The item is no longer in the trash (it was emptied, or put back already).</exception>
    /// <exception cref="TrashException"><see cref="TrashException.Occupied"/>: something is at the original place now; the item stays
    /// in the trash. <see cref="TrashException.Refused"/>: the platform refused the move back.</exception>
    public static void PutBack(TrashedItem item) => PutBack(item, IsInsideATrash);

    /// <summary>The same with the «is it in a trash» check passed in — the seam a test with a trash folder of its own uses.</summary>
    internal static void PutBack(TrashedItem item, Func<TrashedItem, bool> insideATrash)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Path.IsPathFullyQualified(item.OriginalPath) || !Path.IsPathFullyQualified(item.TrashPath))
            throw new ArgumentException("The paths must be absolute.", nameof(item));
        if (!insideATrash(item))
            throw new ArgumentException("This item's place is not inside a trash.", nameof(item));
        if (!File.Exists(item.TrashPath) && !Directory.Exists(item.TrashPath))
            throw new FileNotFoundException("The item is no longer in the trash.", item.TrashPath);
        if (File.Exists(item.OriginalPath) || Directory.Exists(item.OriginalPath))
            throw new TrashException(TrashException.Occupied, "Something is already at the item's original place, so it stays in the trash.");

        try
        {
            var parent = Path.GetDirectoryName(item.OriginalPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (Directory.Exists(item.TrashPath)) Directory.Move(item.TrashPath, item.OriginalPath);
            else File.Move(item.TrashPath, item.OriginalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TrashException(TrashException.Refused, "The item could not be put back.", ex);
        }

        if (item.RecordPath is { } record)
        {
            try { File.Delete(record); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The item is back; a record left behind only shows a stale entry in the trash's list until it is emptied.
            }
        }
    }

    /// <summary>
    /// Whether the handle names a place in a trash: the Recycle Bin (<c>$Recycle.Bin</c>), a Mac Trash (<c>~/.Trash</c>, a volume's
    /// <c>.Trashes</c>), or the freedesktop home trash's <c>files/</c> with its record in <c>info/</c>. A put-back moves a file to
    /// a path the caller names, so the source has to be one the trash holds.
    /// </summary>
    internal static bool IsInsideATrash(TrashedItem item)
    {
        var at = item.TrashPath;
        if (OperatingSystem.IsWindows())
            return at.Contains(@"\$Recycle.Bin", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsMacOS())
        {
            var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash") + "/";
            return at.StartsWith(home, StringComparison.Ordinal) || at.Contains("/.Trashes/", StringComparison.Ordinal);
        }
        return FreedesktopTrash.Holds(FreedesktopTrash.HomeTrash(), item);
    }
}

/// <summary>
/// An item in the trash: where it is now (<see cref="TrashPath"/>), where it was (<see cref="OriginalPath"/>), and the trash's own
/// record of it when the platform keeps one beside it (<see cref="RecordPath"/> — the Recycle Bin's <c>$I</c> file, the
/// <c>.trashinfo</c> record; none on a Mac, where the system keeps it). Plain data, so a caller can hold it across a request.
/// </summary>
public sealed record TrashedItem(string OriginalPath, string TrashPath, string? RecordPath);

/// <summary>Why <see cref="Trash.Move"/> did not move an item; the item is where it was.</summary>
public sealed class TrashException : IOException
{
    /// <summary>The platform would not keep this item in its trash (too large, a drive or file system without one).</summary>
    public const string NotRecyclable = "not_recyclable";

    /// <summary>The platform refused the move (permission, the item in use).</summary>
    public const string Refused = "refused";

    /// <summary><see cref="Trash.PutBack(TrashedItem)"/>: something is at the item's original place now; the item stays in the trash.</summary>
    public const string Occupied = "occupied";

    /// <summary>Creates the exception.</summary>
    public TrashException(string reason, string message, Exception? inner = null) : base(message, inner) => Reason = reason;

    /// <summary><see cref="NotRecyclable"/>, <see cref="Refused"/> or <see cref="Occupied"/>.</summary>
    public string Reason { get; }
}

/// <summary>The Recycle Bin's naming of an item and its record — plain path logic, so it is tested on every platform.</summary>
internal static class RecycleBinRecord
{
    /// <summary>
    /// The Recycle Bin keeps an item as <c>$R…</c> and its record (the original path and the time) as <c>$I…</c> with the same rest of
    /// the name, side by side in the account's <c>$Recycle.Bin</c> folder of that drive.
    /// </summary>
    public static string? Of(string recycled)
    {
        var name = Path.GetFileName(recycled);
        if (!name.StartsWith("$R", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(Path.GetDirectoryName(recycled) ?? "", "$I" + name[2..]);
    }
}
