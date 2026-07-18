using FengSync.Core;
using System.Net;
using System.Net.Http;
using System.Text;

namespace FengSync.Tests;

public sealed class RcloneEndpointTests : IDisposable
{
    private readonly RecordingHandler _handler = new();
    private readonly HttpClient _http;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-rclone-" + Guid.NewGuid().ToString("N"));
    public RcloneEndpointTests() { _http = new HttpClient(_handler) { BaseAddress = new Uri("http://rc.test/") }; Directory.CreateDirectory(_root); }
    public void Dispose() { _http.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task Remote_scan_excludes_internal_files_and_reads_hash()
    {
        _handler.ListJson = "{\"list\":[{\"Path\":\"sync.fengdb\",\"IsDir\":false},{\"Path\":\"folder/a.txt\",\"IsDir\":false,\"Size\":4,\"ModTime\":\"2026-01-01T00:00:00Z\",\"Hashes\":{\"md5\":\"ABCD\"}},{\"Path\":\"empty\",\"IsDir\":true}]}";
        var endpoint = Remote(EndpointType.Sftp);
        var items = await endpoint.ScanAsync();
        Assert.Equal(2, items.Count); Assert.Equal("ABCD", items.Single(x => x.Path == "folder/a.txt").Fingerprint!.Hash); Assert.Contains(_handler.Requests, x => x.AbsolutePath.EndsWith("operations/list"));
    }

    [Fact]
    public async Task Remote_scan_normalizes_a_listing_that_includes_its_configured_root()
    {
        _handler.ListJson = "{\"list\":[{\"Path\":\"root/from-drive.txt\",\"IsDir\":false,\"Size\":5,\"ModTime\":\"2026-01-01T00:00:00Z\"}]}";

        var item = Assert.Single(await Remote(EndpointType.GoogleDrive).ScanAsync());

        Assert.Equal("from-drive.txt", item.Path);
    }

    [Fact]
    public async Task Directory_listing_includes_empty_and_implicit_parent_folders()
    {
        _handler.ListJson = "{\"list\":[{\"Path\":\"empty\",\"IsDir\":true},{\"Path\":\"one/two/file.txt\",\"IsDir\":false}]}";
        var client = new RcloneRcClient(_http, _http.BaseAddress!, "user", "pass");
        var directories = await client.ListDirectoriesAsync("remote", "", false);
        Assert.Equal(["empty", "one", "one/two"], directories);
        Assert.Contains(_handler.Bodies, x => x.Contains("\"recurse\":false", StringComparison.Ordinal));
        var tree = RemoteDirectoryTree.Build(directories);
        Assert.Equal(["empty", "one"], tree.Children.Select(x => x.Name)); Assert.Equal("one/two", Assert.Single(tree.Children.Single(x => x.Name == "one").Children).Path);
    }

    [Theory]
    [InlineData("cloudfile/入职", "cloudfile", "入职")]
    [InlineData("入职", "cloudfile", "入职")]
    [InlineData("cloudfile/contacts/a", "cloudfile", "contacts/a")]
    public void Directory_paths_are_not_prefixed_twice(string listed, string root, string expected) => Assert.Equal(expected, RemoteDirectoryTree.RelativeToListingRoot(listed, root));

    [Theory]
    [InlineData(EndpointType.Sftp)]
    [InlineData(EndpointType.GoogleDrive)]
    public async Task Local_to_remote_uses_the_same_safe_copy_then_move_protocol(EndpointType type)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "local.txt"), "content");
        var remote = Remote(type); var plan = await new EndpointSynchronizer().SynchronizeAsync(new LocalEndpoint(_root), remote, SyncMode.Update);
        Assert.Single(plan.Operations); Assert.Contains(_handler.Requests, x => x.AbsolutePath.EndsWith("operations/copyfile")); Assert.Contains(_handler.Requests, x => x.AbsolutePath.EndsWith("operations/movefile"));
        Assert.Contains(_handler.Bodies, x => x.Contains(".fengsync-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remote_calls_use_a_colon_qualified_rclone_filesystem()
    {
        _handler.ListJson = "{\"list\":[]}";
        await Remote(EndpointType.GoogleDrive).ScanAsync();
        Assert.Contains(_handler.Bodies, x => x.Contains("\"fs\":\"test:\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remote_executor_honors_the_configured_copy_concurrency()
    {
        _handler.DelayCopy = true;
        var source = Remote(EndpointType.Sftp); var target = Remote(EndpointType.Sftp);
        var plan = new SyncPlan([new SyncOperation("one.txt", OperationKind.CopyLeftToRight, "test"), new SyncOperation("two.txt", OperationKind.CopyLeftToRight, "test")]);
        await new EndpointExecutor().ExecuteAsync(plan, source, target, maxConcurrentCopies: 2);
        Assert.True(_handler.MaximumConcurrentCopies >= 2);
    }

    private RcloneEndpoint Remote(EndpointType type) => new(new RcloneRcClient(_http, _http.BaseAddress!, "user", "pass"), new EndpointProfile(Guid.NewGuid(), type, "root", "test"), new(false, true, true, TimeSpan.FromSeconds(1)));
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string ListJson { get; set; } = "{\"list\":[]}";
        public List<Uri> Requests { get; } = []; public List<string> Bodies { get; } = [];
        public bool DelayCopy { get; set; } public int MaximumConcurrentCopies { get; private set; } private int _concurrentCopies;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add(request.RequestUri!); Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)); if (DelayCopy && request.RequestUri!.AbsolutePath.EndsWith("operations/copyfile")) { var current = Interlocked.Increment(ref _concurrentCopies); MaximumConcurrentCopies = Math.Max(MaximumConcurrentCopies, current); await Task.Delay(80, cancellationToken); Interlocked.Decrement(ref _concurrentCopies); } var payload = request.RequestUri!.AbsolutePath.EndsWith("operations/list") ? ListJson : "{}"; return new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") }; }
    }
}
