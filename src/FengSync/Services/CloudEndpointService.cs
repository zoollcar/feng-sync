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
    private static readonly string[] PreferredS3Providers = ["AWS", "Cloudflare", "Minio"];
    private static readonly CloudEndpointMetadataStore Metadata = new();

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
        "s3" => ProviderKind.S3,
        _ => ProviderKind.Sftp
    };

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
        if (values.Bucket.Contains('/') || values.Bucket.Contains('\\')) errors["bucket"] = "Bucket 不能包含目录；请单独填写子目录。";
        if (values.Subdirectory.Split('/', '\\').Any(segment => segment == "..")) errors["subdirectory"] = "子目录不能包含“..”。";

        var endpoint = values.Endpoint.Trim();
        if (endpoint.Length > 0)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
                errors["endpoint"] = "Endpoint 必须是完整的 HTTP 或 HTTPS 地址。";
            else if (uri.AbsolutePath.Trim('/') is not "")
                errors["endpoint"] = "Endpoint 不能包含 Bucket 或目录。";
        }
        return errors;
    }

    public static async Task<IReadOnlyList<RcloneS3Provider>> LoadS3ProvidersAsync(CancellationToken ct = default)
    {
        var values = await ConfigurationApi.GetS3ProvidersAsync(ct);
        return values.OrderBy(provider => Array.IndexOf(PreferredS3Providers, provider.Name) is var index && index >= 0 ? index : int.MaxValue)
            .ThenBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>All cloud remotes stored in rclone.conf, newest UI-friendly display first.</summary>
    public static async Task<IReadOnlyList<RcloneAccount>> LoadAccountsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(BundledRclone.ConfigPath)) return [];
        var api = ConfigurationApi;
        var names = await api.ListRemoteNamesAsync(ct);
        var accounts = new List<RcloneAccount>();
        foreach (var name in names)
        {
            var type = await api.GetRemoteTypeAsync(name, ct);
            if (type is "drive" or "sftp" or "s3") accounts.Add(new(name, type));
        }
        return accounts.OrderBy(account => account.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
        await ConfigurationApi.DeleteAsync(remoteName, ct);
        await Metadata.DeleteAsync(remoteName, ct);
    }

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
            var remote = "fengsync_probe_" + Guid.NewGuid().ToString("N");
            var output = await configuration.CreateAsync(remote, "s3", S3Parameters(values),
                new RcloneConfigOptions(Obscure: true, NoOutput: true), ct);
            if (!string.IsNullOrWhiteSpace(output.Error) || !string.IsNullOrWhiteSpace(output.State))
                throw new InvalidOperationException(output.Error.Length > 0 ? output.Error : $"rclone 需要额外配置：{output.Option?.Name ?? output.State}");
            var directories = await daemon.Client.ListDirectoriesAsync(remote + ":", values.RootPath, false, ct);
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

    public static async Task SaveS3Async(string remoteName, S3EndpointValues values, bool replace, CancellationToken ct = default)
    {
        var existing = await ConfigurationApi.ListRemoteNamesAsync(ct);
        if (existing.Contains(remoteName, StringComparer.OrdinalIgnoreCase) && !replace)
            throw new InvalidOperationException($"端点名称“{remoteName}”已存在，请修改显示名称。");

        var configPath = BundledRclone.ConfigPath;
        var backup = configPath + "." + Guid.NewGuid().ToString("N") + ".bak";
        var hadConfig = File.Exists(configPath);
        if (hadConfig) File.Copy(configPath, backup, true);
        try
        {
            await CreateRemoteAsync(ProviderKind.S3, remoteName, S3Parameters(values), ct);
            await Metadata.UpsertAsync(new(remoteName, "s3", values.Provider, values.Bucket.Trim(), values.Subdirectory.Trim().Trim('/')), ct);
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

    private static Dictionary<string, string> S3Parameters(S3EndpointValues values) => new()
    {
        ["provider"] = values.Provider,
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
        {
            var parameters = fields.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value);
            var output = await ConfigurationApi.CreateAsync(
                remoteName,
                RcloneType(kind),
                parameters,
                new RcloneConfigOptions(Obscure: true, NoOutput: true),
                ct);
            if (!string.IsNullOrWhiteSpace(output.Error))
                throw new RcloneException(RcloneFailureClassifier.FromJob("config/create", output.Error));
            if (!string.IsNullOrWhiteSpace(output.State))
                throw new InvalidOperationException($"无法自动完成 {DisplayName(kind)} 配置；rclone 需要回答选项“{output.Option?.Name ?? output.State}”。");
        }
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
        var client = await App.CurrentApp.RcloneHost.GetClientAsync(ct);
        var filesystem = remoteName.EndsWith(':') ? remoteName : remoteName + ":";
        await client.ListDirectoriesAsync(filesystem, root.Trim().Trim('/'), false, ct);
    }

    private static RcloneConfigurationClient ConfigurationApi => new(App.CurrentApp.RcloneLifecycle);

    private static void OpenBrowser(Uri uri)
    {
        try { _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { throw new InvalidOperationException($"无法打开默认浏览器。授权地址：{uri}", ex); }
    }

}
