using System.Diagnostics;
using System.IO;
using FengSync.Core;
using FengSync.Core.Rclone.Configuration;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Services;

/// <summary>
/// Strongly typed wrapper over rclone's RC JSON API used by the cloud-endpoint management UI.
/// Credentials are sent only in authenticated loopback request bodies; they never enter a Feng Sync
/// profile, process command line, diagnostic message, or log.
/// </summary>
public static class CloudEndpointService
{
    private static readonly string[] PreferredS3Providers = ["AWS", "Backblaze", "Cloudflare", "Minio"];
    /// <summary>
    /// Resolved per use rather than cached: <see cref="AppDataPaths.Root"/> honours FENGSYNC_DATA_DIR on
    /// every read, and a store built once at type-initialization would keep writing to the real application
    /// data even after a test redirected it. Serialization lives in the store's own static gate, so a fresh
    /// instance per call still cannot interleave writes.
    /// </summary>
    private static CloudEndpointMetadataStore Metadata => new();

    /// <summary>
    /// A provider Feng Sync surfaces itself because rclone only knows it under a generic value.
    /// </summary>
    /// <param name="Name">Name shown in the provider list.</param>
    /// <param name="RcloneProvider">Value written to rclone's <c>provider</c> option.</param>
    /// <param name="RequiresEndpoint">Whether a blank endpoint would silently target the wrong service.</param>
    public sealed record S3ProviderPreset(
        string Name,
        string RcloneProvider,
        string Description,
        IReadOnlyList<string> RegionSuggestions,
        bool RequiresEndpoint);

    /// <summary>
    /// rclone's s3 backend has no dedicated Backblaze entry (B2 also has its own <c>b2</c> backend);
    /// its S3-compatible API is the documented "Other" case plus a
    /// <c>https://s3.&lt;region&gt;.backblazeb2.com</c> endpoint. The endpoint is mandatory here because
    /// rclone would otherwise fall back to the Amazon S3 host.
    /// </summary>
    private static readonly S3ProviderPreset[] S3Presets =
    [
        new("Backblaze", "Other",
            "Backblaze B2 的 S3 兼容 API（仅接受 HTTPS）。Endpoint 形如 https://s3.<region>.backblazeb2.com，"
            + "Region 必须与 Endpoint 中的区域一致；两者都能在 B2 控制台的 Bucket 详情页看到。",
            [], RequiresEndpoint: true)
    ];

