using System.Runtime.Versioning;
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

    // --- the macOS Trash (the system's own move, so it runs on a Mac only) ---

    [Fact]
    [SupportedOSPlatform("macos")]
    public void On_a_Mac_a_file_leaves_its_folder_and_the_system_names_where_it_went()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the macOS Trash");
        var item = Path.Combine(_root, "work", "a report " + Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(item)!);
        File.WriteAllText(item, "content");

        var landed = MacTrash.Move(item);
        try
        {
            Assert.False(File.Exists(item));
            Assert.NotEqual(item, landed);
            Assert.Contains(".Trash", landed);
            Assert.Equal("content", File.ReadAllText(landed));
        }
        finally
        {
            try { File.Delete(landed); }
            catch (IOException) { /* the Trash is the person's; one file of the test left in it is no harm */ }
        }
    }

    [Fact]
    public void On_a_Mac_a_folder_moves_whole_through_the_public_call_and_a_missing_item_is_still_not_found()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the macOS Trash");
        var folder = Path.Combine(_root, "photos " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "p.jpg"), "x");

        try
        {
            Trash.Move(folder);

            Assert.False(Directory.Exists(folder));
            Assert.Throws<FileNotFoundException>(() => Trash.Move(folder));
        }
        finally
        {
            // The name carries a fresh GUID, so the folder in the Trash is this test's alone.
            var landed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash", Path.GetFileName(folder));
            try { if (Directory.Exists(landed)) Directory.Delete(landed, recursive: true); }
            catch (IOException) { /* the Trash is the person's; one folder of the test left in it is no harm */ }
        }
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

    // --- putting an item back (the undo of a move to the trash) ---

    [Fact]
    public void A_file_put_back_is_where_it_was_with_its_content_and_its_record_is_gone()
    {
        var trash = Path.Combine(_root, "Trash");
        var item = Path.Combine(_root, "work", "a report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(item)!);
        File.WriteAllText(item, "content");

        var trashed = FreedesktopTrash.Move(item, trash);
        Assert.Equal(item, trashed.OriginalPath);
        Assert.Equal(Path.Combine(trash, "files", "a report.txt"), trashed.TrashPath);
        Assert.Equal(Path.Combine(trash, "info", "a report.txt.trashinfo"), trashed.RecordPath);

        Trash.PutBack(trashed, t => FreedesktopTrash.Holds(trash, t));

        Assert.Equal("content", File.ReadAllText(item));
        Assert.False(File.Exists(trashed.TrashPath));
        Assert.False(File.Exists(trashed.RecordPath));
    }

    [Fact]
    public void A_folder_is_put_back_whole_and_a_folder_that_held_it_and_went_is_made_again()
    {
        var trash = Path.Combine(_root, "Trash");
        var folder = Path.Combine(_root, "projects", "photos");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "p.jpg"), "x");

        var trashed = FreedesktopTrash.Move(folder, trash);
        Directory.Delete(Path.Combine(_root, "projects"));

        Trash.PutBack(trashed, t => FreedesktopTrash.Holds(trash, t));

        Assert.Equal("x", File.ReadAllText(Path.Combine(folder, "p.jpg")));
    }

    [Fact]
    public void Something_at_the_original_place_keeps_the_item_in_the_trash_and_says_so()
    {
        var trash = Path.Combine(_root, "Trash");
        var item = Path.Combine(_root, "notes.md");
        File.WriteAllText(item, "old");
        var trashed = FreedesktopTrash.Move(item, trash);
        File.WriteAllText(item, "new");

        var refused = Assert.Throws<TrashException>(() => Trash.PutBack(trashed, t => FreedesktopTrash.Holds(trash, t)));

        Assert.Equal(TrashException.Occupied, refused.Reason);
        Assert.Equal("new", File.ReadAllText(item));
        Assert.Equal("old", File.ReadAllText(trashed.TrashPath));
    }

    [Fact]
    public void An_item_no_longer_in_the_trash_is_not_found_and_a_handle_outside_a_trash_is_refused()
    {
        var trash = Path.Combine(_root, "Trash");
        var item = Path.Combine(_root, "gone.txt");
        File.WriteAllText(item, "x");
        var trashed = FreedesktopTrash.Move(item, trash);
        File.Delete(trashed.TrashPath);   // the trash was emptied

        Assert.Throws<FileNotFoundException>(() => Trash.PutBack(trashed, t => FreedesktopTrash.Holds(trash, t)));

        // A handle naming an ordinary file is not a trash item: a put-back would move any file to any path.
        var ordinary = Path.Combine(_root, "ordinary.txt");
        File.WriteAllText(ordinary, "x");
        Assert.Throws<ArgumentException>(() => Trash.PutBack(new TrashedItem(Path.Combine(_root, "elsewhere.txt"), ordinary, null)));
        Assert.True(File.Exists(ordinary));
    }

    [Fact]
    public void On_a_Mac_a_file_moved_to_the_Trash_is_put_back_by_the_public_calls()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the macOS Trash");
        var item = Path.Combine(_root, "work", "undo me " + Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(item)!);
        File.WriteAllText(item, "content");

        var trashed = Trash.Move(item);
        Assert.False(File.Exists(item));
        Assert.Null(trashed.RecordPath);

        Trash.PutBack(trashed);

        Assert.Equal("content", File.ReadAllText(item));
        Assert.False(File.Exists(trashed.TrashPath));
    }

    [Fact]
    public void The_Recycle_Bin_record_is_the_I_file_beside_the_R_file()
    {
        Assert.Equal(Path.Combine("bin", "$IAB12CD.txt"), RecycleBinRecord.Of(Path.Combine("bin", "$RAB12CD.txt")));
        Assert.Null(RecycleBinRecord.Of(Path.Combine("bin", "plain.txt")));
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

        var trashedFile = Trash.Move(file);
        var trashedFolder = Trash.Move(folder);

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(folder));
        Assert.Contains(@"\$Recycle.Bin\", trashedFile.TrashPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(trashedFile.RecordPath), "the $I record sits beside the $R item");

        Trash.PutBack(trashedFile);
        Trash.PutBack(trashedFolder);
        Assert.Equal("x", File.ReadAllText(file));
        Assert.Equal("y", File.ReadAllText(Path.Combine(folder, "inside.txt")));
        Assert.False(File.Exists(trashedFile.RecordPath));
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
