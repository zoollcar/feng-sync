using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FengSync.Core;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Tests;

/// <summary>
/// What the bundled rclone actually does when it is asked for a public link, measured rather than assumed. The
/// share command is built on two of these answers and one absence: a backend that has no links refuses in words
/// the application recognises, and a backend that does have them answers <c>{"url": …}</c> for a file and
/// <c>object not found</c> for a directory. The supporting case needs a real S3 server, so the test starts one
/// from the same bundled binary (<c>rclone serve s3</c>) on loopback with a fake key pair — no network, no
/// cloud, no credential of anybody's — because a hand-written reply could not prove the field's name.
/// </summary>
public sealed class RclonePublicLinkFactTests : IAsyncLifetime
{
    private const string AccessKey = "AKIAPROBELOCAL";
    private const string SecretKey = "probelocalsecretkey0000000000000000";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-publiclink-facts-" + Guid.NewGuid().ToString("N"));
    private string Served => Path.Combine(_root, "served");
    private Process? _server;
    private RcloneDaemon? _daemon;
    private int _port;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Served, "bucket"));
        await File.WriteAllTextAsync(Path.Combine(Served, "bucket", "note.txt"), "feng sync link probe\n");
        Directory.CreateDirectory(Path.Combine(Served, "bucket", "inbox"));
        await File.WriteAllTextAsync(Path.Combine(Served, "bucket", "inbox", "later.txt"), "inside a folder\n");

        _port = ReservePort();
        _server = StartS3Server();
        await WaitForServer();

        var config = Path.Combine(_root, "rclone.conf");
        await File.WriteAllTextAsync(config,
            $"[local]\ntype = alias\nremote = {Served.Replace('\\', '/')}\n\n"
            + "[s3serve]\ntype = s3\nprovider = AWS\nenv_auth = false\n"
            + $"access_key_id = {AccessKey}\nsecret_access_key = {SecretKey}\n"
            + $"endpoint = http://127.0.0.1:{_port}\nregion = us-east-1\nforce_path_style = true\n");
        _daemon = await RcloneDaemon.StartAsync(RclonePath(), config);
    }

    public async Task DisposeAsync()
    {
        if (_daemon is not null) await _daemon.DisposeAsync();
        if (_server is not null)
        {
            try { _server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _server.Dispose();
        }
        if (Directory.Exists(_root)) TryDelete(_root);
    }

    private RcloneRcClient Client => _daemon!.Client;

    [Fact]
    public async Task A_backend_without_links_refuses_in_the_words_the_app_listens_for()
    {
        // The share rules decide "this endpoint cannot do that" from this sentence, and the shared failure
        // classifier reads the same HTTP 500 as a temporary connection problem — so the wording is load-bearing
        // and has to be pinned against the real binary, not against a string a test author remembered.
        var failure = await Assert.ThrowsAsync<RcloneException>(
            () => Client.PublicLinkAsync("local:", "bucket/note.txt", CancellationToken.None));

        Assert.Contains("support public links", failure.Failure.TechnicalMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_backend_that_has_links_answers_with_the_url_itself()
    {
        var url = await Client.PublicLinkAsync("s3serve:bucket", "note.txt", CancellationToken.None);

        // This is the whole shape the browser depends on: the link arrives under "url", as a complete address a
        // user can hand to somebody else. An S3 link is a presigned read grant, so it also proves the answer is
        // usable without any extra call.
        Assert.False(string.IsNullOrWhiteSpace(url));
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var parsed));
        Assert.Equal(Uri.UriSchemeHttp, parsed!.Scheme);
        Assert.Contains("bucket/note.txt", parsed.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_directory_has_no_link_and_says_so_rather_than_answering_with_the_folder_name()
    {
        // The command is offered for files only because of this: rclone looks the prefix up as an object and the
        // backend calls it missing, so a folder link would be a promise the application cannot keep.
        var failure = await Assert.ThrowsAsync<RcloneException>(
            () => Client.PublicLinkAsync("s3serve:bucket", "inbox", CancellationToken.None));

        Assert.Contains("not found", failure.Failure.TechnicalMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RcloneFailureCategory.NotFound, failure.Failure.Category);
    }

    [Fact]
    public async Task An_object_the_backend_has_dropped_is_answered_as_missing()
    {
        var failure = await Assert.ThrowsAsync<RcloneException>(
            () => Client.PublicLinkAsync("s3serve:bucket", "gone.txt", CancellationToken.None));

        Assert.Equal(RcloneFailureCategory.NotFound, failure.Failure.Category);
    }

    private Process StartS3Server()
    {
        var start = new ProcessStartInfo(RclonePath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "serve", "s3", Served,
            "--addr", $"127.0.0.1:{_port}",
            "--auth-key", $"{AccessKey},{SecretKey}",
            "--log-level", "ERROR",
        }) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动内置 rclone 的 S3 服务。");
        // Drained rather than left to fill: a server whose log buffer blocks would hang the suite, not fail it.
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private async Task WaitForServer()
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (_server!.HasExited) throw new InvalidOperationException("内置 rclone 的 S3 服务没能启动。");
            try
            {
                // Any HTTP answer at all means the port is live; unsigned requests are refused with 403, which is
                // exactly the refusal this test then proves a signed client does not get.
                await probe.GetAsync($"http://127.0.0.1:{_port}/");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException("等待内置 rclone 的 S3 服务超时。");
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
        throw new FileNotFoundException("找不到内置 rclone.exe，无法运行分享链接事实测试。");
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
