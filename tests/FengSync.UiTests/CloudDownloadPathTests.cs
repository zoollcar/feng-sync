using System.IO;
using FengSync.Core;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Which endpoint a download reads from. A download stages under the file's own name, so the endpoint it
/// reads from is rooted at the file's own directory — the rule this file exists to pin, because getting it
/// wrong is invisible: the bare name still resolves, just to a same-named object one directory up.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudDownloadSourceUriTests
{
    private static CloudTransferTask Download(string uri, string remotePath) =>
        new(CloudTransferDirection.Download, uri, remotePath, @"C:\tmp\x", 10);

    [Fact]
    public void A_nested_download_is_read_from_the_directory_the_file_lives_in()
    {
        // Without this the endpoint root is prepended to the bare staging name, and rclone is asked for
        // "docs/a.txt" at the endpoint root instead of inside "docs" — a different object.
        Assert.Equal("sftp://nas/team/docs",
            CloudTransferQueue.DownloadSourceUri(Download("sftp://nas/team", "docs/a.txt")));
        Assert.Equal("sftp://nas/team/docs/sub",
            CloudTransferQueue.DownloadSourceUri(Download("sftp://nas/team", "docs/sub/b.txt")));
    }

    [Fact]
    public void A_download_at_the_endpoint_root_keeps_the_endpoint_as_it_is()
    {
        Assert.Equal("sftp://nas/team", CloudTransferQueue.DownloadSourceUri(Download("sftp://nas/team", "a.txt")));
        // No configured root: the same rule, with nothing to append to.
        Assert.Equal("sftp://nas", CloudTransferQueue.DownloadSourceUri(Download("sftp://nas", "a.txt")));
        // ...and a nested path composes the directory onto that bare endpoint name.
        Assert.Equal("sftp://nas/docs", CloudTransferQueue.DownloadSourceUri(Download("sftp://nas", "docs/a.txt")));
    }

    [Fact]
    public void A_local_uri_is_left_alone_because_it_has_no_root_to_compose_against()
    {
        // A hand-edited journal is the only way a download can name a local source; composing a path onto it
        // would silently point the transfer at a different directory.
        Assert.Equal(@"C:\tmp\src", CloudTransferQueue.DownloadSourceUri(Download(@"C:\tmp\src", "docs/a.txt")));
    }
}

/// <summary>
/// A download of a nested file, against the bundled rclone over a local tree behind an <c>alias</c> remote.
/// The rule above is pinned as a string; this is the only place that can prove the bytes actually come from
/// the nested object — a same-named file sits at the alias root, so a download that addressed the bare
/// staging name against the endpoint root would still succeed and hand the user the wrong file.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudDownloadPathFactTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-download-path-" + Guid.NewGuid().ToString("N"));
    private string Files => Path.Combine(_root, "files");
    private RcloneDaemon? _daemon;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Files, "docs", "sub"));
        await File.WriteAllTextAsync(Path.Combine(Files, "docs", "a.txt"), new string('a', 1024));
        await File.WriteAllTextAsync(Path.Combine(Files, "docs", "sub", "b.txt"), new string('b', 2048));
        // The decoy: if the download reads its bare staging name from the endpoint root, this is what it gets.
        await File.WriteAllTextAsync(Path.Combine(Files, "b.txt"), new string('X', 7));
        var config = Path.Combine(_root, "rclone.conf");
        await File.WriteAllTextAsync(config, $"[probe]\ntype = alias\nremote = {Files.Replace('\\', '/')}\n");
        _daemon = await RcloneDaemon.StartAsync(RclonePath(), config);
    }

    public async Task DisposeAsync()
    {
        if (_daemon is not null) await _daemon.DisposeAsync();
        if (Directory.Exists(_root)) TryDelete(_root);
    }

    [Fact]
    public async Task A_nested_download_lands_the_nested_object_and_not_the_same_named_file_at_the_root()
    {
        var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        // The resume state lives in application data, so without this the test would write into the
        // developer's own profile and read it back on the next run.
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", Path.Combine(_root, "appdata"));
        try
        {
            var client = _daemon!.Client;
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(Path.Combine(_root, "journal")),
                _ => Task.FromResult(client));
            var destination = Path.Combine(_root, "out", "b.txt");

            var task = queue.Enqueue(CloudTransferDirection.Download, "sftp://probe", "docs/sub/b.txt", destination, 2048);

            Assert.True(await queue.WaitForAsync(task), task.Error);
            Assert.Equal(new string('b', 2048), await File.ReadAllTextAsync(destination));
            // The staging file is published, not left behind: a drag-out of the same folder must find the
            // mirror whole rather than a tree full of partials.
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.fengsync.partial"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
        }
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
        throw new FileNotFoundException("找不到内置 rclone.exe，无法运行下载路径事实测试。");
    }

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
