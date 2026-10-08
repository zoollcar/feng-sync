using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FengSync.Core;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// 新建文件夹 on an S3 endpoint. An S3 bucket has no directories, so rclone answers
/// <c>operations/mkdir</c> with success and creates nothing — which is why the browser used to report a
/// folder that the very next listing could not find. The folder exists only as an object whose name ends
/// with <c>/</c>, the shape rclone's own <c>--s3-directory-markers</c> writes, so the browser has to write
/// that object itself.
/// </summary>
/// <remarks>
/// The fact cases run against the bundled rclone over a loopback <c>rclone serve s3</c> and its own
/// <c>s3</c> backend, so "mkdir does nothing" and "the marker lands" are measured rather than assumed. The
/// remote is declared with rclone's <c>Minio</c> provider on purpose: rclone's <c>AWS</c> preset signs a
/// plain-HTTP request with a payload hash it can only compute from a seekable body, and every write against
/// this loopback server then fails with "request stream is not seekable" — an rclone/serve-s3 limitation
/// that has nothing to do with the behaviour under test. <c>no_check_bucket</c> is set for the same reason:
/// <c>serve s3</c> answers <c>CreateBucket</c> with 409 <c>BucketAlreadyExists</c>, which rclone's own
/// bucket check treats as fatal, while real S3 answers <c>BucketAlreadyOwnedByYou</c>.
/// </remarks>
[Collection(ProcessWideStateCollection.Name)]
public sealed class S3DirectoryMarkerFactTests : IAsyncLifetime
{
    private const string AccessKey = "AKIAPROBELOCAL";
    private const string SecretKey = "probelocalsecretkey0000000000000000";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-s3-marker-" + Guid.NewGuid().ToString("N"));
    private string Served => Path.Combine(_root, "served");
    private Process? _server;
    private RcloneDaemon? _daemon;
    private int _port;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Served, "bucket", "team"));
        await File.WriteAllTextAsync(Path.Combine(Served, "bucket", "team", "existing.txt"), "existing\n");

        _port = ReservePort();
        _server = StartS3Server();
        await WaitForServer();

        var config = Path.Combine(_root, "rclone.conf");
        await File.WriteAllTextAsync(config,
            $"[docs_s3]\ntype = s3\nprovider = Minio\nenv_auth = false\n"
            + $"access_key_id = {AccessKey}\nsecret_access_key = {SecretKey}\n"
            + $"endpoint = http://127.0.0.1:{_port}\nregion = us-east-1\nforce_path_style = true\nno_check_bucket = true\n\n"
            + "[docs]\ntype = alias\nremote = docs_s3:bucket/team\n");
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

    // ------------------------------------------------------------------ measured against the bundled rclone

    [Fact]
    public async Task A_plain_mkdir_on_an_s3_endpoint_reports_success_and_creates_nothing()
    {
        var files = new CloudFileManagerService(_ => Task.FromResult(_daemon!.Client));

        // This is the root cause, pinned: rclone itself logs "running mkdir on a remote which can't have
        // empty directories does nothing" and the listing is exactly what it was. Without the marker the
        // browser is reporting a folder that does not exist.
        await files.CreateDirectoryAsync("docs", "newfolder");

        Assert.DoesNotContain(await files.ListAsync("docs", ""), entry => entry.Name == "newfolder");
    }

    [Fact]
    public async Task The_marker_write_leaves_something_at_the_folder_on_the_cloud()
    {
        var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", Path.Combine(_root, "appdata"));
        try
        {
            var files = new CloudFileManagerService(_ => Task.FromResult(_daemon!.Client));

            await files.CreateDirectoryAsync("docs", "newfolder", persistEmptyDirectory: true);

            // The object is the folder. It is the only thing an S3 endpoint can hold for an empty directory,
            // and it is what a later listing reads back as a directory.
            Assert.True(File.Exists(Path.Combine(Served, "bucket", "team", "newfolder")),
                "标记对象没有写进端点：" + string.Join(", ", Directory.GetFileSystemEntries(Path.Combine(Served, "bucket", "team"))));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
        }
    }

    [Fact]
    public async Task An_upload_still_lands_beside_the_folders_the_browser_created()
    {
        var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", Path.Combine(_root, "appdata"));
        try
        {
            var client = _daemon!.Client;
            var local = Path.Combine(_root, "upload.txt");
            await File.WriteAllTextAsync(local, "hello from the queue\n");
            var queue = new CloudTransferQueue(new CloudTransferJournalStore(Path.Combine(_root, "journal")),
                _ => Task.FromResult(client));

            var task = queue.Enqueue(CloudTransferDirection.Upload, "s3://docs", "upload.txt", local, new FileInfo(local).Length);

            Assert.True(await queue.WaitForAsync(task), task.Error ?? "没有错误");
            Assert.Equal("hello from the queue\n",
                await File.ReadAllTextAsync(Path.Combine(Served, "bucket", "team", "upload.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
        }
    }

    // ------------------------------------------------------------------ what the marker call looks like

    [Fact]
    public async Task The_marker_is_a_copy_to_the_folder_name_ending_in_a_slash()
    {
        var handler = new RecordingRclone();
        var files = new CloudFileManagerService(_ => Task.FromResult(handler.Client));

        await files.CreateDirectoryAsync("docs", "newfolder", persistEmptyDirectory: true);

        // The trailing slash IS the marker: it is what makes rclone read the object back as a directory.
        // The loopback server cannot show that — it maps a key onto a local path and drops the slash — so
        // the name is pinned here instead.
        var copy = Assert.Single(handler.Calls, call => call.Path == "operations/copyfile");
        Assert.Equal("docs:", copy.Payload.GetProperty("dstFs").GetString());
        Assert.Equal("newfolder/", copy.Payload.GetProperty("dstRemote").GetString());
        Assert.Equal("empty", copy.Payload.GetProperty("srcRemote").GetString());
        // The zero-byte source lives in application data, never beside the user's own files.
        Assert.Equal(0, new FileInfo(Path.Combine(copy.Payload.GetProperty("srcFs").GetString()!, "empty")).Length);
    }

    [Fact]
    public async Task An_endpoint_that_has_directories_is_not_given_a_marker()
    {
        var handler = new RecordingRclone();
        var files = new CloudFileManagerService(_ => Task.FromResult(handler.Client));

        await files.CreateDirectoryAsync("docs", "newfolder");

        // 新建文件夹 on Drive, SFTP or WebDAV must stay exactly one mkdir: a marker object there would be a
        // stray file carrying the folder's name.
        Assert.Single(handler.Calls);
        Assert.Equal("operations/mkdir", handler.Calls[0].Path);
    }

    [Theory]
    [InlineData("s3", true)]
    [InlineData("alias", true)]
    [InlineData("sftp", false)]
    [InlineData("drive", false)]
    public void Only_a_backend_without_directories_asks_for_a_marker(string rcloneType, bool expected)
    {
        // The rule reads the rclone backend type, so an enforced-prefix endpoint — an alias over an S3
        // store — is covered by the same row as a plain s3 remote.
        var account = new CloudEndpointAccount(new RcloneAccount("docs", rcloneType), null);

        Assert.Equal(expected, account.NeedsEmptyDirectoryMarker);
    }

    // ------------------------------------------------------------------ harness

    private sealed record RcloneCall(string Path, JsonElement Payload);

    /// <summary>
    /// Answers the two calls a directory creation can make and records what was sent, so the marker's shape
    /// can be asserted without a daemon. The bundled rclone cannot serve as the oracle for the name: a
    /// loopback server stores the key as a path and loses the trailing slash on the way back.
    /// </summary>
    private sealed class RecordingRclone : HttpMessageHandler
    {
        public List<RcloneCall> Calls { get; } = [];
        public RcloneRcClient Client { get; }

        public RecordingRclone()
        {
            var http = new HttpClient(this) { BaseAddress = new Uri("http://rc.test/") };
            Client = new RcloneRcClient(http, new Uri("http://rc.test/"), "user", "pass");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new RcloneCall(path, body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone()));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
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
        throw new FileNotFoundException("找不到内置 rclone.exe。");
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
