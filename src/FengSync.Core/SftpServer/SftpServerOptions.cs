using System.Net;

namespace FengSync.Core.SftpServer;

/// <summary>Non-sensitive server settings. Protocol hosting is deliberately supplied by a vetted server implementation.</summary>
public sealed record SftpServerOptions(
    bool Enabled = false,
    bool StartWithApplication = false,
    string ListenAddress = "127.0.0.1",
    int Port = 2222,
    int MaxConnections = 8,
    TimeSpan? IdleTimeout = null,
    string? NodeExecutablePath = null,
    string? NodeModulePath = null,
    string? HostKeyPath = null,
    IReadOnlyList<SftpAccount>? Accounts = null,
    IReadOnlyList<SftpShare>? Shares = null,
    long MaxUploadBytes = 1_073_741_824,
    int MaxAuthenticationFailures = 5,
    TimeSpan? AuthenticationBlockDuration = null)
{
    public TimeSpan EffectiveIdleTimeout => IdleTimeout ?? TimeSpan.FromMinutes(10);
    public TimeSpan EffectiveAuthenticationBlockDuration => AuthenticationBlockDuration ?? TimeSpan.FromMinutes(5);
    public void Validate()
    {
        if (!IPAddress.TryParse(ListenAddress, out _)) throw new InvalidOperationException("SFTP 监听地址必须是明确的 IP 地址。");
        if (Port is < 1 or > 65535) throw new InvalidOperationException("SFTP 端口必须介于 1 和 65535 之间。");
        if (MaxConnections is < 1 or > 128) throw new InvalidOperationException("SFTP 最大连接数必须介于 1 和 128 之间。");
        if (MaxUploadBytes is < 1 or > 1_099_511_627_776) throw new InvalidOperationException("SFTP 单文件上传限制必须介于 1 字节和 1 TB 之间。");
        if (MaxAuthenticationFailures is < 1 or > 20) throw new InvalidOperationException("SFTP 认证失败次数必须介于 1 和 20 之间。");
        if (EffectiveAuthenticationBlockDuration < TimeSpan.FromSeconds(1) || EffectiveAuthenticationBlockDuration > TimeSpan.FromDays(1)) throw new InvalidOperationException("SFTP 认证封禁时长必须介于 1 秒和 1 天之间。");
        if (Enabled && (Accounts is null || Accounts.Count == 0)) throw new InvalidOperationException("启用 SFTP 前至少配置一个账号。");
        if (Enabled && (Shares is null || Shares.Count == 0)) throw new InvalidOperationException("启用 SFTP 前至少配置一个共享目录。");
        if (Accounts is not null)
        {
            if (Accounts.Any(x => string.IsNullOrWhiteSpace(x.UserName)) ||
                Accounts.GroupBy(x => x.UserName, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
                throw new InvalidOperationException("SFTP 账号名称必须非空且唯一。");
        }
        if (Shares is not null)
        {
            if (Shares.Any(x => string.IsNullOrWhiteSpace(x.VirtualName) || string.IsNullOrWhiteSpace(x.PhysicalPath) || !Directory.Exists(x.PhysicalPath)) ||
                Shares.GroupBy(x => x.VirtualName, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
                throw new InvalidOperationException("SFTP 共享目录必须存在且虚拟名称唯一。");

            var roots = Shares.Select(x => Path.GetFullPath(x.PhysicalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            for (var i = 0; i < roots.Length; i++)
                for (var j = i + 1; j < roots.Length; j++)
                    if (roots[j].StartsWith(roots[i] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        roots[i].StartsWith(roots[j] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("SFTP 共享目录不能重叠或互相包含。");
        }
        if (Accounts is not null && Shares is not null)
        {
            var shareNames = Shares.Select(x => x.VirtualName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Accounts.Any(account => account.AllowedShares is { Count: > 0 } && account.AllowedShares.Any(name => !shareNames.Contains(name))))
                throw new InvalidOperationException("SFTP 账号包含不存在的共享目录授权。");
        }
    }
}
