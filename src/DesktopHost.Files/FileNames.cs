using System.Text;

namespace DesktopHost.Files;

/// <summary>
/// Names that differ only in their Unicode normalization form — the same name a person reads, stored either composed (NFC, one
/// code point per Hangul syllable or accented letter) or decomposed (NFD, base letter plus combining marks).
/// </summary>
/// <remarks>
/// <para>
/// Which form a file system stores and how it looks a name up differ by volume, not by operating system. APFS keeps the form a
/// name was created with and finds it by either; HFS+ stores decomposed; NTFS, ext4 and network shares (SMB on macOS included)
/// compare code point by code point, so a name typed composed does not open a file stored decomposed. Files arrive in both forms:
/// copied from a Mac, unpacked from an archive, synced by a cloud client.
/// </para>
/// <para>
/// <see cref="AreSame"/> compares two names as one; <see cref="ResolvePath"/> turns a path as typed into the path as stored, so a
/// caller that opens, moves or records a file uses the spelling the file system and its change notifications use.
/// </para>
/// </remarks>
public static class FileNames
{
    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> are the same name once both are composed (NFC). Case is compared
    /// as <paramref name="comparison"/> says — whether a volume folds case is the caller's to know.
    /// </summary>
    public static bool AreSame(string a, string b, StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return string.Equals(Compose(a), Compose(b), comparison);
    }

    /// <summary>
    /// The full path of the entry in <paramref name="directory"/> that <paramref name="name"/> names, spelled as stored: the entry
    /// whose name is exactly <paramref name="name"/>, else the one entry whose name is the same once composed. Null when the
    /// directory does not exist, when no entry matches, or when two or more entries differ from <paramref name="name"/> only in
    /// form (a volume that compares code points can hold both) — the name does not say which one is meant.
    /// </summary>
    /// <remarks>Lists the directory once. An entry the file system opens by a spelling no listed name has (a volume that folds
    /// case) is not found here; <see cref="ResolvePath"/> keeps such a segment as given.</remarks>
    public static string? FindEntry(string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!Directory.Exists(directory)) return null;

        string? sameForm = null;
        var sameFormCount = 0;
        var composed = Compose(name);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var stored = Path.GetFileName(entry);
            if (string.Equals(stored, name, StringComparison.Ordinal)) return entry;
            if (string.Equals(Compose(stored), composed, StringComparison.Ordinal))
            {
                sameForm = entry;
                sameFormCount++;
            }
        }
        return sameFormCount == 1 ? sameForm : null;
    }

    /// <summary>
    /// <paramref name="path"/> with every segment spelled as the file system stores it, or null when some segment names nothing
    /// (or names two entries that differ only in form). A segment the file system opens as given but no listed entry spells that
    /// way (a volume that folds case) is kept as given.
    /// </summary>
    /// <remarks>Lists one directory per segment, so it is for a path a person or a model typed, not for a walk over many files.</remarks>
    public static string? ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return null;

        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var found = FindEntry(current, segment);
            if (found is null)
            {
                var given = Path.Combine(current, segment);
                if (!File.Exists(given) && !Directory.Exists(given)) return null;
                found = given;
            }
            current = found;
        }
        return current;
    }

    private static string Compose(string name) => name.IsNormalized(NormalizationForm.FormC) ? name : name.Normalize(NormalizationForm.FormC);
}
