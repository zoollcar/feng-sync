using System.Text;
using FengSync.Core;

namespace FengSync.Tests;

/// <summary>
/// What the bundled rclone's <c>--rc-serve</c> endpoint actually answers when one object's bytes are asked for,
/// measured rather than assumed. A preview-sized read is the first consumer of these answers, and every rule it
/// has to follow comes from one of them: the limit is honoured as a range that the cloud <em>clamps</em> instead
/// of refusing, so "this is bigger than we will show" is recognised from one round trip rather than by pulling
/// the whole object first; a gone object and a directory are two different answers; and an empty object answers
/// with a <c>Content-Range</c> no honest parser can read. These run against the real binary through the real
/// daemon over a local directory behind an <c>alias</c> remote, so no network and no credential is involved —
/// and a hand-written reply could not prove any of it.
/// </summary>
public sealed class RcloneServeReadFactTests : IAsyncLifetime
{
    private static readonly byte[] Note = "feng sync serve probe\nsecond line\n"u8.ToArray();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-serve-read-facts-" + Guid.NewGuid().ToString("N"));
    private string Files => Path.Combine(_root, "files");
    private RcloneDaemon? _daemon;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Files);
        await File.WriteAllBytesAsync(Path.Combine(Files, "note.txt"), Note);
        // Random bytes, so nothing in here can accidentally look like a decodable image or a valid text file.
        await File.WriteAllBytesAsync(Path.Combine(Files, "big.bin"), new byte[4096].Select((_, index) => (byte)(index * 31 + 7)).ToArray());
        await File.WriteAllBytesAsync(Path.Combine(Files, "empty.bin"), []);
        Directory.CreateDirectory(Path.Combine(Files, "inbox"));
        await File.WriteAllTextAsync(Path.Combine(Files, "inbox", "inner.txt"), "inside a folder\n");

        var config = Path.Combine(_root, "rclone.conf");
        await File.WriteAllTextAsync(config, $"[probe]\ntype = alias\nremote = {Files.Replace('\\', '/')}\n");
        _daemon = await RcloneDaemon.StartAsync(RclonePath(), config);
    }

    public async Task DisposeAsync()
    {
        if (_daemon is not null) await _daemon.DisposeAsync();
        if (Directory.Exists(_root)) TryDelete(_root);
    }

    private RcloneRcClient Client => _daemon!.Client;

    [Fact]
    public async Task An_object_within_the_limit_comes_back_byte_for_byte_with_its_own_size()
    {
        var read = await Client.ReadAsync("probe:", "note.txt", 1 << 20);

        Assert.Equal(RcloneReadStatus.Read, read.Status);
        Assert.Equal(Note, read.Bytes);
        Assert.Equal(Note.Length, read.Total);
    }

    [Fact]
    public async Task A_limit_wider_than_the_object_is_clamped_by_the_cloud_instead_of_refused()
    {
        // The whole design of a bounded read rests on this: asking for a megabyte of a 54-byte object is not an
        // error. rclone answers 206 with "bytes 0-53/54" — measured — rather than 416, so the caller does not
        // have to know the size before it asks.
        var read = await Client.ReadAsync("probe:", "note.txt", 1 << 20);
        var sameAgain = await Client.ReadAsync("probe:", "note.txt", Note.Length);

        Assert.Equal(RcloneReadStatus.Read, read.Status);
        // A limit exactly as wide as the object is the boundary case, and it still reads: the range is
        // bytes=0-(size-1), which is the object.
        Assert.Equal(RcloneReadStatus.Read, sameAgain.Status);
        Assert.Equal(Note, sameAgain.Bytes);
    }

    [Fact]
    public async Task An_object_beyond_the_limit_is_reported_as_too_big_and_its_beginning_is_thrown_away()
    {
        var read = await Client.ReadAsync("probe:", "big.bin", 1024);

        // The first kilobyte is in hand and deliberately discarded: a tile that painted it would be showing a
        // truncated object while claiming to show the object. The total still arrives, from the range answer.
        Assert.Equal(RcloneReadStatus.TooLarge, read.Status);
        Assert.Empty(read.Bytes);
        Assert.Equal(4096, read.Total);
    }

    [Fact]
    public async Task An_empty_object_reads_as_empty_rather_than_as_a_refusal()
    {
        var read = await Client.ReadAsync("probe:", "empty.bin", 1 << 20);

        // Measured: a 0-byte object answers 206 with an empty body and a Content-Range of "bytes 0--1/0", which
        // is not a range any parser should be trusted to read. The caller therefore has to treat an empty read as
        // an empty object, and a preview that decodes bytes has to refuse this one in its own words.
        Assert.Equal(RcloneReadStatus.Read, read.Status);
        Assert.Empty(read.Bytes);
        Assert.Equal(0, read.Total);
    }

    [Fact]
    public async Task An_object_the_cloud_no_longer_has_is_answered_as_missing()
    {
        var read = await Client.ReadAsync("probe:", "gone.bin", 1 << 20);

        Assert.Equal(RcloneReadStatus.Missing, read.Status);
        Assert.Empty(read.Bytes);
        // The object that is there is the control for this answer: 404 is about the name, not about the endpoint
        // refusing to serve at all.
        Assert.Equal(RcloneReadStatus.Read, (await Client.ReadAsync("probe:", "inbox/inner.txt", 1 << 20)).Status);
    }

    [Fact]
    public async Task A_directory_refuses_in_its_own_words_and_those_words_survive()
    {
        var read = await Client.ReadAsync("probe:", "inbox", 1 << 20);

        // rclone answers HTTP 500 "failed to find object: is a directory not a file" for a directory. The shared
        // failure classifier reads any 500 as a temporary connection problem, so this path keeps the sentence
        // instead of a category — a preview of a folder has to say what the cloud said, not "connection中断".
        Assert.Equal(RcloneReadStatus.Failed, read.Status);
        Assert.False(string.IsNullOrWhiteSpace(read.Detail));
        Assert.Contains("directory", read.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_header_probe_reports_the_size_without_any_body()
    {
        var identity = await Client.ProbeAsync("probe:", "big.bin");

        Assert.NotNull(identity);
        Assert.Equal(4096, identity!.Length);
        Assert.Null(await Client.ProbeAsync("probe:", "gone.bin"));
        // A directory is answered with the same 500 as a read, so the probe reports "no size" rather than 0.
        Assert.Null(await Client.ProbeAsync("probe:", "inbox"));
    }

    [Fact]
    public async Task An_offset_past_the_end_reads_as_nothing_and_gives_no_total_to_trust()
    {
        // Measured: bytes=100-200 on a 54-byte object answers 206 with an empty body and a Content-Range of
        // "bytes 100-53/54" — a range whose end is below its start, which no header parser is obliged to read.
        // So the caller gets neither an error nor a usable total. A resume at a stale offset therefore cannot tell
        // "the object ends here" from "the object shrank under me" out of this answer, which is exactly why the
        // resume path compares the bytes across the seam instead of trusting a status code or a header.
        var content = await Client.OpenRangeAsync("probe:", "note.txt", 100);

        Assert.NotNull(content);
        using (content)
        {
            var bytes = new byte[64];
            Assert.Equal(0, await content.Content.ReadAsync(bytes));
            Assert.Equal(-1, content.Length);
        }
    }

    [Fact]
    public async Task A_path_holding_a_space_and_a_hash_still_reaches_the_object_it_names()
    {
        var awkward = Path.Combine(Files, "a file #1.txt");
        await File.WriteAllTextAsync(awkward, "awkward name\n");

        var read = await Client.ReadAsync("probe:", "a file #1.txt", 1 << 20);
        var missing = await Client.ReadAsync("probe:", "a file #2.txt", 1 << 20);

        // Each URL segment is escaped, so '#' is not read as a fragment and the name resolves to the file. Without
        // this, a browser that addressed rows by path would silently preview the wrong object.
        Assert.Equal(RcloneReadStatus.Read, read.Status);
        Assert.Equal("awkward name\n"u8.ToArray(), read.Bytes);
        Assert.Equal(RcloneReadStatus.Missing, missing.Status);
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
        throw new FileNotFoundException("找不到内置 rclone.exe，无法运行 rc-serve 读取事实测试。");
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