    /// <summary>Feng Sync's own preset for a provider name, matched case-insensitively; otherwise <c>null</c>.</summary>
    public static S3ProviderPreset? FindPreset(string? providerName) =>
        S3Presets.FirstOrDefault(preset => preset.Name.Equals((providerName ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Supported rclone backends surfaced by the management UI.</summary>
    public enum ProviderKind { GoogleDrive, Sftp, S3 }

    public static string RcloneType(ProviderKind kind) => kind switch
    {
        ProviderKind.GoogleDrive => "drive",
        ProviderKind.Sftp => "sftp",
        ProviderKind.S3 => "s3",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string DisplayName(ProviderKind kind) => kind switch
    {
        ProviderKind.GoogleDrive => "Google Drive",
        ProviderKind.Sftp => "SFTP",
        ProviderKind.S3 => "S3 Bucket",
        _ => "未知"
    };

    /// <summary>Feng Sync's stable endpoint URI form understood by <see cref="EndpointFactory"/>.</summary>
    public static string BuildUri(ProviderKind kind, string remoteName, string? root)
    {
        var scheme = kind switch
        {
            ProviderKind.GoogleDrive => "gdrive://",
            ProviderKind.Sftp => "sftp://",
            ProviderKind.S3 => "s3://",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var trimmed = (root ?? "").Trim().Trim('/');
        return scheme + remoteName + (string.IsNullOrEmpty(trimmed) ? "" : "/" + trimmed);
    }

    public static ProviderKind KindFromRcloneType(string type) => type.ToLowerInvariant() switch
    {
        "drive" => ProviderKind.GoogleDrive,
        // An alias only ever appears here for an S3 endpoint whose prefix is enforced: LoadAccountsAsync
        // surfaces no other alias, because a hand-made one could point anywhere.
        "s3" or "alias" => ProviderKind.S3,
        _ => ProviderKind.Sftp
    };

    /// <summary>
    /// Name of the hidden <c>s3</c> remote behind an enforced-prefix endpoint. Deriving it from the
    /// user-facing name keeps saving idempotent, so replacing an endpoint never accumulates remotes.
    /// </summary>
    internal static string BackingRemoteName(string aliasName) => aliasName + "_s3";

    /// <summary>
    /// The <c>alias</c> target for an S3 endpoint: <c>&lt;backing&gt;:&lt;bucket&gt;/&lt;prefix&gt;</c>.
    /// rclone resolves nothing above this path — a <c>..</c> or absolute path is refused rather than clamped
    /// or followed — which is what makes the prefix a boundary instead of a UI convention.
    /// </summary>
    internal static string AliasTarget(string backingRemoteName, string prefix) =>
        backingRemoteName + ":" + (prefix ?? "").Trim().Trim('/');

    /// <summary>rclone parameters for the <c>alias</c> that enforces an S3 endpoint's prefix.</summary>
    internal static Dictionary<string, string> AliasParameters(string backingRemoteName, string prefix) =>
        new() { ["remote"] = AliasTarget(backingRemoteName, prefix) };

    /// <summary>Turns a human display name into a safe rclone remote id; falls back to a random id.</summary>
    public static string SanitizeRemoteName(string? display)
    {
        var cleaned = new string((display ?? "").Trim().Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(cleaned) ? "fengsync_" + Guid.NewGuid().ToString("N")[..8] : cleaned;
    }

    public static IReadOnlyDictionary<string, string> ValidateS3Settings(S3EndpointValues values, IReadOnlyCollection<string> providers)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(values.DisplayName)) errors["displayName"] = "请输入显示名称。";
        if (!providers.Contains(values.Provider, StringComparer.Ordinal)) errors["provider"] = "请选择 rclone 支持的 S3 Provider。";
        if (string.IsNullOrWhiteSpace(values.AccessKey)) errors["accessKey"] = "请输入 Access Key ID。";
        if (string.IsNullOrWhiteSpace(values.Secret)) errors["secret"] = "请输入 Secret Access Key。";
        if (string.IsNullOrWhiteSpace(values.Bucket)) errors["bucket"] = "请输入 Bucket。";
        if (values.Bucket.Contains('/') || values.Bucket.Contains('\\')) errors["bucket"] = "Bucket 不能包含目录；请单独填写前缀。";
        if (values.Subdirectory.Split('/', '\\').Any(segment => segment == "..")) errors["subdirectory"] = "前缀不能包含“..”。";
        if (values.Subdirectory.Contains('\\')) errors["subdirectory"] = "前缀请使用“/”分隔，不要使用“\\”。";

        var endpoint = values.Endpoint.Trim();
        if (endpoint.Length > 0)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
                errors["endpoint"] = "Endpoint 必须是完整的 HTTP 或 HTTPS 地址。";
            else if (uri.AbsolutePath.Trim('/') is not "")
                errors["endpoint"] = "Endpoint 不能包含 Bucket 或目录。";
        }
        else if (FindPreset(values.Provider) is { RequiresEndpoint: true })
        {
            // Without an endpoint rclone would talk to the Amazon S3 host instead of the chosen service.
            errors["endpoint"] = $"{values.Provider} 必须填写 Endpoint，形如 https://s3.<region>.backblazeb2.com。";
        }
        return errors;
    }

    public static async Task<IReadOnlyList<RcloneS3Provider>> LoadS3ProvidersAsync(CancellationToken ct = default) =>
        MergeS3Providers(await ConfigurationApi.GetS3ProvidersAsync(ct));

    /// <summary>
    /// Combines the providers rclone reports with Feng Sync's own presets. A name rclone reports wins,
    /// so a future rclone release that ships a first-class Backblaze provider takes over automatically.
    /// An empty report means rclone could not describe its s3 backend at all; presets are withheld then
    /// so the UI keeps signalling "no providers available" instead of offering a single misleading entry.
    /// </summary>
    public static IReadOnlyList<RcloneS3Provider> MergeS3Providers(IReadOnlyList<RcloneS3Provider> reported)
    {
        var known = reported.Select(provider => provider.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = reported.Count == 0
            ? reported
            : reported.Concat(S3Presets
                .Where(preset => !known.Contains(preset.Name))
                .Select(preset => new RcloneS3Provider(preset.Name, preset.Description, preset.RegionSuggestions, preset.RcloneProvider)));
        return merged
            .OrderBy(provider => Array.IndexOf(PreferredS3Providers, provider.Name) is var index && index >= 0 ? index : int.MaxValue)
            .ThenBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// All cloud remotes stored in rclone.conf, newest UI-friendly display first.
    /// </summary>
    public static async Task<IReadOnlyList<RcloneAccount>> LoadAccountsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(BundledRclone.ConfigPath)) return [];
        var api = ConfigurationApi;
        var names = await api.ListRemoteNamesAsync(ct);
        var types = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) types[name] = await api.GetRemoteTypeAsync(name, ct);
        return SelectEndpointRemotes(types, await Metadata.LoadAsync(ct));
    }

    /// <summary>
    /// Decides which rclone remotes are endpoints the UI should show. Split out from the RC calls so the
    /// rules stay testable, because they are not obvious: an enforced-prefix endpoint is two remotes and
    /// only the alias is an endpoint, a hand-made alias is not an endpoint at all (it could point
    /// anywhere, so Feng Sync cannot know its flavour), and a backing store whose alias has been removed
    /// becomes visible again — otherwise it would be stranded in rclone.conf with no way to delete it.
    /// </summary>
    internal static IReadOnlyList<RcloneAccount> SelectEndpointRemotes(
        IReadOnlyDictionary<string, string?> types,
        IReadOnlyDictionary<string, CloudEndpointMetadata> metadata)
    {
        var hidden = metadata.Values
            .Where(value => value.AliasOf is { Length: > 0 } && types.ContainsKey(value.RemoteName))
            .Select(value => value.AliasOf!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = metadata.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return types
            .Where(pair => !hidden.Contains(pair.Key))
            .Where(pair => pair.Value is "drive" or "sftp" or "s3" || (pair.Value is "alias" && known.Contains(pair.Key)))
            .Select(pair => new RcloneAccount(pair.Key, pair.Value!))
            .OrderBy(account => account.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<IReadOnlyList<CloudEndpointAccount>> LoadEndpointAccountsAsync(CancellationToken ct = default)
    {
        var accounts = await LoadAccountsAsync(ct);
        var metadata = await Metadata.LoadAsync(ct);
        return accounts.Select(account => new CloudEndpointAccount(account,
            metadata.TryGetValue(account.Name, out var value) ? value : null)).ToList();
    }

    public static async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        var metadata = await Metadata.LoadAsync(ct);
        await ConfigurationApi.DeleteAsync(remoteName, ct);
        // An enforced-prefix endpoint owns a hidden backing store. Deleting only the alias would strand a
        // credential-bearing remote in rclone.conf that the endpoint list no longer shows.
        if (metadata.TryGetValue(remoteName, out var value) && value.AliasOf is { Length: > 0 } backing)
            await ConfigurationApi.DeleteAsync(backing, ct);
        await Metadata.DeleteAsync(remoteName, ct);
    }

    /// <summary>
    /// Verifies an S3 endpoint in a throwaway config, using exactly the shape saving produces: a real
    /// <c>s3</c> remote plus the <c>alias</c> that confines access to the prefix. Listing through the alias
    /// proves both halves at once — that the credentials work and that the prefix the user typed exists.
    /// </summary>
    public static async Task<EndpointProbeResult> ProbeS3Async(S3EndpointValues values, CancellationToken ct = default)
    {
        var directory = Path.Combine(AppDataPaths.Root, "probes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "rclone.conf");
        try
        {
            await using var daemon = await RcloneDaemon.StartAsync(BundledRclone.ExecutablePath, configPath, ct,
                new TraceRcloneLogSink());
            var configuration = new RcloneConfigurationClient(daemon.Client);
            var backing = "fengsync_probe_" + Guid.NewGuid().ToString("N");
            var alias = backing + "_alias";
            await CreateStaticRemoteAsync(configuration, backing, "s3", DisplayName(ProviderKind.S3), S3Parameters(values), ct);
            await CreateStaticRemoteAsync(configuration, alias, "alias", "别名", AliasParameters(backing, values.Prefix), ct);
            // The path is empty on purpose: the alias root is already the prefix. rclone refuses anything
            // above it, so this listing cannot reach a sibling key.
            var directories = await daemon.Client.ListDirectoriesAsync(alias + ":", "", false, ct);
            return new(directories);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            if (Directory.Exists(directory)) throw new IOException($"S3 连接测试临时目录未能清理：{directory}");
            var parent = Path.GetDirectoryName(directory)!;
            try { if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent); } catch { }
        }
    }

    /// <summary>
    /// Saves an S3 endpoint as a pair of remotes: a hidden <c>s3</c> remote holding the credentials, and an
    /// <c>alias</c> that points at <c>&lt;bucket&gt;/&lt;prefix&gt;</c> inside it. The alias keeps the name
    /// the user chose, so profiles, the endpoint list and the mount picker are unchanged, while the prefix
    /// becomes a boundary rclone enforces rather than one this application merely respects.
    /// </summary>
    public static async Task SaveS3Async(string remoteName, S3EndpointValues values, bool replace, CancellationToken ct = default)
    {
        var existing = await ConfigurationApi.ListRemoteNamesAsync(ct);
        if (existing.Contains(remoteName, StringComparer.OrdinalIgnoreCase) && !replace)
            throw new InvalidOperationException($"端点名称“{remoteName}”已存在，请修改显示名称。");

        var backing = BackingRemoteName(remoteName);
        if (existing.Contains(backing, StringComparer.OrdinalIgnoreCase) && !replace)
            throw new InvalidOperationException($"内部名称“{backing}”已被占用，请修改显示名称。");

        var prefix = CloudEndpointMetadata.NormalizeRoot(values.Bucket, values.Subdirectory);
        var configPath = BundledRclone.ConfigPath;
        // This path writes rclone.conf directly instead of going through CreateRemoteAsync, so it has to
        // create the folder itself: on a fresh install there is no rclone folder yet.
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var backup = configPath + "." + Guid.NewGuid().ToString("N") + ".bak";
        var hadConfig = File.Exists(configPath);
        if (hadConfig) File.Copy(configPath, backup, true);
        try
        {
            // Remove both first so a replace cannot leave the previous remote's keys — including a stale
            // secret — behind in the new section.
            if (existing.Contains(remoteName, StringComparer.OrdinalIgnoreCase)) await ConfigurationApi.DeleteAsync(remoteName, ct);
            if (existing.Contains(backing, StringComparer.OrdinalIgnoreCase)) await ConfigurationApi.DeleteAsync(backing, ct);

            await CreateStaticRemoteAsync(ConfigurationApi, backing, "s3", DisplayName(ProviderKind.S3), S3Parameters(values), ct);
            await CreateStaticRemoteAsync(ConfigurationApi, remoteName, "alias", "别名", AliasParameters(backing, prefix), ct);
            await Metadata.UpsertAsync(new(remoteName, "alias", values.Provider, values.Bucket.Trim(), values.Subdirectory.Trim().Trim('/'), backing), ct);
        }
        catch
        {
            if (hadConfig) File.Copy(backup, configPath, true);
            else if (File.Exists(configPath)) File.Delete(configPath);
            throw;
        }
        finally { if (File.Exists(backup)) File.Delete(backup); }
    }

    public static Task SaveMetadataAsync(string remoteName, ProviderKind kind, string rootPath, CancellationToken ct = default) =>
        Metadata.UpsertAsync(new(remoteName, RcloneType(kind), DisplayName(kind), rootPath.Trim().Trim('/'), ""), ct);

    /// <summary>rclone configuration parameters for an S3 endpoint. Visible to tests via InternalsVisibleTo.</summary>
    internal static Dictionary<string, string> S3Parameters(S3EndpointValues values) => new()
    {
        // The displayed provider name and the value rclone understands can differ (see S3ProviderPreset).
        ["provider"] = string.IsNullOrWhiteSpace(values.RcloneProvider) ? values.Provider : values.RcloneProvider,
        ["access_key_id"] = values.AccessKey.Trim(),
        ["secret_access_key"] = values.Secret,
        ["region"] = values.Region.Trim(),
        ["endpoint"] = values.Endpoint.Trim()
    };

    public static async Task ReconnectAsync(string remoteName, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await RcloneEnvironment.WarmOAuthDnsAsync(ct);
        var api = ConfigurationApi;
        await new RcloneOAuthFlow(api, OpenBrowser).ReconnectGoogleDriveAsync(remoteName, progress, ct);
        await api.VerifyAsync(remoteName, ct);
    }

    /// <summary>
    /// Creates (or overwrites) an rclone remote. Google Drive completes OAuth in the default browser;
    /// the other backends are non-interactive. Failures remain structured RC errors.
    /// </summary>
    public static Task CreateRemoteAsync(ProviderKind kind, string remoteName, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
        => CreateRemoteAsync(kind, remoteName, fields, progress: null, ct);

    public static async Task CreateRemoteAsync(
        ProviderKind kind,
        string remoteName,
        IReadOnlyDictionary<string, string> fields,
        IProgress<string>? progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BundledRclone.ConfigPath)!);
        if (kind == ProviderKind.GoogleDrive)
        {
            await RcloneEnvironment.WarmOAuthDnsAsync(ct);
            var api = ConfigurationApi;
            await new RcloneOAuthFlow(api, OpenBrowser).CreateGoogleDriveAsync(remoteName, fields, progress, ct);
            return;
        }
        await CreateStaticRemoteAsync(ConfigurationApi, remoteName, RcloneType(kind), DisplayName(kind), fields, ct);
    }

    /// <summary>
    /// Creates a remote whose configuration is fully described by <paramref name="fields"/>, so it needs no
    /// interactive step. Google Drive never reaches this path: it completes OAuth in the browser.
    /// </summary>
    private static async Task CreateStaticRemoteAsync(
        RcloneConfigurationClient client,
        string remoteName,
        string rcloneType,
        string label,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken ct)
    {
        var parameters = fields.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var output = await client.CreateAsync(
            remoteName,
            rcloneType,
            parameters,
            new RcloneConfigOptions(Obscure: true, NoOutput: true),
            ct);
        if (!string.IsNullOrWhiteSpace(output.Error))
            throw new RcloneException(RcloneFailureClassifier.FromJob("config/create", output.Error));
        if (!string.IsNullOrWhiteSpace(output.State))
            throw new InvalidOperationException($"无法自动完成 {label} 配置；rclone 需要回答选项“{output.Option?.Name ?? output.State}”。");
    }

    /// <summary>Confirms the remote and the user-selected root are reachable.</summary>
    public static async Task VerifyAsync(string remoteName, string? root = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            await ConfigurationApi.VerifyAsync(remoteName, ct);
            return;
        }

        // Verify the selected path directly. Bucket-scoped S3 credentials commonly cannot
        // ListBuckets, even though they have full access to their configured bucket.
        var client = await AppServices.Current.RcloneHost.GetClientAsync(ct);
        var filesystem = remoteName.EndsWith(':') ? remoteName : remoteName + ":";
        await client.ListDirectoriesAsync(filesystem, root.Trim().Trim('/'), false, ct);
    }

    private static RcloneConfigurationClient ConfigurationApi => new(AppServices.Current.RcloneLifecycle);

    private static void OpenBrowser(Uri uri)
    {
        try { _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { throw new InvalidOperationException($"无法打开默认浏览器。授权地址：{uri}", ex); }
    }

}
