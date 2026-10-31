using System.Diagnostics;

namespace DesktopHost.Signatures;

/// <summary>
/// Checks a macOS program or application bundle's code signature the way the system checks it, and that it was signed by one of the
/// Apple developer teams the caller expects — for a host about to run what it downloaded. The macOS counterpart of
/// <see cref="Authenticode"/>, with the same <see cref="SignatureCheck"/> answer.
/// </summary>
/// <remarks>
/// <para>
/// The system's own tools do the checking: <c>codesign --verify --strict --deep</c> for the signature (the content unchanged since
/// signing, every nested part covered, a chain to Apple's root), then <c>codesign</c> again with a code requirement that holds only
/// for a Developer ID signature from the expected team — Apple's own Developer ID shape (an Apple-anchored chain through the
/// Developer ID certification authority, a Developer ID Application leaf) with the leaf's team added. The verdict is the tool's
/// answer to that requirement, never text read out of a printout. Optionally the file must also be notarized (<c>spctl</c>'s
/// Gatekeeper assessment says <c>Notarized Developer ID</c>).
/// </para>
/// <para>
/// Nothing here needs a network: the signature and the team are checked locally. Whether Gatekeeper looks up a notarization ticket
/// that is not stapled to the file is the system's policy, not this method's.
/// </para>
/// <para>The tools are run by absolute path, with an argument list and no shell, and a tool that does not finish in time counts as
/// a signature that does not hold.</para>
/// </remarks>
public static class MacCodeSignature
{
    private const string Codesign = "/usr/bin/codesign";
    private const string Spctl = "/usr/sbin/spctl";

    // codesign --verify answers 3 when the signature holds but the code does not satisfy the requirement it was asked about.
    private const int RequirementNotSatisfied = 3;
    private const int TimedOut = -1;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Checks the signature of the file or bundle at <paramref name="path"/> and that its team is one of <paramref name="expected"/>.
    /// </summary>
    /// <param name="path">A Mach-O program or an application bundle.</param>
    /// <param name="expected">The teams the caller trusts to have signed it. At least one.</param>
    /// <param name="requireNotarization">Also require that Apple has notarized it; a signed, expected, un-notarized file is then
    /// <see cref="SignatureVerdict.NotNotarized"/>.</param>
    /// <param name="timeout">How long each system tool may take (default one minute).</param>
    /// <exception cref="ArgumentException">No team is given — a check that accepts any signer is not this check.</exception>
    /// <exception cref="FileNotFoundException">There is no file or bundle at <paramref name="path"/>.</exception>
    public static SignatureCheck Verify(
        string path, IReadOnlyCollection<AppleTeam> expected, bool requireNotarization = false, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Count == 0)
            throw new ArgumentException("Name at least one expected team.", nameof(expected));
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("No file to check.", full);
        if (!OperatingSystem.IsMacOS()) return new SignatureCheck(SignatureVerdict.Unsupported, null, 0);

        var limit = timeout ?? DefaultTimeout;

        // 1. Is there a signature, and does it hold?
        var intact = Run(Codesign, ["--verify", "--strict", "--deep", full], limit);
        if (intact.ExitCode != 0)
        {
            var verdict = intact.ExitCode != TimedOut && intact.Output.Contains("not signed at all", StringComparison.Ordinal)
                ? SignatureVerdict.NotSigned
                : SignatureVerdict.Invalid;
            return new SignatureCheck(verdict, null, intact.ExitCode);
        }

        // 2. Is it a Developer ID signature from one of the expected teams?
        var signer = SignerOf(full, limit);
        var satisfied = false;
        var status = RequirementNotSatisfied;
        foreach (var team in expected)
        {
            var result = Run(Codesign, ["--verify", "--strict", "--deep", "-R=" + Requirement(team), full], limit);
            if (result.ExitCode == 0) { satisfied = true; break; }
            status = result.ExitCode;
        }
        if (!satisfied)
        {
            // A requirement the code does not satisfy is a valid signature from someone else; any other failure is not an answer.
            return new SignatureCheck(
                status == RequirementNotSatisfied ? SignatureVerdict.UnexpectedPublisher : SignatureVerdict.Invalid, signer, status);
        }

        // 3. Notarized, when asked.
        if (requireNotarization)
        {
            var assessment = Run(Spctl, ["--assess", "--type", "execute", "--verbose=4", full], limit);
            if (assessment.ExitCode != 0 || !assessment.Output.Contains("source=Notarized Developer ID", StringComparison.Ordinal))
                return new SignatureCheck(SignatureVerdict.NotNotarized, signer, assessment.ExitCode);
        }
        return new SignatureCheck(SignatureVerdict.Trusted, signer, 0);
    }

    /// <summary>
    /// The code requirement that only a Developer ID signature from <paramref name="team"/> satisfies: an Apple-anchored chain, the
    /// Developer ID certification authority as the intermediate (OID 1.2.840.113635.100.6.2.6), a Developer ID Application leaf
    /// (1.2.840.113635.100.6.1.13), and the team as the leaf's organizational unit.
    /// </summary>
    internal static string Requirement(AppleTeam team) =>
        "anchor apple generic"
        + " and certificate 1[field.1.2.840.113635.100.6.2.6] /* exists */"
        + " and certificate leaf[field.1.2.840.113635.100.6.1.13] /* exists */"
        + $" and certificate leaf[subject.OU] = \"{team.TeamIdentifier}\"";

    // The first Authority line is the signing certificate's name, e.g. "Developer ID Application: Example Software Ltd (ABCDE12345)".
    private static string? SignerOf(string path, TimeSpan timeout)
    {
        var shown = Run(Codesign, ["-dvv", path], timeout);
        const string key = "Authority=";
        foreach (var line in shown.Output.Split('\n'))
            if (line.StartsWith(key, StringComparison.Ordinal))
                return line[key.Length..].Trim();
        return null;
    }

    private static (int ExitCode, string Output) Run(string tool, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} did not start.");
        // Both streams are read as they fill, so a tool that writes a lot cannot block on a full pipe while it is waited for.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return (TimedOut, "");
        }
        process.WaitForExit(); // let the redirected streams finish
        return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }
}
