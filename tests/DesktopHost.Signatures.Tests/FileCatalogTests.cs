using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopHost.Signatures.Tests;

/// <summary>
/// A directory against a Windows catalog made by PowerShell's <c>New-FileCatalog</c> (SHA-256) — the tool a release build uses.
/// The catalog's own signature is the Authenticode check <see cref="AuthenticodeTests"/> covers; here it is supplied, so no
/// test has to trust a certificate on the machine it runs on.
/// </summary>
public sealed class FileCatalogTests : IDisposable
{
    private readonly string _root;
    private readonly string _dir;
    private readonly string _catalog;

    private static readonly SignatureCheck Signed = new(SignatureVerdict.Trusted, "CN=Test, O=Example Software Ltd", 0);

    public FileCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "desktophost-catalog-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(_root, "component");
        _catalog = Path.Combine(_root, "component.cat");
        Directory.CreateDirectory(Path.Combine(_dir, "scripts"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a scanner holding a copy a moment longer; the temp folder is the OS's to clean */ }
    }

    // A program (hashed the Authenticode way, its signature left out) and a script (hashed whole) — the two kinds a component
    // directory carries.
    private void Seed()
    {
        File.Copy(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll"), Path.Combine(_dir, "engine.dll"));
        File.WriteAllText(Path.Combine(_dir, "scripts", "sidecar.js"), "console.log('ready');\n");
    }

    private void MakeCatalog(string? directory = null, string? catalog = null)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                $"New-FileCatalog -Path '{directory ?? _dir}' -CatalogFilePath '{catalog ?? _catalog}' -CatalogVersion 2.0 | Out-Null" },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // Started from PowerShell 7 (a developer's terminal, a runner step), the child inherits a module path that points
        // Windows PowerShell at 7's modules, and New-FileCatalog's module then fails to load. Its own default path is right.
        start.Environment.Remove("PSModulePath");
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && File.Exists(catalog ?? _catalog), $"New-FileCatalog failed: {error}");
    }

    private CatalogCheck Check(SignatureCheck? signature = null) =>
        FileCatalog.Verify(_dir, _catalog, _ => signature ?? Signed);

    [Fact]
    public void A_directory_exactly_as_its_signed_catalog_lists_it_is_trusted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows catalog");
        Seed();
        MakeCatalog();

        var check = Check();
        Assert.Equal(CatalogVerdict.Trusted, check.Verdict);
        Assert.True(check.IsTrusted);
        Assert.Equal(Signed, check.Signature);
    }

    [Fact]
    public void A_script_changed_after_the_catalog_was_signed_is_named()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows catalog");
        Seed();
        MakeCatalog();
        File.AppendAllText(Path.Combine(_dir, "scripts", "sidecar.js"), "fetch('https://example.invalid');\n");

        var check = Check();
        Assert.Equal(CatalogVerdict.FileChanged, check.Verdict);
        Assert.Equal(@"scripts\sidecar.js", check.File);
    }

    [Fact]
    public void A_library_changed_after_the_catalog_was_signed_is_named()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows catalog");
        Seed();
        MakeCatalog();
        var path = Path.Combine(_dir, "engine.dll");
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 3] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Equal(new CatalogCheck(CatalogVerdict.FileChanged, Signed, "engine.dll"), Check());
    }

    [Fact]
    public void A_file_added_beside_the_listed_ones_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows catalog");
        Seed();
        MakeCatalog();
        File.WriteAllText(Path.Combine(_dir, "scripts", "extra.js"), "/* not shipped */\n");

        var check = Check();
        Assert.Equal(CatalogVerdict.FileNotListed, check.Verdict);
        Assert.Equal(@"scripts\extra.js", check.File);
    }

    [Fact]
    public void A_listed_file_that_is_gone_is_named()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows catalog");
        Seed();
        MakeCatalog();
        File.Delete(Path.Combine(_dir, "scripts", "sidecar.js"));

        var check = Check();
        Assert.Equal(CatalogVerdict.FileMissing, check.Verdict);
        Assert.Equal(@"scripts\sidecar.js", check.File);
    }

    [Fact]
    public void A_catalog_that_is_not_signed_by_an_expected_publisher_stops_the_check_before_any_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows catalog");
        Seed();
        MakeCatalog();
        File.Delete(Path.Combine(_dir, "engine.dll")); // would read as missing — the signature is checked first

        var unsigned = new SignatureCheck(SignatureVerdict.NotSigned, null, unchecked((int)0x800B0100));
        var check = Check(unsigned);
        Assert.Equal(CatalogVerdict.CatalogNotTrusted, check.Verdict);
        Assert.Equal(unsigned, check.Signature);
        Assert.Null(check.File);
    }

    [Fact]
    public void Through_the_public_check_an_unsigned_catalog_is_not_trusted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows catalog");
        Seed();
        MakeCatalog();

        var check = FileCatalog.Verify(_dir, _catalog, [new Publisher("Example Software Ltd")]);
        Assert.Equal(CatalogVerdict.CatalogNotTrusted, check.Verdict);
        Assert.Equal(SignatureVerdict.NotSigned, check.Signature!.Verdict);
    }

    [Fact]
    public void The_catalog_inside_the_directory_it_lists_is_not_one_of_its_own_members()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows catalog");
        Seed();
        var inside = Path.Combine(_dir, "component.cat");
        MakeCatalog(catalog: Path.Combine(_root, "made-outside.cat"));
        File.Move(Path.Combine(_root, "made-outside.cat"), inside);

        Assert.Equal(CatalogVerdict.Trusted, FileCatalog.Verify(_dir, inside, _ => Signed).Verdict);
    }

    [Fact]
    public void A_file_that_is_not_a_catalog_is_unreadable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows catalog");
        Seed();
        File.WriteAllText(_catalog, "not a catalog");

        Assert.Equal(CatalogVerdict.CatalogUnreadable, Check().Verdict);
    }

    // The signature half on a real catalog: Windows' own catalogs are signed by its publisher, and the check reads a catalog's
    // signature the way it reads a program's (a catalog is the same signed container a program's signature is).
    [Fact]
    public void A_catalogs_own_signature_is_read_by_the_same_check_as_a_programs()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a Windows catalog");
        var catRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "CatRoot", "{F750E6C3-38EE-11D1-85E5-00C04FC295EE}");
        var systemCatalog = Directory.EnumerateFiles(catRoot, "*.cat").FirstOrDefault();
        Assert.SkipWhen(systemCatalog is null, "no system catalog on this machine");

        var check = Authenticode.Verify(systemCatalog!, [new Publisher("Microsoft Corporation") { Country = "US" }]);
        Assert.Equal(SignatureVerdict.Trusted, check.Verdict);
        Assert.Equal(SignatureVerdict.UnexpectedPublisher, Authenticode.Verify(systemCatalog!, [new Publisher("Example Software Ltd")]).Verdict);
    }

    [Fact]
    public void A_check_that_names_no_publisher_is_refused()
    {
        // Refused before anything is read, on every platform — so this test seeds nothing (the seed copies a Windows file).
        Assert.Throws<ArgumentException>(() => FileCatalog.Verify(_dir, _catalog, []));
    }
}
