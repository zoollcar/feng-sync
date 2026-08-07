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

    public static async Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        await ConfigurationApi.DeleteAsync(remoteName, ct);
    }

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

    /// <summary>Confirms the remote is reachable by listing its root directories.</summary>
    public static async Task VerifyAsync(string remoteName, CancellationToken ct = default)
    {
        await ConfigurationApi.VerifyAsync(remoteName, ct);
    }

    private static RcloneConfigurationClient ConfigurationApi => new(App.CurrentApp.RcloneLifecycle);

    private static void OpenBrowser(Uri uri)
    {
        try { _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { throw new InvalidOperationException($"无法打开默认浏览器。授权地址：{uri}", ex); }
    }

}
