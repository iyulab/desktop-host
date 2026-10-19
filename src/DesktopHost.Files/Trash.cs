namespace DesktopHost.Files;

/// <summary>
/// Moves a file or folder to the platform's trash — the Recycle Bin on Windows, the freedesktop.org home trash on Linux — so the
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
/// </remarks>
public static class Trash
{
    /// <summary>Moves <paramref name="path"/> (a file or a folder) to the trash.</summary>
    /// <param name="path">An absolute path to an existing file or folder.</param>
    /// <exception cref="ArgumentException">The path is empty or not absolute.</exception>
    /// <exception cref="FileNotFoundException">Nothing is at the path.</exception>
    /// <exception cref="TrashException">The platform did not move it to its trash; the item is where it was.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform's trash is not supported yet (macOS).</exception>
    public static void Move(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The path must be absolute.", nameof(path));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException("Nothing is at this path.", full);

        if (OperatingSystem.IsWindows())
            WindowsRecycleBin.Move(full);
        else if (OperatingSystem.IsLinux())
            FreedesktopTrash.Move(full, FreedesktopTrash.HomeTrash());
        else
            throw new PlatformNotSupportedException("Moving to the trash is not supported on this platform yet.");
    }
}

/// <summary>Why <see cref="Trash.Move"/> did not move an item; the item is where it was.</summary>
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
