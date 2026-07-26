using System.Net;
using System.Text.Json;

namespace FengSync.Core.Updates;

public sealed class GitHubReleaseClient
{
    public static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/zoollcar/feng-sync/releases/latest");
    private readonly HttpClient _http;
    public GitHubReleaseClient(HttpClient? httpClient = null) { _http = httpClient ?? new HttpClient(); _http.Timeout = TimeSpan.FromSeconds(15); }
    public async Task<UpdateCheckResult> CheckAsync(ReleaseVersion current, string? etag = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
            request.Headers.UserAgent.ParseAdd($"FengSync/{current}"); request.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseEtag = response.Headers.ETag?.ToString();
            if (response.StatusCode == HttpStatusCode.NotModified) return new(UpdateCheckStatus.NotModified, Etag: responseEtag ?? etag);
            if (response.StatusCode == HttpStatusCode.Forbidden) return new(UpdateCheckStatus.RateLimited, Error: "GitHub API 请求受限。", Etag: responseEtag);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(UpdateCheckStatus.NotFound, Error: "未找到 GitHub Release。", Etag: responseEtag);
            if (!response.IsSuccessStatusCode) return new(UpdateCheckStatus.Failed, Error: $"GitHub 返回 {(int)response.StatusCode}。", Etag: responseEtag);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false)); var root = doc.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return new(UpdateCheckStatus.InvalidRelease, Error: "Release 不是正式版。", Etag: responseEtag);
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!ReleaseVersion.TryParse(tag, out var remote) || remote.IsPrerelease) return new(UpdateCheckStatus.InvalidRelease, Error: "Release 标签无效。", Etag: responseEtag);
            if (remote.CompareTo(current) <= 0) return new(UpdateCheckStatus.Latest, Etag: responseEtag);
            var zipName = $"FengSync-{remote}-win-x64.zip"; var shaName = zipName + ".sha256";
            JsonElement? zip = null, sha = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray()) { var name = asset.GetProperty("name").GetString(); if (name == zipName) zip = asset; else if (name == shaName) sha = asset; }
            if (zip is null || sha is null) return new(UpdateCheckStatus.InvalidRelease, Error: "Release 缺少 Windows 安装包或 SHA-256 文件。", Etag: responseEtag);
            var download = zip.Value.GetProperty("browser_download_url").GetString(); var checksum = sha.Value.GetProperty("browser_download_url").GetString();
            if (!IsGitHub(download) || !IsGitHub(checksum)) return new(UpdateCheckStatus.InvalidRelease, Error: "下载地址必须来自 github.com。", Etag: responseEtag);
            var html = root.GetProperty("html_url").GetString(); if (!Uri.TryCreate(html, UriKind.Absolute, out var htmlUri)) htmlUri = new Uri("https://github.com/zoollcar/feng-sync/releases");
            return new(UpdateCheckStatus.UpdateAvailable, new(root.GetProperty("name").GetString() ?? tag, tag, root.GetProperty("body").GetString() ?? "", htmlUri, new Uri(download!), new Uri(checksum!), zip.Value.GetProperty("size").GetInt64(), responseEtag), Etag: responseEtag);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(UpdateCheckStatus.Timeout, Error: "检查更新超时。"); }
        catch (HttpRequestException) { return new(UpdateCheckStatus.Offline, Error: "无法连接 GitHub。请检查网络。 "); }
        catch (Exception ex) { return new(UpdateCheckStatus.Failed, Error: ex.Message); }
    }
    private static bool IsGitHub(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
}
