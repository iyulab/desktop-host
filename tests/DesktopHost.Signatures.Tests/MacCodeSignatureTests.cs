using System.Diagnostics;

namespace DesktopHost.Signatures.Tests;

/// <summary>
/// The macOS code-signature check against real files, made at test time. A file with no signature, an ad hoc one and a tampered one
/// need no identity and run on any Mac. A Developer ID signature needs one: set <c>DESKTOPHOST_TEST_MACOS_IDENTITY</c> (the
/// identity's name) and <c>DESKTOPHOST_TEST_MACOS_TEAM</c> (its team identifier) to run those. A notarized application is any one
/// installed on the machine: set <c>DESKTOPHOST_TEST_MACOS_NOTARIZED_APP</c> (its path) and
/// <c>DESKTOPHOST_TEST_MACOS_NOTARIZED_TEAM</c> (its team identifier). Everything else is skipped, with the reason.
/// </summary>
public sealed class MacCodeSignatureTests : IDisposable
{
    private readonly string _root;

    private static readonly AppleTeam SomeTeam = new("ABCDE12345");

    public MacCodeSignatureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "desktophost-mac-signatures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* the temp folder is the OS's to clean */ }
    }

    // A small program of the machine's own, copied under a new name so it can be re-signed or damaged.
    private string Copy(string name)
    {
        var path = Path.Combine(_root, name);
        File.Copy("/bin/echo", path);
        Tool("/usr/bin/codesign", "--remove-signature", path);
        return path;
    }

    private static void Tool(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var text = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{file} {string.Join(' ', arguments)}: {text}");
    }

    private string AdHocSigned(string name)
    {
        var path = Copy(name);
        Tool("/usr/bin/codesign", "--force", "--sign", "-", path);
        return path;
    }

    [Fact]
    public void A_team_identifier_is_ten_upper_case_letters_and_digits_and_nothing_else()
    {
        Assert.Equal("ABCDE12345", new AppleTeam("ABCDE12345").TeamIdentifier);
        foreach (var bad in new[] { "", "abcde12345", "ABCDE1234", "ABCDE123456", "ABCDE1234\"", "ABCDE 2345", "ABCDE1234]" })
            Assert.Throws<ArgumentException>(() => new AppleTeam(bad));
    }

    [Fact]
    public void The_requirement_is_a_developer_id_signature_from_the_team()
    {
        var requirement = MacCodeSignature.Requirement(SomeTeam);

        Assert.StartsWith("anchor apple generic", requirement, StringComparison.Ordinal);
        Assert.Contains("1.2.840.113635.100.6.2.6", requirement, StringComparison.Ordinal);   // the Developer ID certification authority
        Assert.Contains("1.2.840.113635.100.6.1.13", requirement, StringComparison.Ordinal);  // a Developer ID Application leaf
        Assert.EndsWith("certificate leaf[subject.OU] = \"ABCDE12345\"", requirement, StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_without_a_team_or_a_file_is_refused_not_answered()
    {
        Assert.Throws<ArgumentException>(() => MacCodeSignature.Verify("/bin/echo", []));
        Assert.Throws<FileNotFoundException>(() => MacCodeSignature.Verify(Path.Combine(_root, "absent"), [SomeTeam]));
    }

    [Fact]
    public void Off_macOS_nothing_is_checked()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "this is the case for the other operating systems");
        var file = Path.Combine(_root, "any");
        File.WriteAllText(file, "x");

        var check = MacCodeSignature.Verify(file, [SomeTeam]);

        Assert.Equal(SignatureVerdict.Unsupported, check.Verdict);
        Assert.False(check.IsTrusted);
    }

    [Fact]
    public void A_program_with_no_signature_is_not_signed()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "a macOS code signature");
        var check = MacCodeSignature.Verify(Copy("unsigned"), [SomeTeam]);

        Assert.Equal(SignatureVerdict.NotSigned, check.Verdict);
        Assert.Null(check.Signer);
    }

    [Fact]
    public void An_ad_hoc_signature_holds_but_is_nobodys_expected_publisher()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "a macOS code signature");
        var check = MacCodeSignature.Verify(AdHocSigned("adhoc"), [SomeTeam]);

        Assert.Equal(SignatureVerdict.UnexpectedPublisher, check.Verdict);
        Assert.False(check.IsTrusted);
        Assert.Equal(3, check.Status);
    }

    [Fact]
    public void A_signed_program_changed_afterwards_is_invalid()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "a macOS code signature");
        var path = AdHocSigned("tampered");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(100, SeekOrigin.Begin);
            var b = stream.ReadByte();
            stream.Seek(100, SeekOrigin.Begin);
            stream.WriteByte((byte)(b ^ 0xFF));
        }

        var check = MacCodeSignature.Verify(path, [SomeTeam]);

        Assert.Equal(SignatureVerdict.Invalid, check.Verdict);
        Assert.NotEqual(0, check.Status);
    }

    [Fact]
    public void A_tool_that_does_not_finish_in_time_is_a_signature_that_does_not_hold()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "a macOS code signature");
        var check = MacCodeSignature.Verify(AdHocSigned("slow"), [SomeTeam], timeout: TimeSpan.Zero);

        Assert.Equal(SignatureVerdict.Invalid, check.Verdict);
        Assert.Equal(-1, check.Status);
    }

    [Fact]
    public void A_developer_id_signature_from_an_expected_team_is_trusted_and_from_anyone_else_is_not()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "a macOS code signature");
        var identity = Environment.GetEnvironmentVariable("DESKTOPHOST_TEST_MACOS_IDENTITY");
        var team = Environment.GetEnvironmentVariable("DESKTOPHOST_TEST_MACOS_TEAM");
        Assert.SkipWhen(string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(team),
            "set DESKTOPHOST_TEST_MACOS_IDENTITY and DESKTOPHOST_TEST_MACOS_TEAM to sign with a Developer ID");
        var path = Copy("developer-id");
        Tool("/usr/bin/codesign", "--force", "--sign", identity!, path);

        var right = MacCodeSignature.Verify(path, [SomeTeam, new AppleTeam(team!)]);
        var wrong = MacCodeSignature.Verify(path, [SomeTeam]);
        var unnotarized = MacCodeSignature.Verify(path, [new AppleTeam(team!)], requireNotarization: true);

        Assert.Equal(SignatureVerdict.Trusted, right.Verdict);
        Assert.Contains(team!, right.Signer, StringComparison.Ordinal);
        Assert.Equal(SignatureVerdict.UnexpectedPublisher, wrong.Verdict);
        Assert.Contains(team!, wrong.Signer, StringComparison.Ordinal); // a valid signature, from someone else: the signer is named
        Assert.Equal(SignatureVerdict.NotNotarized, unnotarized.Verdict);
        Assert.False(unnotarized.IsTrusted);
    }

    [Fact]
    public void A_notarized_application_from_its_team_is_trusted_when_notarization_is_required()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "a macOS code signature");
        var app = Environment.GetEnvironmentVariable("DESKTOPHOST_TEST_MACOS_NOTARIZED_APP");
        var team = Environment.GetEnvironmentVariable("DESKTOPHOST_TEST_MACOS_NOTARIZED_TEAM");
        Assert.SkipWhen(string.IsNullOrEmpty(app) || string.IsNullOrEmpty(team),
            "set DESKTOPHOST_TEST_MACOS_NOTARIZED_APP and DESKTOPHOST_TEST_MACOS_NOTARIZED_TEAM to an installed notarized application");

        var trusted = MacCodeSignature.Verify(app!, [new AppleTeam(team!)], requireNotarization: true);
        var other = MacCodeSignature.Verify(app!, [SomeTeam], requireNotarization: true);

        Assert.Equal(SignatureVerdict.Trusted, trusted.Verdict);
        Assert.Equal(SignatureVerdict.UnexpectedPublisher, other.Verdict);
    }
}
