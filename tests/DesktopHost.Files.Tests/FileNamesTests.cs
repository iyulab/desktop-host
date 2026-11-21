using System.Text;

namespace DesktopHost.Files.Tests;

// Names are built from one Hangul word in both forms. «Stored» is what the directory lists — not always the form a file was created
// with: a volume may store a name in another form (macOS's SMB client lists a name created composed as decomposed), so each test
// reads the listing back instead of assuming it.
public sealed class FileNamesTests : IDisposable
{
    private static readonly string Composed = "계약서".Normalize(NormalizationForm.FormC);
    private static readonly string Decomposed = "계약서".Normalize(NormalizationForm.FormD);
    private readonly string _root;

    public FileNamesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "desktophost-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string StoredName(string path) => Path.GetFileName(path);

    /// <summary>The one entry the root lists, as the file system spells it.</summary>
    private string Listed() => Path.GetFileName(Assert.Single(Directory.GetFileSystemEntries(_root)));

    [Fact]
    public void The_two_forms_of_one_name_are_the_same_name_and_another_name_is_not()
    {
        Assert.NotEqual(Composed, Decomposed);
        Assert.True(FileNames.AreSame(Composed + ".txt", Decomposed + ".txt"));
        Assert.False(FileNames.AreSame(Composed + ".txt", Composed + ".md"));
        Assert.False(FileNames.AreSame("Report.txt", "report.txt"));
        Assert.True(FileNames.AreSame("Report.txt", "report.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_name_typed_composed_finds_the_file_stored_decomposed_spelled_as_stored()
    {
        File.WriteAllText(Path.Combine(_root, Decomposed + ".txt"), "x");

        var found = FileNames.FindEntry(_root, Composed + ".txt");

        Assert.NotNull(found);
        Assert.Equal(Listed(), StoredName(found));
    }

    [Fact]
    public void A_name_typed_decomposed_finds_the_file_stored_composed()
    {
        File.WriteAllText(Path.Combine(_root, Composed + ".txt"), "x");

        var found = FileNames.FindEntry(_root, Decomposed + ".txt");

        Assert.NotNull(found);
        Assert.Equal(Listed(), StoredName(found));
    }

    [Fact]
    public void A_name_that_matches_nothing_finds_nothing()
    {
        File.WriteAllText(Path.Combine(_root, Composed + ".txt"), "x");

        Assert.Null(FileNames.FindEntry(_root, "영수증.txt"));
        Assert.Null(FileNames.FindEntry(Path.Combine(_root, "missing"), Composed + ".txt"));
        Assert.Null(FileNames.ResolvePath(Path.Combine(_root, "영수증.txt")));
    }

    [Fact]
    public void Both_forms_held_side_by_side_are_each_found_by_their_own_spelling_and_neither_by_a_third()
    {
        File.WriteAllText(Path.Combine(_root, Composed + ".txt"), "composed");
        File.WriteAllText(Path.Combine(_root, Decomposed + ".txt"), "decomposed");
        // A volume that looks names up by either form (APFS) opens the first file for the second name: one entry, nothing to test.
        Assert.SkipWhen(Directory.GetFileSystemEntries(_root).Length < 2, "this volume holds one entry for both forms");

        Assert.Equal(Composed + ".txt", StoredName(FileNames.FindEntry(_root, Composed + ".txt")!));
        Assert.Equal(Decomposed + ".txt", StoredName(FileNames.FindEntry(_root, Decomposed + ".txt")!));
        // A spelling of the name that is neither stored one — here, composed with a different extension case — matches neither.
        Assert.Null(FileNames.FindEntry(_root, Composed + ".TXT"));
    }

    [Fact]
    public void A_path_typed_composed_resolves_every_segment_to_its_stored_spelling()
    {
        var folder = Path.Combine(_root, Decomposed);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, Decomposed + ".txt"), "x");

        var storedFolder = Listed();
        var storedFile = Path.GetFileName(Assert.Single(Directory.GetFileSystemEntries(Path.Combine(_root, storedFolder))));

        var resolved = FileNames.ResolvePath(Path.Combine(_root, Composed, Composed + ".txt"));

        Assert.Equal(Path.Combine(_root, storedFolder, storedFile), resolved);
        Assert.Equal("x", File.ReadAllText(resolved!));
    }

    [Fact]
    public void A_path_already_spelled_as_stored_resolves_to_itself()
    {
        File.WriteAllText(Path.Combine(_root, Composed + ".txt"), "x");
        var file = Path.Combine(_root, Listed());

        Assert.Equal(file, FileNames.ResolvePath(file));
        Assert.Equal(_root, FileNames.ResolvePath(_root));
    }
}
