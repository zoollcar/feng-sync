using System.Diagnostics;
using System.IO;
using FengSync.Core;

namespace FengSync.Services;

/// <summary>
/// Thin wrapper over the bundled rclone CLI/RC daemon used by the cloud-endpoint management UI.
/// Credentials are only ever passed to rclone as process arguments while a remote is created; they
/// live afterwards in rclone.conf and never enter a Feng Sync profile, log or command line.
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
    public static async Task<IReadOnlyList<RcloneAccount>> LoadAccountsAsync()
    {
        if (!File.Exists(BundledRclone.ConfigPath)) return [];
        var json = await RunAsync("config", "dump", "--config", BundledRclone.ConfigPath);
        return RcloneConfig.ParseDump(json);
    }

    public static Task DeleteAsync(string remoteName) => RunAsync("config", "delete", remoteName, "--config", BundledRclone.ConfigPath);

    public static Task ReconnectAsync(string remoteName) => RunAsync("config", "reconnect", remoteName + ":", "--config", BundledRclone.ConfigPath);

    /// <summary>
    /// Creates (or overwrites) an rclone remote. Google Drive completes OAuth in the default browser;
    /// the other backends are non-interactive. Throws with rclone's stderr on failure.
    /// </summary>
    public static async Task CreateRemoteAsync(ProviderKind kind, string remoteName, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BundledRclone.ConfigPath)!);
        var args = new List<string> { "config", "create", remoteName, RcloneType(kind), "--config", BundledRclone.ConfigPath };
        foreach (var (key, value) in fields)
            if (!string.IsNullOrWhiteSpace(value)) { args.Add(key); args.Add(value); }
        await RunAsync(ct, args.ToArray());
    }

    /// <summary>Confirms the remote is reachable by listing its root directories.</summary>
    public static Task VerifyAsync(string remoteName, CancellationToken ct = default)
        => RunAsync(ct, "lsd", remoteName + ":", "--config", BundledRclone.ConfigPath);

    public static Task<string> RunAsync(params string[] arguments) => RunAsync(CancellationToken.None, arguments);

    private static async Task<string> RunAsync(CancellationToken ct, params string[] arguments)
    {
        var start = new ProcessStartInfo(BundledRclone.ExecutablePath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动内置 rclone。");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "rclone 命令失败。" : error.Trim());
        return output;
    }
}
