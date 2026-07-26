namespace FengSync.Core.Updates;

public sealed record GitHubReleaseInfo(string Name, string Tag, string Body, Uri HtmlUrl, Uri DownloadUrl, Uri Sha256Url, long DownloadSize, string? Etag);
