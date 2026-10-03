namespace FengSync.Core;

/// <summary>
/// The single map between the three names a cloud endpoint goes by: the URI scheme Feng Sync writes into
/// profiles (<c>gdrive://</c>), the rclone backend type it is stored under (<c>drive</c>), and the
/// <see cref="EndpointType"/> the rest of the code switches on.
/// </summary>
/// <remarks>
/// This exists because a scheme or backend type that is not recognised is <em>silently</em> mishandled
/// rather than rejected: <see cref="IsRemote"/> would call a cloud URI a local path, and
/// <see cref="RcloneConfig.ParseDump"/> would drop the remote from the list. Adding a provider therefore
/// has to be one row here, not a new <c>StartsWith</c> in each caller — a forgotten caller is a
/// data-loss bug, not a compile error. <c>CloudProviderCatalogTests</c> asserts every row is covered.
/// </remarks>
public static class EndpointSchemes
{
    private static readonly (string Scheme, string RcloneType, EndpointType Type)[] Map =
    [
        ("gdrive", "drive", EndpointType.GoogleDrive),
        ("onedrive", "onedrive", EndpointType.OneDrive),
        ("dropbox", "dropbox", EndpointType.Dropbox),
        ("box", "box", EndpointType.Box),
        ("webdav", "webdav", EndpointType.WebDav),
        ("sftp", "sftp", EndpointType.Sftp),
        ("s3", "s3", EndpointType.S3)
    ];

    /// <summary>
    /// An rclone <c>alias</c> is listed as S3 because the only alias Feng Sync creates is the prefix
    /// boundary of an S3 endpoint; see <c>CloudEndpointService.SelectEndpointRemotes</c>.
    /// </summary>
    private const EndpointType AliasType = EndpointType.S3;

    /// <summary>Every scheme a cloud endpoint URI can start with, in catalogue order.</summary>
    public static IReadOnlyList<string> AllSchemes { get; } = Map.Select(entry => entry.Scheme).ToArray();

    /// <summary>Every <see cref="EndpointType"/> that addresses a cloud service, in catalogue order.</summary>
    public static IReadOnlyList<EndpointType> CloudTypes { get; } = Map.Select(entry => entry.Type).Distinct().ToArray();

    /// <summary>The URI scheme for an endpoint type, without the <c>://</c> separator.</summary>
    public static string SchemeFor(EndpointType type) => Lookup(type).Scheme;

    /// <summary>The rclone backend type an endpoint of this flavour is stored under.</summary>
    public static string RcloneTypeFor(EndpointType type) => Lookup(type).RcloneType;

    /// <summary>The label the endpoint list shows for this flavour.</summary>
    public static string DisplayNameFor(EndpointType type) => type switch
    {
        EndpointType.GoogleDrive => "Google Drive",
        EndpointType.OneDrive => "OneDrive",
        EndpointType.Dropbox => "Dropbox",
        EndpointType.Box => "Box",
        EndpointType.WebDav => "WebDAV",
        EndpointType.Sftp => "SFTP",
        EndpointType.S3 => "S3 Bucket",
        _ => "本地文件夹"
    };

    /// <summary>True when the value is one of Feng Sync's cloud URIs rather than a filesystem path.</summary>
    public static bool IsRemote(string value) => SchemeOf(value) is not null;

    /// <summary>
    /// The scheme of a cloud URI, lower-cased, or <see langword="null"/> when the value is a local path.
    /// Only the scheme is checked, not the remote name: <c>"sftp://"</c> is still a cloud URI with a
    /// missing remote, and the callers that need a name report that as their own clear error rather than
    /// letting it fall through to the local-filesystem path.
    /// </summary>
    public static string? SchemeOf(string? value)
    {
        var text = value ?? "";
        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0) return null;
        var scheme = text[..separator].ToLowerInvariant();
        return Map.Any(entry => entry.Scheme == scheme) ? scheme : null;
    }

    /// <summary>The endpoint type a URI scheme denotes, or <see langword="null"/> for an unknown scheme.</summary>
    public static EndpointType? TypeForScheme(string? scheme)
    {
        var value = (scheme ?? "").Trim().ToLowerInvariant();
        foreach (var entry in Map)
            if (entry.Scheme == value) return entry.Type;
        return null;
    }

    /// <summary>
    /// The endpoint type an rclone backend type denotes, or <see langword="null"/> for a backend Feng Sync
    /// does not surface. <c>alias</c> is mapped because an S3 endpoint with an enforced prefix is one — but
    /// see <see cref="IsSelfDescribingRcloneType"/>: an alias is only an endpoint when Feng Sync's own
    /// metadata says it made it.
    /// </summary>
    public static EndpointType? TypeForRcloneType(string? rcloneType)
    {
        var value = (rcloneType ?? "").Trim().ToLowerInvariant();
        if (value == "alias") return AliasType;
        foreach (var entry in Map)
            if (entry.RcloneType == value) return entry.Type;
        return null;
    }

    /// <summary>
    /// True when a remote of this backend type describes itself well enough to be listed on its own.
    /// <c>alias</c> deliberately is not: an alias is just a pointer, so a hand-made one could aim at any
    /// backend or any path, and Feng Sync cannot know what flavour of endpoint it represents. Only an alias
    /// that Feng Sync's metadata records as a prefix boundary is listed, which the caller has to check.
    /// </summary>
    public static bool IsSelfDescribingRcloneType(string? rcloneType) =>
        (rcloneType ?? "").Trim().ToLowerInvariant() is not ("alias" or "") && TypeForRcloneType(rcloneType) is not null;

    /// <summary>Feng Sync's stable endpoint URI: <c>&lt;scheme&gt;://&lt;remote&gt;/&lt;root&gt;</c>.</summary>
    public static string BuildUri(EndpointType type, string remoteName, string? root)
    {
        var trimmed = (root ?? "").Trim().Trim('/');
        return SchemeFor(type) + "://" + remoteName + (trimmed.Length == 0 ? "" : "/" + trimmed);
    }

    private static (string Scheme, string RcloneType, EndpointType Type) Lookup(EndpointType type)
    {
        foreach (var entry in Map)
            if (entry.Type == type) return entry;
        throw new ArgumentOutOfRangeException(nameof(type), type, "不是云端端点类型。");
    }
}
