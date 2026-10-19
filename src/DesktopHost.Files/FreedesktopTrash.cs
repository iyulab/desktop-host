using System.Globalization;
using System.Text;

namespace DesktopHost.Files;

/// <summary>
/// The freedesktop.org Trash specification's home trash: <c>$XDG_DATA_HOME/Trash</c> (default <c>~/.local/share/Trash</c>), with
/// <c>files/</c> for the items and <c>info/</c> for one <c>.trashinfo</c> record each.
/// </summary>
internal static class FreedesktopTrash
{
    /// <summary>The home trash directory for this user.</summary>
    public static string HomeTrash()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome) || !Path.IsPathFullyQualified(dataHome))
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataHome, "Trash");
    }

    /// <summary>
    /// Moves <paramref name="fullPath"/> into <paramref name="trash"/>: the record is written first (created exclusively, which
    /// also claims the name), then the item renamed into <c>files/</c> under the same name; a failed rename removes the record.
    /// </summary>
    public static void Move(string fullPath, string trash)
    {
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        var name = Path.GetFileName(fullPath);
        var record = Record(fullPath, DateTime.Now);
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? name : $"{Path.GetFileNameWithoutExtension(name)}.{n}{Path.GetExtension(name)}";
            var infoPath = Path.Combine(info, candidate + ".trashinfo");
            var target = Path.Combine(files, candidate);
            if (File.Exists(target) || Directory.Exists(target)) continue;
            FileStream claim;
            try
            {
                claim = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(infoPath))
            {
                continue;   // another item with this name is being trashed — the next number
            }
            using (claim) claim.Write(record);

            try
            {
                if (Directory.Exists(fullPath)) Directory.Move(fullPath, target);
                else File.Move(fullPath, target);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Delete(infoPath);
                // A file on another file system is copied by File.Move itself; a folder is not (Directory.Move refuses across
                // file systems), and the specification's per-volume trash is not implemented — the folder stays where it is.
                throw new TrashException(TrashException.Refused, "The item could not be moved to the trash.", ex);
            }
        }
    }

    /// <summary>The <c>.trashinfo</c> record: the original path, percent-encoded as the specification asks, and the local time.</summary>
    internal static byte[] Record(string fullPath, DateTime deletedLocal) => Encoding.UTF8.GetBytes(
        "[Trash Info]\n" +
        $"Path={Uri.EscapeDataString(fullPath).Replace("%2F", "/", StringComparison.Ordinal)}\n" +
        $"DeletionDate={deletedLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n");
}
