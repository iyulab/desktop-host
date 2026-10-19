using System.Text;

namespace DesktopHost.Files.Tests;

public sealed class TrashTests : IDisposable
{
    private readonly string _root;

    public TrashTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "desktophost-trash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a scanner holding a file a moment longer; the temp folder is the OS's to clean */ }
    }

    [Fact]
    public void A_relative_path_is_refused_and_a_missing_one_is_not_found()
    {
        Assert.Throws<ArgumentException>(() => Trash.Move("relative.txt"));
        Assert.Throws<FileNotFoundException>(() => Trash.Move(Path.Combine(_root, "never-made.txt")));
    }

    // --- the freedesktop.org home trash (the logic is file moves, so it runs on every platform) ---

    [Fact]
    public void A_file_moves_into_files_with_a_record_of_where_it_was()
    {
        var item = Path.Combine(_root, "work", "a report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(item)!);
        File.WriteAllText(item, "content");
        var trash = Path.Combine(_root, "Trash");

        FreedesktopTrash.Move(item, trash);

        Assert.False(File.Exists(item));
        Assert.Equal("content", File.ReadAllText(Path.Combine(trash, "files", "a report.txt")));
        var record = File.ReadAllText(Path.Combine(trash, "info", "a report.txt.trashinfo"));
        Assert.StartsWith("[Trash Info]\nPath=", record);
        Assert.Contains("a%20report.txt", record);
        Assert.Contains("\nDeletionDate=", record);
    }

    [Fact]
    public void A_name_already_in_the_trash_gets_a_number_and_folders_move_whole()
    {
        var trash = Path.Combine(_root, "Trash");
        var first = Path.Combine(_root, "one", "notes.md");
        var second = Path.Combine(_root, "two", "notes.md");
        var folder = Path.Combine(_root, "three", "photos");
        foreach (var f in new[] { first, second }) { Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, f); }
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "p.jpg"), "x");

        FreedesktopTrash.Move(first, trash);
        FreedesktopTrash.Move(second, trash);
        FreedesktopTrash.Move(folder, trash);

        Assert.True(File.Exists(Path.Combine(trash, "files", "notes.md")));
        Assert.Equal(second, File.ReadAllText(Path.Combine(trash, "files", "notes.2.md")));
        Assert.True(File.Exists(Path.Combine(trash, "info", "notes.2.md.trashinfo")));
        Assert.True(File.Exists(Path.Combine(trash, "files", "photos", "p.jpg")));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void The_record_encodes_the_path_and_keeps_its_separators()
    {
        var record = Encoding.UTF8.GetString(FreedesktopTrash.Record("/home/me/한 글/file#1.txt", new DateTime(2026, 10, 19, 13, 5, 9)));

        Assert.Equal(
            "[Trash Info]\nPath=/home/me/%ED%95%9C%20%EA%B8%80/file%231.txt\nDeletionDate=2026-10-19T13:05:09\n",
            record);
    }

    // --- the Recycle Bin (Windows) ---

    // Opt-in: every run leaves its two items in this account's real Recycle Bin, and emptying it is the person's to do. Run it
    // with DESKTOPHOST_TRASH_LIVE=1 after a change to the Windows path; the sink's rule is covered on every run below.
    [Fact]
    public void On_Windows_a_file_and_a_folder_go_to_the_Recycle_Bin()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("DESKTOPHOST_TRASH_LIVE") != "1") return;
        var file = Path.Combine(_root, "recycle-me.txt");
        File.WriteAllText(file, "x");
        var folder = Path.Combine(_root, "recycle-folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "y");

        Trash.Move(file);
        Trash.Move(folder);

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void The_sink_lets_a_recycling_delete_through_and_aborts_any_other()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sink = new RecycleOnlySink();

        Assert.Equal(0, sink.PreDeleteItem(WindowsRecycleBin.TsfDeleteRecycleIfPossible, 0));
        Assert.False(sink.RefusedPermanentDelete);

        Assert.Equal(unchecked((int)0x80004004), sink.PreDeleteItem(0, 0));
        Assert.True(sink.RefusedPermanentDelete, "a delete the shell would not recycle is refused, never let through");
    }
}
