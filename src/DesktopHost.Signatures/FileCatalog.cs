using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DesktopHost.Signatures;

/// <summary>What <see cref="FileCatalog.Verify(string, string, IReadOnlyCollection{Publisher})"/> found.</summary>
public enum CatalogVerdict
{
    /// <summary>The catalog's signature is trusted, every file in the directory is listed in it with its hash, and every
    /// file it lists is there.</summary>
    Trusted,

    /// <summary>The catalog itself is not signed by an expected publisher — <see cref="CatalogCheck.Signature"/> says how.</summary>
    CatalogNotTrusted,

    /// <summary>The catalog could not be read as a Windows catalog.</summary>
    CatalogUnreadable,

    /// <summary>A file in the directory is not listed in the catalog.</summary>
    FileNotListed,

    /// <summary>A file's content does not match the hash the catalog lists for it.</summary>
    FileChanged,

    /// <summary>A file the catalog lists is not in the directory.</summary>
    FileMissing,

    /// <summary>Nothing was checked: a Windows catalog is checked by Windows, and this is not Windows.</summary>
    Unsupported,
}

/// <summary>The result of checking a directory against a signed catalog.</summary>
/// <param name="Verdict">What was found.</param>
/// <param name="Signature">The catalog's own signature check (null when it was not reached).</param>
/// <param name="File">The file the verdict is about, relative to the directory, when it is about one; null otherwise.</param>
public sealed record CatalogCheck(CatalogVerdict Verdict, SignatureCheck? Signature, string? File)
{
    /// <summary>True only for <see cref="CatalogVerdict.Trusted"/>.</summary>
    public bool IsTrusted => Verdict == CatalogVerdict.Trusted;

    /// <summary>One line for a log: the verdict, the file it is about, the catalog's signature when that was the problem.</summary>
    public override string ToString() => Verdict switch
    {
        CatalogVerdict.CatalogNotTrusted => $"{Verdict} — {Signature}",
        _ when File is not null => $"{Verdict} — {File}",
        _ => Verdict.ToString(),
    };
}

