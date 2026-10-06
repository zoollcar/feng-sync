using FengSync.Core;
using FengSync.Localization;

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
/// <param name="WebConsoleUrl">
/// The provider's own web console, when it has exactly one. This exists for the one thing rclone cannot do:
/// it exposes no version list (see <see cref="CloudVersionHistory"/>), so the honest answer to "show me this
/// file's history" is to send the user where the history actually is. Deliberately <see langword="null"/> for
/// SFTP, WebDAV and S3 — a self-hosted SFTP server has no console at all, and an S3-compatible endpoint's
/// belongs to whichever service it points at, which this descriptor does not model. A null here means the
/// browser offers no link rather than inventing one.
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
    Func<IReadOnlyDictionary<string, string>, string?>? CrossFieldRule = null,
    string? WebConsoleUrl = null)
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
    private static CloudFormField[] OAuthClientFields =>
    [
        // client_id is not a secret rclone obscures, so it is pre-filled on an edit form; client_secret is
        // a password field, so it starts empty and the label says what empty means.
        new("client_id", Tr.F("Provider.OAuthClientId"), AutomationId: "OAuthClientId"),
        new("client_secret", Tr.F("Provider.OAuthClientSecret"), CloudFormFieldKind.Password, AutomationId: "OAuthClientSecret",
            EditLabel: Tr.F("Provider.OAuthClientSecretEdit"))
    ];

    /// <summary>
    /// The catalog, rebuilt on every read rather than cached in a static field: the labels are display text, and
    /// a cached array would freeze the wording of whichever language happened to be chosen first.
    /// </summary>
    private static IReadOnlyList<CloudProviderDescriptor> All
    {
        get
        {
        return
        [
        new(EndpointType.GoogleDrive, "Google Drive", "GDriveRemoteName", "GDriveRemoteRoot",
            OAuthClientFields, RequiresOAuth: true, SupportsTrash: true, SupportsPublicLink: true,
            ConstantParameters: new Dictionary<string, string> { ["scope"] = "drive" },
            WebConsoleUrl: "https://drive.google.com/"),
        new(EndpointType.OneDrive, "OneDrive", "OneDriveRemoteName", "OneDriveRemoteRoot",
            OAuthClientFields, RequiresOAuth: true, SupportsTrash: false, SupportsPublicLink: true,
            WebConsoleUrl: "https://onedrive.live.com/"),
        new(EndpointType.Dropbox, "Dropbox", "DropboxRemoteName", "DropboxRemoteRoot",
            OAuthClientFields, RequiresOAuth: true, SupportsTrash: false, SupportsPublicLink: true,
            WebConsoleUrl: "https://www.dropbox.com/home"),
        new(EndpointType.Box, "Box", "BoxRemoteName", "BoxRemoteRoot",
            OAuthClientFields, RequiresOAuth: true, SupportsTrash: false, SupportsPublicLink: true,
            WebConsoleUrl: "https://app.box.com/"),
        new(EndpointType.WebDav, "WebDAV", "WebDavRemoteName", "WebDavRemoteRoot",
        [
            new("url", Tr.F("Provider.WebDav.Url"), Required: true, AutomationId: "WebDavUrl",
                Hint: Tr.F("Provider.WebDav.UrlHint")),
            new("vendor", Tr.F("Provider.WebDav.Vendor"), AutomationId: "WebDavVendor",
                Hint: Tr.F("Provider.WebDav.VendorHint")),
            new("user", Tr.F("Provider.WebDav.User"), AutomationId: "WebDavUser"),
            new("pass", Tr.F("Provider.WebDav.Password"), CloudFormFieldKind.Password, AutomationId: "WebDavPassword",
                EditLabel: Tr.F("Provider.WebDav.PasswordEdit"))
        ], RequiresOAuth: false, SupportsTrash: false, SupportsPublicLink: false),
        new(EndpointType.Sftp, Tr.F("Provider.Sftp.NamePlaceholder"), "SftpRemoteName", "SftpRemoteRoot",
        [
            new("host", Tr.F("Provider.Sftp.Host"), Required: true, AutomationId: "SftpRemoteHost"),
            new("port", Tr.F("Provider.Sftp.Port"), CloudFormFieldKind.Number, Required: true, AutomationId: "SftpRemotePort", DefaultValue: "22"),
            new("user", Tr.F("Provider.Sftp.User"), Required: true, AutomationId: "SftpRemoteUser"),
            new("pass", Tr.F("Provider.Sftp.Password"), CloudFormFieldKind.Password, AutomationId: "SftpRemotePassword",
                EditLabel: Tr.F("Provider.Sftp.PasswordEdit")),
            new("key_file", Tr.F("Provider.Sftp.KeyFile"), AutomationId: "SftpRemoteKeyFile")
        ], RequiresOAuth: false, SupportsTrash: false, SupportsPublicLink: false,
            CrossFieldRule: static values =>
                string.IsNullOrWhiteSpace(values.GetValueOrDefault("pass")) && string.IsNullOrWhiteSpace(values.GetValueOrDefault("key_file"))
                    ? Tr.F("Provider.Sftp.PasswordOrKeyRequired")
                    : null),
        // S3's parameters are collected by the bespoke panel, so this descriptor carries no fields.
        new(EndpointType.S3, "S3 Bucket", "S3DisplayName", "S3RemoteRoot",
            [], RequiresOAuth: false, SupportsTrash: false, SupportsPublicLink: true)
        ];
        }
    }

    /// <summary>Every provider the endpoint editor offers, in the order it lists them.</summary>
    public static IReadOnlyList<CloudProviderDescriptor> Providers => All;

    /// <summary>The descriptor for a provider, or <see langword="null"/> when the type is not a provider.</summary>
    public static CloudProviderDescriptor? TryFor(EndpointType kind) =>
        All.FirstOrDefault(descriptor => descriptor.Kind == kind);

    /// <summary>The descriptor for a provider; throws for a type that is not one (including Local).</summary>
    public static CloudProviderDescriptor For(EndpointType kind) => TryFor(kind)
        ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, Tr.F("Provider.NotAProviderType"));

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
