using FengSync.Core;

namespace FengSync.Services;

/// <summary>How the endpoint editor renders one connection parameter.</summary>
public enum CloudFormFieldKind { Text, Password, Number }

/// <summary>
/// One rclone configuration parameter the endpoint editor collects for a provider.
/// </summary>
/// <param name="Key">The rclone config key, for example <c>host</c> or <c>client_id</c>.</param>
/// <param name="Label">The label above the input.</param>
/// <param name="EditLabel">
/// The label used when editing an existing endpoint, for a field whose meaning changes. A password box is
/// empty on an edit form because rclone stores the value obscured, so the label has to say what empty means.
/// </param>
/// <param name="AutomationId">
/// Required for every field: the desktop acceptance scenario drives the form by AutomationId, and a field
/// without one is invisible to it. <c>CloudProviderCatalogTests</c> fails if any field lacks one.
/// </param>
public sealed record CloudFormField(
    string Key,
    string Label,
    CloudFormFieldKind Kind = CloudFormFieldKind.Text,
    bool Required = false,
    string? AutomationId = null,
    string? DefaultValue = null,
    string? Hint = null,
    string? EditLabel = null);

/// <summary>
/// Everything the rest of the application needs to know about one cloud provider. Before this existed the
/// knowledge was spread over a dozen <c>switch</c> statements and a hard-coded XAML panel, so adding a
/// provider meant finding them all and a missed one failed silently — the endpoint was saved with the wrong
/// backend type, or was treated as a local folder.
/// </summary>
/// <param name="NamePlaceholder">Default text of the display-name box for a new endpoint.</param>
/// <param name="Fields">
/// The connection parameters, in the order they are shown. Empty for S3, whose editor is bespoke because it
/// needs a provider list, a prefix boundary and a probe that no other backend has.
/// </param>
/// <param name="RequiresOAuth">
/// True when rclone can only complete the configuration by sending the user through a browser. Such a
/// provider must not be created with <c>config/create</c> alone; the OAuth flow drives it.
/// </param>
/// <param name="SupportsTrash">
/// True when deleting an object moves it to a recycle bin the provider can restore from. Only asserted
/// where it has been verified against the bundled rclone; everything else is false, because promising an
/// undo that does not exist is worse than not offering one.
/// </param>
/// <param name="SupportsPublicLink">
/// True when <c>operations/publiclink</c> can mint a share URL for this backend. Recorded here so the
/// share action can be enabled per provider rather than per provider list in the UI layer.
/// </param>
/// <param name="ConstantParameters">
/// Parameters rclone needs but the user must not be asked about, for example Google Drive's <c>scope</c>.
/// </param>
/// <param name="CrossFieldRule">
/// A rule no single field can state, given every collected value; returns the error to show, or
/// <see langword="null"/>. SFTP's "a password or a key file" is the only one so far.
/// </param>
public sealed record CloudProviderDescriptor(
    EndpointType Kind,
    string NamePlaceholder,
    string NameAutomationId,
    string RootAutomationId,
    IReadOnlyList<CloudFormField> Fields,
    bool RequiresOAuth,
    bool SupportsTrash,
    bool SupportsPublicLink,
    string DefaultRoot = "",
    IReadOnlyDictionary<string, string>? ConstantParameters = null,
    Func<IReadOnlyDictionary<string, string>, string?>? CrossFieldRule = null)
{
    /// <summary>The rclone backend type this provider is stored under.</summary>
    public string RcloneType => EndpointSchemes.RcloneTypeFor(Kind);

    /// <summary>The URI scheme Feng Sync writes into profiles for this provider.</summary>
    public string UriScheme => EndpointSchemes.SchemeFor(Kind);

    public string DisplayName => EndpointSchemes.DisplayNameFor(Kind);

    /// <summary>
    /// True for the one provider whose root is enforced by an rclone <c>alias</c> rather than by metadata.
    /// Its editor is the bespoke one.
    /// </summary>
    public bool IsS3Compatible => Kind is EndpointType.S3;

    /// <summary>The parameters to write, with the provider's constants merged in and blanks dropped.</summary>
    public IReadOnlyDictionary<string, string> MergeParameters(IReadOnlyDictionary<string, string> typed)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in ConstantParameters ?? new Dictionary<string, string>())
            merged[pair.Key] = pair.Value;
        foreach (var pair in typed)
            if (!string.IsNullOrWhiteSpace(pair.Value)) merged[pair.Key] = pair.Value;
        return merged;
    }
}

/// <summary>
/// The one place a cloud provider is described. Adding a provider is a row here plus, if it needs one, a
/// field list — and <see cref="EndpointSchemes"/> already carries its scheme and backend type, which
/// <c>CloudProviderCatalogTests</c> asserts agree.
/// </summary>
public static class CloudProviderCatalog
{
    private static readonly CloudFormField[] OAuthClientFields =
    [
        // client_id is not a secret rclone obscures, so it is pre-filled on an edit form; client_secret is
        // a password field, so it starts empty and the label says what empty means.
        new("client_id", "Client ID（可选）", AutomationId: "OAuthClientId"),
        new("client_secret", "Client Secret（可选）", CloudFormFieldKind.Password, AutomationId: "OAuthClientSecret",
            EditLabel: "Client Secret（留空表示保持原值）")
    ];

