using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace DesktopHost.Signatures.Tests;

public sealed class AuthenticodeTests : IDisposable
{
    private readonly string _root;

    public AuthenticodeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "desktophost-signatures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a scanner holding a copy a moment longer; the temp folder is the OS's to clean */ }
    }

    // The runtime these tests run on: on Windows its native files carry the publisher's embedded signature, so every
    // machine that can run the tests has a validly signed file from a known publisher at hand.
    private static string SignedFile => Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");

    private static readonly Publisher RuntimePublisher = new("Microsoft Corporation") { Country = "US" };

    [Fact]
    public void A_file_signed_by_an_expected_publisher_is_trusted_and_names_its_signer()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is a Windows signature");
        var check = Authenticode.Verify(SignedFile, [RuntimePublisher]);
        Assert.Equal(SignatureVerdict.Trusted, check.Verdict);
        Assert.True(check.IsTrusted);
        Assert.Contains("O=Microsoft Corporation", check.Signer);
        Assert.Equal(0, check.Status);
    }

    [Fact]
    public void A_valid_signature_from_anyone_else_is_refused_as_an_unexpected_publisher()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is a Windows signature");
        var other = Authenticode.Verify(SignedFile, [new Publisher("Example Software Ltd")]);
        Assert.Equal(SignatureVerdict.UnexpectedPublisher, other.Verdict);
        Assert.False(other.IsTrusted);
        Assert.Contains("Microsoft Corporation", other.Signer);

        // Every pinned field must hold: the right organization in another country is someone else.
        var elsewhere = Authenticode.Verify(SignedFile, [RuntimePublisher with { Country = "DE" }]);
        Assert.Equal(SignatureVerdict.UnexpectedPublisher, elsewhere.Verdict);
    }

    [Fact]
    public void Any_one_of_several_expected_publishers_is_enough()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is a Windows signature");
        var check = Authenticode.Verify(SignedFile, [new Publisher("Example Software Ltd"), RuntimePublisher]);
        Assert.Equal(SignatureVerdict.Trusted, check.Verdict);
    }

    [Fact]
    public void A_signed_file_changed_after_signing_is_invalid()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is a Windows signature");
        var copy = Path.Combine(_root, "changed.dll");
        var bytes = File.ReadAllBytes(SignedFile);
        bytes[bytes.Length / 3] ^= 0xFF; // inside the signed content, far from the signature at the end
        File.WriteAllBytes(copy, bytes);

        var check = Authenticode.Verify(copy, [RuntimePublisher]);
        Assert.Equal(SignatureVerdict.Invalid, check.Verdict);
        Assert.Null(check.Signer);
        Assert.NotEqual(0, check.Status);
    }

    [Fact]
    public void An_unchanged_copy_of_a_signed_file_is_still_trusted()
    {
        // The control for the test above: the copy and the read are not what makes a changed file invalid.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is a Windows signature");
        var copy = Path.Combine(_root, "copy.dll");
        File.Copy(SignedFile, copy);
        Assert.Equal(SignatureVerdict.Trusted, Authenticode.Verify(copy, [RuntimePublisher]).Verdict);
    }

    [Fact]
    public void A_program_without_a_signature_and_a_file_of_no_signable_kind_are_not_signed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode is a Windows signature");
        // This test assembly: a program file built here, never signed.
        var unsigned = Authenticode.Verify(typeof(AuthenticodeTests).Assembly.Location, [RuntimePublisher]);
        Assert.Equal(SignatureVerdict.NotSigned, unsigned.Verdict);

        var text = Path.Combine(_root, "not-a-program.exe");
        File.WriteAllText(text, "just text with a program's name");
        Assert.Equal(SignatureVerdict.NotSigned, Authenticode.Verify(text, [RuntimePublisher]).Verdict);
    }

    [Fact]
    public void Nothing_is_checked_off_windows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the check runs on Windows");
        var file = Path.Combine(_root, "any.bin");
        File.WriteAllText(file, "x");
        var check = Authenticode.Verify(file, [RuntimePublisher]);
        Assert.Equal(SignatureVerdict.Unsupported, check.Verdict);
        Assert.False(check.IsTrusted);
    }

    [Fact]
    public void A_check_needs_an_expected_publisher_and_a_file()
    {
        var file = Path.Combine(_root, "any.bin");
        File.WriteAllText(file, "x");
        Assert.Throws<ArgumentException>(() => Authenticode.Verify(file, []));
        Assert.Throws<FileNotFoundException>(() => Authenticode.Verify(Path.Combine(_root, "never-made.exe"), [RuntimePublisher]));
    }

    // --- the subject match (no signature needed) ---

    private static X500DistinguishedName ExtendedValidation => new(
        "SERIALNUMBER=110-0000000, OID.2.5.4.15=Private Organization, C=KR, O=Example Software Ltd, CN=Example Software Ltd");

    [Fact]
    public void A_publisher_matches_on_the_fields_it_names_and_ignores_the_rest()
    {
        Assert.True(new Publisher("Example Software Ltd").Matches(ExtendedValidation));
        Assert.True(new Publisher("Example Software Ltd") { Country = "KR", SerialNumber = "110-0000000" }.Matches(ExtendedValidation));
        Assert.True(new Publisher("Example Software Ltd") { CommonName = "Example Software Ltd" }.Matches(ExtendedValidation));
    }

    [Fact]
    public void A_publisher_does_not_match_a_differing_or_missing_field_or_a_near_miss()
    {
        Assert.False(new Publisher("Example Software Ltd") { SerialNumber = "110-0000001" }.Matches(ExtendedValidation));
        Assert.False(new Publisher("Example Software Ltd") { Country = "US" }.Matches(ExtendedValidation));
        Assert.False(new Publisher("example software ltd").Matches(ExtendedValidation)); // exact, as issued
        Assert.False(new Publisher("Example Software").Matches(ExtendedValidation));     // not a prefix
        Assert.False(new Publisher("Example Software Ltd") { SerialNumber = "1" }.Matches(new X500DistinguishedName("O=Example Software Ltd")));
    }

    [Fact]
    public void A_subject_that_repeats_an_attribute_names_none_of_its_values()
    {
        var repeated = new X500DistinguishedName("O=Someone Else, O=Example Software Ltd, CN=x");
        Assert.False(new Publisher("Example Software Ltd").Matches(repeated));
    }
}