/// <summary>
/// Checks that a directory is exactly what a signed Windows catalog (<c>.cat</c>) describes — for a host about to run a
/// directory it downloaded (an update, a plugin), where an entry program's own signature leaves the files beside it open:
/// scripts it runs, libraries it loads, data it trusts.
/// </summary>
/// <remarks>
/// <para>
/// The catalog is the standard Windows mechanism (driver packages, PowerShell modules): a signed list of the directory's
/// files, each with its hash. Make one with PowerShell's <c>New-FileCatalog -CatalogVersion 2.0</c> (SHA-256) and sign it like
/// a program. This check asks three things, and all must hold: the catalog's Authenticode signature is valid and from one of
/// the expected publishers (<see cref="Authenticode.Verify"/>, offline); every file under the directory is listed, with the
/// hash Windows computes for it (a program's Authenticode hash, any other file's SHA-256); every file listed is there.
/// </para>
/// <para>
/// Paths are compared as the catalog stores them — relative to the directory, case-insensitive. The catalog file itself may
/// sit inside the directory; it is not one of its own members.
/// </para>
/// </remarks>
public static partial class FileCatalog
{
    /// <summary>
    /// Checks <paramref name="directory"/> against the catalog at <paramref name="catalogPath"/>, signed by one of
    /// <paramref name="expected"/>.
    /// </summary>
    /// <exception cref="ArgumentException">No publisher is given — a check that accepts any signer is not this check.</exception>
    /// <exception cref="DirectoryNotFoundException">There is no directory at <paramref name="directory"/>.</exception>
    /// <exception cref="FileNotFoundException">There is no catalog at <paramref name="catalogPath"/>.</exception>
    public static CatalogCheck Verify(string directory, string catalogPath, IReadOnlyCollection<Publisher> expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Count == 0)
            throw new ArgumentException("Name at least one expected publisher.", nameof(expected));
        return Verify(directory, catalogPath, path => Authenticode.Verify(path, expected));
    }

    /// <summary>The check with the catalog's signature check supplied — the seam the tests use.</summary>
    internal static CatalogCheck Verify(string directory, string catalogPath, Func<string, SignatureCheck> signatureOf)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(catalogPath);
        var root = Path.GetFullPath(directory);
        var catalog = Path.GetFullPath(catalogPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"No directory to check: {root}");
        if (!File.Exists(catalog)) throw new FileNotFoundException("No catalog to check against.", catalog);
        if (!OperatingSystem.IsWindows()) return new CatalogCheck(CatalogVerdict.Unsupported, null, null);

        var signature = signatureOf(catalog);
        if (!signature.IsTrusted) return new CatalogCheck(CatalogVerdict.CatalogNotTrusted, signature, null);
        return Compare(root, catalog) with { Signature = signature };
    }

    /// <summary>The directory against the catalog's members, the signature aside.</summary>
    [SupportedOSPlatform("windows")]
    internal static CatalogCheck Compare(string root, string catalog)
    {
        var members = ReadMembers(catalog);
        if (members is null) return new CatalogCheck(CatalogVerdict.CatalogUnreadable, null, null);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (string.Equals(file, catalog, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(root, file).Replace('/', '\\');
            if (!members.TryGetValue(relative, out var listed)) return new CatalogCheck(CatalogVerdict.FileNotListed, null, relative);
            if (!string.Equals(HashOf(file), listed, StringComparison.OrdinalIgnoreCase))
                return new CatalogCheck(CatalogVerdict.FileChanged, null, relative);
            seen.Add(relative);
        }

        var missing = members.Keys.Where(k => !seen.Contains(k)).Order(StringComparer.Ordinal).FirstOrDefault();
        return missing is null
            ? new CatalogCheck(CatalogVerdict.Trusted, null, null)
            : new CatalogCheck(CatalogVerdict.FileMissing, null, missing);
    }

    private const uint CryptCatOpenExisting = 0x00000002;
    private const uint CryptCatVersion2 = 0x00000200;                  // a SHA-256 catalog (New-FileCatalog -CatalogVersion 2.0)
    private const uint EncodingX509AndPkcs7 = 0x00000001 | 0x00010000;  // X509_ASN_ENCODING | PKCS_7_ASN_ENCODING
    private const string FileAttribute = "FilePath";
    private const int Sha256HexLength = 64;

    /// <summary>The catalog's SHA-256 members by the relative path their <c>FilePath</c> attribute names, each with its hash (the
    /// member's reference tag, hex); null when the file is not a catalog Windows can read, or lists nothing.</summary>
    /// <remarks><c>New-FileCatalog -CatalogVersion 2.0</c> lists each file twice, under its SHA-1 and its SHA-256 hash; only the
    /// SHA-256 member is read, so a path listed only by SHA-1 is not listed.</remarks>
    [SupportedOSPlatform("windows")]
    private static Dictionary<string, string>? ReadMembers(string catalog)
    {
        var handle = CryptCATOpen(catalog, CryptCatOpenExisting, IntPtr.Zero, CryptCatVersion2, EncodingX509AndPkcs7);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
        try
        {
            var members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var member = IntPtr.Zero;
            while ((member = CryptCATEnumerateMember(handle, member)) != IntPtr.Zero)
            {
                var head = Marshal.PtrToStructure<CryptCatMemberHead>(member);
                var tag = Marshal.PtrToStringUni(head.ReferenceTag);
                string? path = null;
                var attribute = IntPtr.Zero;
                while ((attribute = CryptCATEnumerateAttr(handle, member, attribute)) != IntPtr.Zero)
                {
                    var attr = Marshal.PtrToStructure<CryptCatAttribute>(attribute);
                    if (!string.Equals(Marshal.PtrToStringUni(attr.ReferenceTag), FileAttribute, StringComparison.OrdinalIgnoreCase)) continue;
                    // The value is UTF-16 with its terminator counted.
                    path = Marshal.PtrToStringUni(attr.Value, (int)attr.ValueSize / 2).TrimEnd('\0');
                }
                if (tag is null || path is null) return null; // a member that names no file describes nothing in a directory
                if (tag.Length != Sha256HexLength) continue;   // the SHA-1 twin
                // Two SHA-256 members under one path cannot both be its hash.
                if (!members.TryAdd(path.Replace('/', '\\'), tag)) return null;
            }
            // Windows opens some files that are not catalogs and enumerates nothing in them; a catalog that lists nothing
            // describes no directory either way.
            return members.Count == 0 ? null : members;
        }
        finally { CryptCATClose(handle); }
    }

    /// <summary>The hash Windows records for a file in a SHA-256 catalog: a program's Authenticode hash, any other file's
    /// SHA-256 — the same call that made the catalog's entry.</summary>
    [SupportedOSPlatform("windows")]
    private static string HashOf(string path)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
            throw new InvalidOperationException($"No SHA-256 catalog context (error {Marshal.GetLastPInvokeError()}).");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var handle = stream.SafeFileHandle;
            uint size = 0;
            CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, null, 0);
            if (size == 0) throw new IOException($"Windows computed no catalog hash for {path} (error {Marshal.GetLastPInvokeError()}).");
            var hash = new byte[size];
            if (!CryptCATAdminCalcHashFromFileHandle2(admin, handle, ref size, hash, 0))
                throw new IOException($"Windows computed no catalog hash for {path} (error {Marshal.GetLastPInvokeError()}).");
            return Convert.ToHexString(hash, 0, (int)size);
        }
        finally { CryptCATAdminReleaseContext(admin, 0); }
    }

    // The head of CRYPTCATMEMBER: its size, then the reference tag — read for the tag's offset only.
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptCatMemberHead
    {
        public uint CbStruct;
        public IntPtr ReferenceTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptCatAttribute
    {
        public uint CbStruct;
        public IntPtr ReferenceTag;
        public uint AttrTypeAndAction;
        public uint ValueSize;
        public IntPtr Value;
        public uint Reserved;
    }

    [LibraryImport("wintrust.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static partial IntPtr CryptCATOpen(string fileName, uint openFlags, IntPtr provider, uint publicVersion, uint encodingType);

    [LibraryImport("wintrust.dll")]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATClose(IntPtr catalog);

    [LibraryImport("wintrust.dll")]
    [SupportedOSPlatform("windows")]
    private static partial IntPtr CryptCATEnumerateMember(IntPtr catalog, IntPtr previousMember);

    [LibraryImport("wintrust.dll")]
    [SupportedOSPlatform("windows")]
    private static partial IntPtr CryptCATEnumerateAttr(IntPtr catalog, IntPtr member, IntPtr previousAttribute);

    [LibraryImport("wintrust.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminAcquireContext2(
        out IntPtr catAdmin, IntPtr subsystem, string hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminCalcHashFromFileHandle2(
        IntPtr catAdmin, SafeFileHandle file, ref uint hashSize, [Out] byte[]? hash, uint flags);

    [LibraryImport("wintrust.dll")]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);
}
