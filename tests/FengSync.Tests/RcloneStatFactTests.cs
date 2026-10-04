using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FengSync.Core;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Tests;

/// <summary>
/// What the bundled rclone's <c>operations/stat</c> actually does, measured rather than assumed. The details
/// panel is built on this call, and every rule about what it may claim comes from one of these answers: a
/// MIME type the listing never reports, a checksum that only arrives when it is named, a checksum named
/// wrongly refused out loud, and a missing object answered as a value instead of an error. These run against
/// the real binary through the real daemon over a local directory behind an <c>alias</c> remote, so no
/// network and no credential is involved — and a fake could not prove any of it.
/// </summary>
public sealed class RcloneStatFactTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-stat-facts-" + Guid.NewGuid().ToString("N"));
    private string Files => Path.Combine(_root, "files");
    private RcloneDaemon? _daemon;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Files);
        await File.WriteAllTextAsync(Path.Combine(Files, "note.txt"), "feng sync stat probe\n");
        Directory.CreateDirectory(Path.Combine(Files, "inbox"));
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
    public async Task The_object_itself_reports_the_type_a_listing_never_gives()
    {
        var stat = await Client.StatAsync("probe:", "note.txt", null);

        Assert.NotNull(stat);
        Assert.False(stat!.IsDirectory);
        Assert.Equal(Encoding.UTF8.GetByteCount(await File.ReadAllTextAsync(Path.Combine(Files, "note.txt"))), stat.Size);
        // This is the row 属性 can only fill from the object: operations/list carries no MimeType field for the
        // browser's entry model, so without this call the panel would have to guess from the extension.
        Assert.False(string.IsNullOrWhiteSpace(stat.MimeType));
    }

    [Fact]
    public async Task A_directory_is_reported_as_one_and_still_comes_back_with_a_type()
    {
        var stat = await Client.StatAsync("probe:", "inbox", null);

        Assert.True(stat!.IsDirectory);
        Assert.Equal("inode/directory", stat.MimeType);
    }

    [Fact]
    public async Task No_checksum_is_reported_until_the_call_names_one()
    {
        var unasked = await Client.StatAsync("probe:", "note.txt", []);
        var asked = await Client.StatAsync("probe:", "note.txt", ["md5"]);

        // The panel's 未计算 / 此后端未提供校验值 split exists because of this pair: an answer with no
        // checksums is what rclone gives when it was not asked, so the application has to remember which
        // question it sent to say which of the two is true.
        Assert.Empty(unasked!.Hashes);
        Assert.Equal(
            Convert.ToHexString(MD5.HashData(await File.ReadAllBytesAsync(Path.Combine(Files, "note.txt")))).ToLowerInvariant(),
            asked!.Hashes["md5"]);
    }

    [Fact]
    public async Task A_directory_asked_for_a_checksum_reports_none_rather_than_failing()
    {
        var stat = await Client.StatAsync("probe:", "inbox", ["md5"]);

        // The folder row may offer no 计算校验值 button, but the read itself is harmless: nothing here would
        // make a panel that asked for one wrong, it would only be a waste.
        Assert.Empty(stat!.Hashes);
    }

    [Fact]
    public async Task A_checksum_named_wrong_is_refused_loudly_instead_of_answered_as_absent()
    {
        // What makes the silent-override failure mode impossible: rclone rejects a hash type it does not
        // know, so a misspelled name in HashPreference surfaces in a test rather than reading as "this
        // backend has no checksums".
        var failure = await Assert.ThrowsAsync<RcloneException>(() => Client.StatAsync("probe:", "note.txt", ["bogus"]));

        Assert.Contains("unknown hash type", failure.Failure.TechnicalMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_object_is_answered_as_an_empty_result_rather_than_an_error()
    {
        var stat = await Client.StatAsync("probe:", "gone.txt", null);

        // v1.75.0 answers {"item":null} here, which is why "the file is no longer in the cloud" is a value the
        // panel can show instead of an exception it would have to classify.
        Assert.Null(stat);
    }

    [Fact]
    public async Task The_raw_answer_carries_the_null_item_that_makes_a_gone_object_a_value()
    {
        // Pinned at the wire as well as through the parser, because the reading "no item means gone, not
        // failed" is the one the details panel depends on and an upgrade that turned this into an error would
        // otherwise only show up as a differently-worded panel.
        var response = await Client.CallAsync("operations/stat", new { fs = "probe:", remote = "gone.txt", opt = new { hashTypes = new[] { "md5" } } });

        Assert.True(response.TryGetProperty("item", out var item));
        Assert.Equal(JsonValueKind.Null, item.ValueKind);
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
        throw new FileNotFoundException("找不到内置 rclone.exe，无法运行 stat 事实测试。");
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
