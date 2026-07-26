namespace FengSync.Core.Updates;

public enum UpdateCheckStatus { UpdateAvailable, Latest, NotModified, InvalidRelease, RateLimited, NotFound, Timeout, Offline, Failed }
public sealed record UpdateCheckResult(UpdateCheckStatus Status, GitHubReleaseInfo? Release = null, string? Error = null, string? Etag = null);
