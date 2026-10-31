namespace DesktopHost.Signatures;

/// <summary>What <see cref="Authenticode.Verify"/> or <see cref="MacCodeSignature.Verify"/> found.</summary>
public enum SignatureVerdict
{
    /// <summary>A valid signature, from one of the expected publishers.</summary>
    Trusted,

    /// <summary>No signature, or not a file kind that carries one.</summary>
    NotSigned,

    /// <summary>A signature that does not hold: the file changed after signing, the certificate was not issued by an
    /// authority this computer trusts, it is not for code signing, or it expired without a timestamp from while it was
    /// valid.</summary>
    Invalid,

    /// <summary>A valid signature from a publisher other than the expected ones.</summary>
    UnexpectedPublisher,

    /// <summary>Nothing was checked: the signature kind belongs to another operating system (Authenticode is Windows'; a macOS code
    /// signature is checked on macOS only).</summary>
    Unsupported,

    /// <summary>A valid signature from an expected publisher, but Apple has not notarized the file (macOS, when notarization
    /// was required).</summary>
    NotNotarized,
}

/// <summary>The result of checking one file's signature.</summary>
/// <param name="Verdict">What was found.</param>
/// <param name="Signer">The verified signer certificate's subject, when the signature was valid; null otherwise.</param>
/// <param name="Status">The status the check returned — <c>WinVerifyTrust</c>'s HRESULT, or the exit code of the macOS tool that
/// decided (0 when the signature is valid, -1 when a tool did not finish in time) — for a log line, not for branching on:
/// <see cref="Verdict"/> is the answer.</param>
public sealed record SignatureCheck(SignatureVerdict Verdict, string? Signer, int Status)
{
    /// <summary>True only for <see cref="SignatureVerdict.Trusted"/>.</summary>
    public bool IsTrusted => Verdict == SignatureVerdict.Trusted;

    /// <summary>One line for a log: the verdict, the signer when there is one, the status in hex.</summary>
    public override string ToString() =>
        Signer is null ? $"{Verdict} (0x{Status:X8})" : $"{Verdict} — signed by {Signer} (0x{Status:X8})";
}
