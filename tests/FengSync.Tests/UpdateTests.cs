using System.Net;
using System.Text;
using FengSync.Core.Updates;

namespace FengSync.Tests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData("v0.1.16", "0.1.16")]
    [InlineData("0.1.16", "0.1.16")]
    public void Release_version_parses_official_three_part_versions(string input, string expected)
        => Assert.Equal(expected, ReleaseVersion.Parse(input).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("0.1")]
    [InlineData("v1.a.0")]
    public void Release_version_rejects_invalid_values(string input) => Assert.False(ReleaseVersion.TryParse(input, out _));

    [Fact]
    public async Task Client_returns_update_and_sends_etag()
    {
        var handler = new FakeHandler(Json("v0.2.0")); var client = new GitHubReleaseClient(new HttpClient(handler));
        var result = await client.CheckAsync(ReleaseVersion.Parse("0.1.0"), "\"old\"");
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status); Assert.Equal("\"old\"", handler.Request!.Headers.IfNoneMatch.Single().ToString());
        Assert.Equal("v0.2.0", result.Release!.Tag);
    }

    [Fact]
    public async Task Client_filters_invalid_release_shapes()
    {
        foreach (var json in new[] { Json("v0.2.0", draft: true), Json("v0.2.0", prerelease: true), Json("not-a-version"), Json("v0.2.0", assets: false), Json("v0.2.0", github: false) })
            Assert.Equal(UpdateCheckStatus.InvalidRelease, (await new GitHubReleaseClient(new HttpClient(new FakeHandler(json))).CheckAsync(ReleaseVersion.Parse("0.1.0"))).Status);
    }

    [Fact]
    public async Task Client_handles_latest_304_rate_limit_and_offline()
    {
        Assert.Equal(UpdateCheckStatus.Latest, (await new GitHubReleaseClient(new HttpClient(new FakeHandler(Json("v0.1.0")))).CheckAsync(ReleaseVersion.Parse("0.1.0"))).Status);
        Assert.Equal(UpdateCheckStatus.NotModified, (await new GitHubReleaseClient(new HttpClient(new FakeHandler("", HttpStatusCode.NotModified))).CheckAsync(ReleaseVersion.Parse("0.1.0"))).Status);
        Assert.Equal(UpdateCheckStatus.RateLimited, (await new GitHubReleaseClient(new HttpClient(new FakeHandler("", HttpStatusCode.Forbidden))).CheckAsync(ReleaseVersion.Parse("0.1.0"))).Status);
        Assert.Equal(UpdateCheckStatus.Offline, (await new GitHubReleaseClient(new HttpClient(new ThrowingHandler())).CheckAsync(ReleaseVersion.Parse("0.1.0"))).Status);
    }

    [Fact]
    public async Task Client_preserves_response_etag_and_distinguishes_404_and_timeout()
    {
        var response = await new GitHubReleaseClient(new HttpClient(new FakeHandler(Json("v0.2.0"), HttpStatusCode.OK, "\"new\""))).CheckAsync(ReleaseVersion.Parse("0.1.0"));
        Assert.Equal("\"new\"", response.Etag);
        Assert.Equal(UpdateCheckStatus.NotFound, (await new GitHubReleaseClient(new HttpClient(new FakeHandler("", HttpStatusCode.NotFound))).CheckAsync(ReleaseVersion.Parse("0.1.0"))).Status);
        Assert.Equal(UpdateCheckStatus.Timeout, (await new GitHubReleaseClient(new HttpClient(new TimeoutHandler())).CheckAsync(ReleaseVersion.Parse("0.1.0"))).Status);
    }

    private static string Json(string tag, bool draft = false, bool prerelease = false, bool assets = true, bool github = true)
    {
        var host = github ? "https://github.com/zoollcar/feng-sync/releases/download/x/" : "https://example.test/";
        var files = assets ? $"\"assets\":[{{\"name\":\"FengSync-{tag.TrimStart('v')}-win-x64.zip\",\"browser_download_url\":\"{host}FengSync-{tag.TrimStart('v')}-win-x64.zip\",\"size\":123}},{{\"name\":\"FengSync-{tag.TrimStart('v')}-win-x64.zip.sha256\",\"browser_download_url\":\"{host}FengSync-{tag.TrimStart('v')}-win-x64.zip.sha256\",\"size\":10}}]" : "\"assets\":[]";
        return $"{{\"draft\":{draft.ToString().ToLowerInvariant()},\"prerelease\":{prerelease.ToString().ToLowerInvariant()},\"tag_name\":\"{tag}\",\"name\":\"Release\",\"body\":\"notes\",\"html_url\":\"https://github.com/zoollcar/feng-sync/releases/tag/{tag}\",{files}}}";
    }
    private sealed class FakeHandler(string content, HttpStatusCode status = HttpStatusCode.OK, string? etag = null) : HttpMessageHandler { public HttpRequestMessage? Request { get; private set; } protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Request = request; var response = new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") }; if (etag is not null) response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag); return Task.FromResult(response); } }
    private sealed class ThrowingHandler : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new HttpRequestException(); }
    private sealed class TimeoutHandler : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new OperationCanceledException(); }
}
