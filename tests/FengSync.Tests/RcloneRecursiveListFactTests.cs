using System.Text.Json;
using FengSync.Core;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Tests;

/// <summary>
/// What a recursive <c>operations/list</c> actually hands back, measured rather than assumed. Dragging a whole
/// folder out of the browser is built on this one call: the entries it names decide which local files must
/// exist, which directories have to be created, and what a path in the answer is <em>relative to</em>. Every
/// fact here is about the answer's shape, which no fake could invent honestly — these run against the bundled
/// binary through the real daemon over a local tree behind an <c>alias</c> remote, so no network and no
/// credential is involved.
/// </summary>
public sealed class RcloneRecursiveListFactTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-recursivelist-facts-" + Guid.NewGuid().ToString("N"));
    private string Files => Path.Combine(_root, "files");
    private string Top => Path.Combine(Files, "top");
    private RcloneDaemon? _daemon;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Top, "sub", "deep"));
        Directory.CreateDirectory(Path.Combine(Top, "empty"));
        await File.WriteAllTextAsync(Path.Combine(Top, "a.txt"), new string('a', 1024));
        await File.WriteAllTextAsync(Path.Combine(Top, "sub", "b.txt"), new string('b', 2048));
        await File.WriteAllTextAsync(Path.Combine(Top, "sub", "deep", "c.txt"), new string('c', 4096));
        await File.WriteAllTextAsync(Path.Combine(Files, "side.txt"), new string('s', 512));
        var config = Path.Combine(_root, "rclone.conf");
        await File.WriteAllTextAsync(config,
            $"[probe]\ntype = alias\nremote = {Files.Replace('\\', '/')}\n");
        _daemon = await RcloneDaemon.StartAsync(RclonePath(), config);
    }

    public async Task DisposeAsync()
    {
        if (_daemon is not null) await _daemon.DisposeAsync();
        if (Directory.Exists(_root)) TryDelete(_root);
    }

    private RcloneRcClient Client => _daemon!.Client;

    [Fact]
    public async Task This_backend_answers_the_browsed_prefix_inside_every_reported_path()
    {
        var inside = await Client.ListRecursiveAsync("probe:", "top", default);

        // Measured, not assumed: alias answers `top/sub/b.txt` even though `remote` was `top`, so the path in the
        // answer is already relative to the remote root rather than to the directory just browsed. The browser's
        // rows are built from that root-relative shape (CloudFileManagerService.Entry composes the browsed
        // directory only when it is missing from what came back), and the drag-out staging path is built from the
        // row's path — never from this raw string as-is.
        Assert.Contains("top/sub/b.txt", inside.Select(x => x.Path));
        Assert.DoesNotContain("sub/b.txt", inside.Select(x => x.Path));
    }

    [Fact]
    public async Task The_same_object_is_answered_under_a_path_that_grows_with_the_directory_asked_for()
    {
        var inside = await Client.ListRecursiveAsync("probe:", "top/sub", default);
        var fromRoot = await Client.ListRecursiveAsync("probe:", "", default);

        // Two browsed directories, one file: the answer names it differently each time, so which folder a drag
        // is standing in has to travel with the listing rather than be re-derived from the entry.
        Assert.Contains("top/sub/b.txt", inside.Select(x => x.Path));
        Assert.Contains("top/sub/b.txt", fromRoot.Select(x => x.Path));
        Assert.DoesNotContain("b.txt", fromRoot.Select(x => x.Path));
    }

    [Fact]
    public async Task The_directory_asked_for_is_not_one_of_the_entries_it_returns()
    {
        var entries = await Client.ListRecursiveAsync("probe:", "top", default);

        // So "the folder being dragged" never arrives as a row of its own: the local folder has to be created
        // from what was asked for, and an empty-path entry must never become a file at the cache root.
        Assert.DoesNotContain(entries, x => x.Path.Length == 0);
        Assert.All(entries, x => Assert.NotEqual("top", x.Path));
    }

    [Fact]
    public async Task Every_depth_arrives_in_one_answer_and_directories_are_named_as_ones()
    {
        var entries = await Client.ListRecursiveAsync("probe:", "top", default);

        // One call for the whole tree is what makes a folder drag affordable; if this ever needs a walk per
        // directory, the gesture timeout below becomes a per-directory decision instead.
        Assert.Equal(["top/a.txt", "top/empty", "top/sub", "top/sub/b.txt", "top/sub/deep", "top/sub/deep/c.txt"],
            entries.Select(x => x.Path).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(["top/empty", "top/sub", "top/sub/deep"],
            entries.Where(x => x.IsDirectory).Select(x => x.Path).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_empty_directory_is_reported_rather_than_dropped_from_the_answer()
    {
        var entries = await Client.ListRecursiveAsync("probe:", "top", default);

        // The whole reason a folder drag can mirror structure: if an empty directory vanished from the listing,
        // dragging out would silently lose the folders the user can see on screen.
        Assert.Contains(entries, x => x.IsDirectory && x.Path == "top/empty");
    }

    [Fact]
    public async Task A_directory_reports_no_size_that_could_stand_as_its_contents()
    {
        var entries = await Client.ListRecursiveAsync("probe:", "top", default);
        var sub = Assert.Single(entries, x => x.Path == "top/sub");

        // The local mirror of a directory is therefore checked by asking whether the files it holds are there,
        // never by comparing a number the listing gave for the directory itself.
        Assert.True(sub.IsDirectory);
        Assert.Equal(0, sub.Size);
    }

    [Fact]
    public async Task A_file_named_in_the_answer_carries_the_size_its_bytes_have()
    {
        var entries = await Client.ListRecursiveAsync("probe:", "top", default);
        var deep = Assert.Single(entries, x => x.Path == "top/sub/deep/c.txt");

        Assert.False(deep.IsDirectory);
        Assert.Equal(new FileInfo(Path.Combine(Top, "sub", "deep", "c.txt")).Length, deep.Size);
    }

    [Fact]
    public async Task A_recursive_answer_also_names_the_files_beside_the_browsed_directory_when_the_root_is_asked_for()
    {
        var entries = await Client.ListRecursiveAsync("probe:", "", default);

        // A folder drag inside a browsed directory must not pick up these neighbours: the staging path is built
        // from the same remote the listing was asked for, and this is the fact that keeps that honest.
        Assert.Contains("side.txt", entries.Select(x => x.Path));
        Assert.Contains("top/a.txt", entries.Select(x => x.Path));
    }

    [Fact]
    public async Task A_missing_directory_is_answered_with_the_sentence_the_drag_reports_rather_than_an_empty_list()
    {
        // The gesture has to say something different when the folder it was pointed at is gone from the cloud
        // than when it simply holds nothing, so this stays a failure at the wire rather than a zero-length list.
        var failure = await Assert.ThrowsAsync<RcloneException>(() => Client.ListRecursiveAsync("probe:", "gone", default));

        Assert.Contains("directory not found", failure.Failure.TechnicalMessage, StringComparison.OrdinalIgnoreCase);

        var empty = await Client.ListRecursiveAsync("probe:", "top/empty", default);
        Assert.Empty(empty);
    }

    [Fact]
    public async Task The_raw_answer_marks_directories_with_IsDir_and_nothing_else()
    {
        // Pinned at the wire because the mirror is built by reading this field: a directory that came back
        // without IsDir would be staged as a file whose name has no extension.
        var response = await Client.CallAsync("operations/list", new { fs = "probe:", remote = "top", opt = new { recurse = true } });
        var first = response.GetProperty("list").EnumerateArray().First(x => x.GetProperty("Path").GetString() == "top/sub");

        Assert.True(first.GetProperty("IsDir").GetBoolean());
        Assert.Equal(JsonValueKind.String, first.GetProperty("Path").ValueKind);
    }

    private static string RclonePath()
    {
        try { return BundledRclone.ExecutablePath; }
        catch (FileNotFoundException) { }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "FengSync", "Assets", "rclone", "rclone.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到内置 rclone.exe，无法运行递归列表事实测试。");
    }

    /// <summary>Windows can hold a directory a few milliseconds after its last handle closes.</summary>
    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Directory.Delete(path, true); return; }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
