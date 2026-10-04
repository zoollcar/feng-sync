using System.Text.Json;
using FengSync.Core;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Tests;

/// <summary>
/// What the bundled rclone's <c>operations/size</c> actually does, measured rather than assumed. The whole 统计大小
/// feature is a promise about this call's answer, so each half of that promise is pinned here against the real
/// binary through the real daemon: the directory has to ride inside <c>fs</c> because a <c>remote</c> beside it is
/// silently ignored, the count excludes directories, an empty folder is an answer rather than a failure, and the
/// two refusals — a folder that is gone and a row that is a file — come back as sentences the panel reads. A local
/// directory behind an <c>alias</c> remote means no network and no credential, and a fake could prove none of it.
/// </summary>
public sealed class RcloneSizeFactTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-size-facts-" + Guid.NewGuid().ToString("N"));
    private string Files => Path.Combine(_root, "files");
    private RcloneDaemon? _daemon;

    /// <summary>The bytes written directly under the endpoint root.</summary>
    private const long TopBytes = 1_024 + 2_048;

    /// <summary>The bytes written under <c>sub</c>, the one directory this fixture sizes.</summary>
    private const long SubBytes = 4_096;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Files);
        await Write("top-a.txt", 1_024);
        await Write("top-b.txt", 2_048);
        Directory.CreateDirectory(Path.Combine(Files, "sub"));
        await Write("sub/one.txt", 4_096);
        // A directory holding a directory: the innermost one is empty and the middle one holds one object, so a
        // count that included folders would differ from one that does not by exactly two.
        Directory.CreateDirectory(Path.Combine(Files, "sub", "deeper"));
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

    private Task Write(string relative, int bytes) =>
        File.WriteAllBytesAsync(Path.Combine(Files, relative.Replace('/', Path.DirectorySeparatorChar)), new byte[bytes]);

    private RcloneRcClient Client => _daemon!.Client;

    [Fact]
    public async Task A_directory_inside_the_fs_is_sized_alone_rather_than_with_the_whole_endpoint()
    {
        var sub = await Client.SizeAsync("probe:sub");
        var top = await Client.SizeAsync("probe:");

        // The pair is the shape of the trap: one call answers one subtree and the other answers everything under
        // the remote, and both look perfectly reasonable on a folder row.
        Assert.Equal(SubBytes, sub!.Bytes);
        Assert.Equal(1, sub.Count);
        Assert.Equal(TopBytes + SubBytes, top!.Bytes);
        Assert.Equal(3, top.Count);
    }

    [Fact]
    public async Task A_remote_field_sent_alongside_the_fs_is_ignored_rather_than_narrowing_the_walk()
    {
        // Measured, and the reason the client has no remote parameter: this ask names the subtree in the field
        // rclone ignores, and what comes back is the whole endpoint's total — 3,072 bytes and 3 objects — with no
        // error and no hint that the question was misread.
        var response = await Client.CallAsync("operations/size", new { fs = "probe:", remote = "sub" });

        Assert.Equal(TopBytes + SubBytes, ReadLong(response, "bytes"));
        Assert.Equal(3, ReadLong(response, "count"));
    }

    [Fact]
    public async Task The_count_counts_objects_and_leaves_the_directories_out()
    {
        var sub = await Client.SizeAsync("probe:sub");

        // Walking <c>sub</c> touches three entries — one file and two directories — and reports one. The row
        // therefore says "1 个对象" for a folder that visibly holds two folders, which is only honest because the
        // number is described as objects rather than as entries.
        Assert.Equal(1, sub!.Count);
        Assert.Equal(SubBytes, sub.Bytes);
    }

    [Fact]
    public async Task An_empty_directory_answers_with_zeros_rather_than_an_error()
    {
        var empty = await Client.SizeAsync("probe:sub/deeper");

        // The distinction 0 个对象，空目录 rests on: this is the same shape a failed read would leave, so the
        // panel has to call it an answer and not report it as 未能统计.
        Assert.NotNull(empty);
        Assert.Equal(0, empty!.Bytes);
        Assert.Equal(0, empty.Count);
        Assert.Equal(0, empty.Sizeless);
    }

    [Fact]
    public async Task A_directory_that_is_not_there_is_refused_in_words_the_panel_reads()
    {
        var failure = await Assert.ThrowsAsync<RcloneException>(() => Client.SizeAsync("probe:gone"));

        // 404 and its sentence, not a status the classifier would call a connection problem: an uncounted folder
        // and a vanished one have to be two different rows.
        Assert.Equal(404, failure.Failure.HttpStatus);
        Assert.Contains("directory not found", failure.Failure.TechnicalMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_file_asked_for_a_subtree_is_refused_in_words_the_panel_reads()
    {
        var failure = await Assert.ThrowsAsync<RcloneException>(() => Client.SizeAsync("probe:top-a.txt"));

        // HTTP 500 with "is a file not a directory" — a category that reads as a temporary failure while the fact
        // is that this row was never sizeable.
        Assert.Equal(500, failure.Failure.HttpStatus);
        Assert.Contains("not a directory", failure.Failure.TechnicalMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_answer_reports_sizeless_as_its_own_field_rather_than_folding_it_in()
    {
        var response = await Client.CallAsync("operations/size", new { fs = "probe:" });

        // Pinned at the wire because the whole wording of 其中 N 个云端不报大小 depends on rclone separating the
        // objects it could not size from the bytes it did: a build that quietly added them together would make
        // the panel's floor a total.
        Assert.True(response.TryGetProperty("sizeless", out var sizeless));
        Assert.Equal(0, sizeless.GetInt64());
        Assert.Equal(3, response.GetProperty("count").GetInt64());
    }

    private static long ReadLong(JsonElement answer, string name) =>
        answer.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : -1;

    private static string RclonePath()
    {
        try { return BundledRclone.ExecutablePath; }
        catch (FileNotFoundException) { }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "FengSync", "Assets", "rclone", "rclone.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到内置 rclone.exe，无法运行 size 事实测试。");
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