    private static readonly CloudProviderDescriptor[] All =
    [
        new(EndpointType.GoogleDrive, "Google Drive", "GDriveRemoteName", "GDriveRemoteRoot",
            OAuthClientFields, RequiresOAuth: true, SupportsTrash: true, SupportsPublicLink: true,
            ConstantParameters: new Dictionary<string, string> { ["scope"] = "drive" }),
        new(EndpointType.OneDrive, "OneDrive", "OneDriveRemoteName", "OneDriveRemoteRoot",
            OAuthClientFields, RequiresOAuth: true, SupportsTrash: false, SupportsPublicLink: true),
        new(EndpointType.Dropbox, "Dropbox", "DropboxRemoteName", "DropboxRemoteRoot",
            OAuthClientFields, RequiresOAuth: true, SupportsTrash: false, SupportsPublicLink: true),
        new(EndpointType.Box, "Box", "BoxRemoteName", "BoxRemoteRoot",
            OAuthClientFields, RequiresOAuth: true, SupportsTrash: false, SupportsPublicLink: true),
        new(EndpointType.WebDav, "WebDAV", "WebDavRemoteName", "WebDavRemoteRoot",
        [
            new("url", "服务器地址", Required: true, AutomationId: "WebDavUrl",
                Hint: "例如 https://cloud.example.com/remote.php/dav/files/user"),
            new("vendor", "服务商（可选）", AutomationId: "WebDavVendor",
                Hint: "留空即可；个别服务商需要指定，例如 other、nextcloud、sharepoint。"),
            new("user", "用户名（可选）", AutomationId: "WebDavUser"),
            new("pass", "密码（可选）", CloudFormFieldKind.Password, AutomationId: "WebDavPassword",
                EditLabel: "密码（留空表示保持原值）")
        ], RequiresOAuth: false, SupportsTrash: false, SupportsPublicLink: false),
        new(EndpointType.Sftp, "SFTP 连接", "SftpRemoteName", "SftpRemoteRoot",
        [
            new("host", "主机", Required: true, AutomationId: "SftpRemoteHost"),
            new("port", "端口", CloudFormFieldKind.Number, Required: true, AutomationId: "SftpRemotePort", DefaultValue: "22"),
            new("user", "用户名", Required: true, AutomationId: "SftpRemoteUser"),
            new("pass", "密码（使用密钥时可留空）", CloudFormFieldKind.Password, AutomationId: "SftpRemotePassword",
                EditLabel: "密码（留空表示保持原密码不变）"),
            new("key_file", "私钥文件（可选）", AutomationId: "SftpRemoteKeyFile")
        ], RequiresOAuth: false, SupportsTrash: false, SupportsPublicLink: false,
            CrossFieldRule: static values =>
                string.IsNullOrWhiteSpace(values.GetValueOrDefault("pass")) && string.IsNullOrWhiteSpace(values.GetValueOrDefault("key_file"))
                    ? "SFTP 必须填写密码或私钥文件。"
                    : null),
        // S3's parameters are collected by the bespoke panel, so this descriptor carries no fields.
        new(EndpointType.S3, "S3 Bucket", "S3DisplayName", "S3RemoteRoot",
            [], RequiresOAuth: false, SupportsTrash: false, SupportsPublicLink: true)
    ];

    /// <summary>Every provider the endpoint editor offers, in the order it lists them.</summary>
    public static IReadOnlyList<CloudProviderDescriptor> Providers => All;

    /// <summary>The descriptor for a provider, or <see langword="null"/> when the type is not a provider.</summary>
    public static CloudProviderDescriptor? TryFor(EndpointType kind) =>
        All.FirstOrDefault(descriptor => descriptor.Kind == kind);

    /// <summary>The descriptor for a provider; throws for a type that is not one (including Local).</summary>
    public static CloudProviderDescriptor For(EndpointType kind) => TryFor(kind)
        ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "不是云端 Provider 类型。");

    /// <summary>True when this endpoint type is one of the providers the editor can create.</summary>
    public static bool IsProvider(EndpointType kind) => TryFor(kind) is not null;

    public static bool RequiresOAuth(EndpointType kind) => TryFor(kind)?.RequiresOAuth ?? false;

    /// <summary>
    /// Whether deleting through this provider can be undone. Unknown types answer false: the delete
    /// confirmation promises a recycle bin only when one exists.
    /// </summary>
    public static bool SupportsTrash(EndpointType kind) => TryFor(kind)?.SupportsTrash ?? false;

    public static bool SupportsPublicLink(EndpointType kind) => TryFor(kind)?.SupportsPublicLink ?? false;
}
