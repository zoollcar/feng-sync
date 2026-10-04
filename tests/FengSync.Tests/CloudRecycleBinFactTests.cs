using FengSync.Core;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Tests;

/// <summary>
/// What the bundled rclone v1.75.0 really does around the operations the recycle bin is built from, measured
/// against the binary itself rather than assumed. These are the facts that forced the design: there is no
/// <c>operations/trash</c> to call, so a soft delete has to be a move into a folder the application owns — and a
/// move is the dangerous half of that, because both move legs overwrite a name that is already taken, create the
/// parent directories they need, and leave a merged directory's source sitting there empty. Every case is a local
/// backend behind an <c>alias</c> remote, so no network and no credential is involved.
/// </summary>
public sealed class CloudRecycleBinFactTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-trash-facts-" + Guid.NewGuid().ToString("N"));

    /// <summary>The directory the alias remote stands for: what is on disk is what the endpoint holds.</summary>
    private string EndpointDirectory => Path.Combine(_root, "endpoint");

    /// <summary>The rclone fs string every call in this file is addressed with.</summary>
    private const string Fs = "endpoint:";

    private RcloneDaemon? _daemon;

    private RcloneRcClient Client => _daemon!.Client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(EndpointDirectory);
        var config = Path.Combine(_root, "rclone.conf");
        await File.WriteAllTextAsync(config,
            $"[endpoint]\ntype = alias\nremote = {EndpointDirectory.Replace('\\', '/')}\n");
        _daemon = await RcloneDaemon.StartAsync(RclonePath(), config);
    }

    public async Task DisposeAsync()
    {
        if (_daemon is not null) await _daemon.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task There_is_no_backend_trash_to_move_into_so_the_recycle_bin_has_to_be_a_folder()
    {
        // The whole design follows from this: the RC API has no trash verb, so a soft delete that wants a 还原
        // button behind it has to be an ordinary move into a directory the application lists and owns.
        var error = await Assert.ThrowsAsync<RcloneException>(() =>
            Client.CallAsync("operations/trash", new { fs = Fs, remote = "a.txt" }));

        Assert.Equal(RcloneFailureCategory.NotFound, error.Failure.Category);
        Assert.Contains("couldn't find method", error.Failure.TechnicalMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_directory_is_an_error_and_an_empty_one_is_a_listing_so_the_two_can_be_told_apart()
    {
        // The browser reads a directory that has never been created as empty, and that reading only holds
        // because "not there" and "nothing inside" are different answers rather than the same one twice.
        var missing = await Assert.ThrowsAsync<RcloneException>(() => Client.ListDirectoryAsync(Fs, "never-created"));
        Assert.Equal(RcloneFailureCategory.NotFound, missing.Failure.Category);
        Assert.Contains("directory not found", missing.Failure.TechnicalMessage, StringComparison.OrdinalIgnoreCase);

        await Client.MakeDirectoryAsync(Fs, "empty");

        Assert.Empty(await Client.ListDirectoryAsync(Fs, "empty"));
    }

    [Fact]
    public async Task A_file_move_creates_the_directories_it_needs_so_a_restore_never_needs_the_folder_first()
    {
        // An object can go back to a directory the user deleted in the meantime, and the trash folder itself
        // never needs a mkdir: both are this same behaviour.
        await Write("top.txt", "content");

        await Client.MoveFileAsync(Fs, "top.txt", "reports/2026/top.txt");

        Assert.False(File.Exists(Local("top.txt")));
        Assert.Equal("content", await File.ReadAllTextAsync(Local("reports", "2026", "top.txt")));
    }

    [Fact]
    public async Task A_file_move_replaces_the_object_that_already_holds_the_destination_name()
    {
        // The hazard that makes a landing name have to be chosen free before anything moves: nothing refuses this
        // call and no error comes back, so a collision discovered afterwards is already a lost file.
        await Write("incoming.txt", "the new copy");
        await Write("taken.txt", "the copy that is already there");

        await Client.MoveFileAsync(Fs, "incoming.txt", "taken.txt");

        Assert.Equal("the new copy", await File.ReadAllTextAsync(Local("taken.txt")));
        Assert.False(File.Exists(Local("incoming.txt")));
    }

    [Fact]
    public async Task A_landing_name_that_belongs_to_a_directory_is_refused_outright()
    {
        // Why the free names are chosen from a full listing rather than from the files only: a name a directory
        // holds is not a silent overwrite but a refusal, and a batch that treated it as one would report a move
        // that never happened.
        await Client.MakeDirectoryAsync(Fs, "keep");
        await Write("a.txt", "content");

        var error = await Assert.ThrowsAsync<RcloneException>(() => Client.MoveFileAsync(Fs, "a.txt", "keep"));

        Assert.Contains("directory", error.Failure.TechnicalMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("content", await File.ReadAllTextAsync(Local("a.txt")));
    }

    [Fact]
    public async Task A_directory_move_onto_a_path_that_exists_merges_keeps_what_was_only_there_and_leaves_an_empty_husk()
    {
        // A folder recycled into a trash that already holds a folder of the same name is this case. The contents
        // survive from both sides and the incoming one wins, but the source stays behind as an empty directory —
        // --delete-empty-src-dirs clears the source's subdirectories and never its root — so a browser that did
        // not remove it would show the user a folder that appears to have survived.
        await Write("reports/a.pdf", "the incoming copy");
        await Write("reports/only-in-source.pdf", "moved along");
        await Write("other/a.pdf", "the copy that was already in the trash");
        await Write("other/only-in-destination.pdf", "stays");

        await Client.MoveDirectoryAsync(Fs + "reports", Fs + "other");

        Assert.True(Directory.Exists(Local("other")));
        Assert.Equal("the incoming copy", await File.ReadAllTextAsync(Local("other", "a.pdf")));
        Assert.Equal("stays", await File.ReadAllTextAsync(Local("other", "only-in-destination.pdf")));
        Assert.Equal("moved along", await File.ReadAllTextAsync(Local("other", "only-in-source.pdf")));
        Assert.Empty(Directory.GetFileSystemEntries(Local("reports")));
    }

    [Fact]
    public async Task Removing_the_husk_works_once_and_answering_it_twice_is_the_reason_existence_is_probed_first()
    {
        // rmdir clears the empty directory the merge left. Asking again is the trap the cleanup has to avoid:
        // v1.75.0 reports a path that is not there with the operating system's own wording in an HTTP 500, which
        // the shared classifier reads as a temporary failure — so a blind second removal looks like a network
        // problem rather than like nothing having to be done.
        await Write("reports/a.pdf", "content");
        await Write("other/a.pdf", "already there");
        await Client.MoveDirectoryAsync(Fs + "reports", Fs + "other");

        await Client.RemoveDirectoryAsync(Fs, "reports");

        Assert.False(Directory.Exists(Local("reports")));
        var again = await Assert.ThrowsAsync<RcloneException>(() => Client.RemoveDirectoryAsync(Fs, "reports"));
        Assert.Equal(RcloneFailureCategory.Temporary, again.Failure.Category);
        // operations/stat is the answer that lets the caller know there is nothing to remove: an empty item,
        // not an exception.
        Assert.Null(await Client.StatAsync(Fs, "reports", null));
    }

    [Fact]
    public async Task A_directory_move_onto_a_fresh_path_takes_the_whole_tree_and_leaves_no_source_behind()
    {
        // The ordinary case, and the reason the husk cleanup is conditional: a first recycle of a folder moves it
        // whole and there is nothing left to remove.
        await Write("reports/2026/a.pdf", "content");
        await Write("reports/top.txt", "another");

        await Client.MoveDirectoryAsync(Fs + "reports", Fs + ".fengsync-trash/reports");

        Assert.False(Directory.Exists(Local("reports")));
        Assert.Equal("content", await File.ReadAllTextAsync(Local(".fengsync-trash", "reports", "2026", "a.pdf")));
        Assert.Equal("another", await File.ReadAllTextAsync(Local(".fengsync-trash", "reports", "top.txt")));
    }

    [Fact]
    public async Task Making_the_trash_folder_is_idempotent_and_refuses_to_replace_a_file_wearing_its_name()
    {
        // The 「回收站」 button creates the folder on the way in, so a second click has to be a no-op — and the
        // one case where it must not silently succeed is an ordinary file that happens to carry the folder's
        // name, which it loudly refuses to overwrite.
        await Client.MakeDirectoryAsync(Fs, ".fengsync-trash");
        await Client.MakeDirectoryAsync(Fs, ".fengsync-trash");

        Assert.True(Directory.Exists(Local(".fengsync-trash")));
        await Write("plain.txt", "content");

        await Assert.ThrowsAsync<RcloneException>(() => Client.MakeDirectoryAsync(Fs, "plain.txt"));

        // The refusal is the point: a button that creates a folder must never be able to replace the file that
        // happens to carry that name.
        Assert.Equal("content", await File.ReadAllTextAsync(Local("plain.txt")));
    }

    [Fact]
    public async Task A_file_moves_into_the_mirror_and_back_to_the_path_it_came_from()
    {
        // The mirror is the restore's whole mechanism, and it costs nothing: the same two legs compose the round
        // trip. What the file move leaves behind is the empty mirror directory, because a file move never touches
        // directories — the browser's restore of a whole folder is the directory leg, and its husk cleanup is
        // what pins that side.
        await Write("deep/nested/a.txt", "content");

        await Client.MoveFileAsync(Fs, "deep/nested/a.txt", ".fengsync-trash/deep/nested/a.txt");
        await Client.MoveFileAsync(Fs, ".fengsync-trash/deep/nested/a.txt", "deep/nested/a.txt");

        Assert.Equal("content", await File.ReadAllTextAsync(Local("deep", "nested", "a.txt")));
        Assert.False(File.Exists(Local(".fengsync-trash", "deep", "nested", "a.txt")));
    }

    private async Task Write(string relativePath, string content)
    {
        var target = Local(relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, content);
    }

    private string Local(params string[] parts) => Path.Combine(EndpointDirectory, Path.Combine(parts));

    private static string RclonePath()
    {
        try { return BundledRclone.ExecutablePath; }
        catch (FileNotFoundException) { }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "FengSync", "Assets", "rclone", "rclone.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到内置 rclone.exe，无法运行回收站实证测试。");
    }
}
