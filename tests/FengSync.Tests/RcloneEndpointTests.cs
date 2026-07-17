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

    private RcloneEndpoint Remote(EndpointType type) => new(new RcloneRcClient(_http, _http.BaseAddress!, "user", "pass"), new EndpointProfile(Guid.NewGuid(), type, "root", "test"), new(false, true, true, TimeSpan.FromSeconds(1)));
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string ListJson { get; set; } = "{\"list\":[]}";
        public List<Uri> Requests { get; } = []; public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add(request.RequestUri!); Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)); var payload = request.RequestUri!.AbsolutePath.EndsWith("operations/list") ? ListJson : "{}"; return new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") }; }
    }
}
