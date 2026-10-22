using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace DesktopHost.Signatures;

/// <summary>
/// Checks a file's Authenticode signature the way Windows checks it before running a program, and that the signer is one of
/// the publishers the caller expects — for a host about to run what it downloaded (an update, a plugin, a model runner).
/// </summary>
/// <remarks>
/// <para>
/// The signature is checked by <c>WinVerifyTrust</c> (<c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c>): the file's hash against the
/// signed one, a certificate chain to a root this computer trusts, issued for code signing, valid when signed (a timestamp
/// counts). The signer is the certificate that check verified — never a certificate merely found in the file.
/// </para>
/// <para>
/// <b>Offline.</b> No network is used: revocation is not checked and nothing is fetched to build the chain (the intermediate
/// certificates a signature embeds, and the ones Windows already has, are used). A computer without a connection, or behind a
/// proxy that blocks certificate servers, can still verify what it downloaded through another path; the cost is that a
/// certificate revoked after a file was signed still passes here. Windows' own checks when the program starts are unchanged.
/// </para>
/// <para>Only files with an embedded signature are checked; a file covered only by a Windows catalog reads as not signed.</para>
/// </remarks>
public static partial class Authenticode
{
    /// <summary>
    /// Checks the signature of the file at <paramref name="path"/> and that its signer is one of <paramref name="expected"/>.
    /// </summary>
    /// <exception cref="ArgumentException">No publisher is given — a check that accepts any signer is not this check.</exception>
    /// <exception cref="FileNotFoundException">There is no file at <paramref name="path"/>.</exception>
    public static SignatureCheck Verify(string path, IReadOnlyCollection<Publisher> expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Count == 0)
            throw new ArgumentException("Name at least one expected publisher.", nameof(expected));
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("No file to check.", full);
        if (!OperatingSystem.IsWindows()) return new SignatureCheck(SignatureVerdict.Unsupported, null, 0);
        return VerifyOnWindows(full, expected);
    }

    // HRESULTs WinVerifyTrust answers for a file with no signature to check.
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);

    [SupportedOSPlatform("windows")]
    private static SignatureCheck VerifyOnWindows(string path, IReadOnlyCollection<Publisher> expected)
    {
        var action = GenericVerifyV2;
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WintrustFileInfo
        {
            CbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            FilePath = pathPtr,
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        var data = new WintrustData
        {
            CbStruct = (uint)Marshal.SizeOf<WintrustData>(),
            UiChoice = WtdUiNone,
            RevocationChecks = WtdRevokeNone,
            UnionChoice = WtdChoiceFile,
            File = fileInfoPtr,
            StateAction = WtdStateActionVerify,
            ProvFlags = WtdCacheOnlyUrlRetrieval | WtdRevocationCheckNone | WtdDisableMd2Md4,
        };
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);
            var status = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            if (status is TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown)
                return new SignatureCheck(SignatureVerdict.NotSigned, null, status);
            if (status != 0)
                return new SignatureCheck(SignatureVerdict.Invalid, null, status);

            using var signer = SignerOf(data.StateData);
            if (signer is null)
                return new SignatureCheck(SignatureVerdict.Invalid, null, status);
            return expected.Any(p => p.Matches(signer.SubjectName))
                ? new SignatureCheck(SignatureVerdict.Trusted, signer.Subject, status)
                : new SignatureCheck(SignatureVerdict.UnexpectedPublisher, signer.Subject, status);
        }
        finally
        {
            data.StateAction = WtdStateActionClose;
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    /// <summary>The certificate of the first signer the verification trusted (a copy the caller disposes).</summary>
    [SupportedOSPlatform("windows")]
    private static X509Certificate2? SignerOf(IntPtr stateData)
    {
        var provider = WTHelperProvDataFromStateData(stateData);
        if (provider == IntPtr.Zero) return null;
        var signer = WTHelperGetProvSignerFromChain(provider, 0, fCounterSigner: false, 0);
        if (signer == IntPtr.Zero) return null;
        var providerCert = WTHelperGetProvCertFromChain(signer, 0);
        if (providerCert == IntPtr.Zero) return null;
        var context = Marshal.ReadIntPtr(providerCert, (int)Marshal.OffsetOf<CryptProviderCertHead>(nameof(CryptProviderCertHead.Cert)));
        // The constructor duplicates the context, so the copy outlives the state data closed after this.
        return context == IntPtr.Zero ? null : new X509Certificate2(context);
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    private const uint WtdDisableMd2Md4 = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint CbStruct;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint CbStruct;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    // The head of CRYPT_PROVIDER_CERT: its size, then the certificate context — read for the field's offset only.
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertHead
    {
        public uint CbStruct;
        public IntPtr Cert;
    }

    [LibraryImport("wintrust.dll")]
    [SupportedOSPlatform("windows")]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WintrustData data);

    [LibraryImport("wintrust.dll")]
    [SupportedOSPlatform("windows")]
    private static partial IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [LibraryImport("wintrust.dll")]
    [SupportedOSPlatform("windows")]
    private static partial IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll")]
    [SupportedOSPlatform("windows")]
    private static partial IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);
}
