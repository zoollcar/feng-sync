using FengSync.Core;

namespace FengSync.Services;

/// <summary>
/// What the browser can honestly say about a file's history — which, measured against the bundled v1.75.0, is
/// nothing from the cloud. rclone exposes no version list: <c>rc/list</c> registers 101 methods and the only
/// one whose name mentions a version is <c>core/version</c>, which is rclone's own build number;
/// <c>operations/versions</c>, <c>operations/history</c> and <c>backend/versions</c> all answer 404
/// <c>couldn't find method</c>; <c>operations/fsinfo</c> reports no versioning feature; and
/// <c>operations/list</c>'s <c>opt</c> is a set of <em>listing</em> options rather than a backend-config
/// override, so the one lever that does exist cannot be pulled per request. That lever —
/// <c>s3-versions</c> / <c>b2-versions</c> / <c>imagekit-versions</c>, a <c>rclone.conf</c> setting — is
/// deliberately left alone as well: it rewrites the namespace, so old versions arrive as objects and the file
/// list fills with names that are not the objects the user has. (GDrive has no such option at all, and
/// OneDrive's and Jottacloud's <c>no_versions</c> are the opposite: they delete history on write.)
/// <para>
/// So this class does not name a "show versions" command. It names the trip, because the trip is what the
/// browser can actually do, and it supplies the sentence that says why there is no alternative.
/// </para>
/// </summary>
public static class CloudVersionHistory
{
    /// <summary>
    /// The row-menu item. It names the trip rather than the feature: a command called 版本历史 that never shows
    /// a version would be the same kind of lie as a button that can only refuse, and this one at least does
    /// exactly what it says.
    /// </summary>
    public const string MenuLabel = "在网页端查看历史版本…";

    /// <summary>Only a file has a history to look at; a folder is not an object the cloud keeps versions of.</summary>
    public static bool Offers(CloudFileEntry entry) => !entry.IsDirectory;

    /// <summary>
    /// What the browser may offer for one endpoint: the sentence explaining the limit, and the console to open
    /// when the provider has exactly one. <see cref="Offer.Url"/> is null for SFTP, WebDAV and S3 — a
    /// self-hosted SFTP server has no console, and an S3-compatible endpoint's belongs to whichever service it
    /// points at — and the browser then offers no link rather than inventing one.
    /// </summary>
    public sealed record Offer(string Provider, string Explanation, string? Url)
    {
        public bool CanOpen => Url is not null;

        public string OpenLabel => $"打开 {Provider} 网页端";
    }

    /// <summary>
    /// The offer for one endpoint, from the two strings an endpoint row carries: its rclone type and the
    /// provider name to show. Taken as strings rather than as the metadata record because a row whose metadata
    /// could not be read still has both, and an endpoint with no console is a normal answer here, not an error.
    /// </summary>
    public static Offer For(string rcloneType, string provider)
    {
        var name = string.IsNullOrWhiteSpace(provider) ? rcloneType : provider;
        var kind = EndpointSchemes.TypeForRcloneType(rcloneType);
        var url = kind is { } type ? CloudProviderCatalog.TryFor(type)?.WebConsoleUrl : null;
        return new Offer(name, Explain(name, url is not null), url);
    }

    /// <summary>
    /// The whole explanation, in the user's terms: what the application cannot do, why the one switch that
    /// exists is not used, and where the history really is.
    /// </summary>
    public static string Explain(string provider, bool hasConsole) =>
        "本应用读不到历史版本：它访问云端唯一的通道是 rclone，而 rclone 不提供版本列表。"
        + "（S3 / B2 / ImageKit 有一个「把旧版本列进目录」的开关，但那会让文件列表里混进云端并不存在的文件，所以本应用不打开它。）"
        + (hasConsole
            ? $"要查看历史版本，请到 {provider} 自己的网页端。"
            : $"这个端点没有可打开的网页端（{provider}），请到你的服务商自己的控制台查看。");

    /// <summary>The line the status bar carries afterwards — the same fact, short enough to read at a glance.</summary>
    public static string StatusFor(Offer offer) => offer.CanOpen
        ? $"已在浏览器里打开 {offer.Provider} 网页端。本应用读不到历史版本：rclone 不提供版本列表。"
        : $"本应用读不到历史版本（rclone 不提供版本列表），而 {offer.Provider} 也没有可打开的网页端。";
}
