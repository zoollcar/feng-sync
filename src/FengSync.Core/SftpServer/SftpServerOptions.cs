using System.Net;

namespace FengSync.Core.SftpServer;

/// <summary>Configuration for the bundled rclone SFTP server.  One service exposes one writable root.</summary>
public sealed record SftpServerOptions(
    bool Enabled = false,
    bool StartWithApplication = false,
    string ListenAddress = "127.0.0.1",
    int Port = 2222,
    string? RootPath = null,
    string? UserName = null,
    string? HostKeyPath = null,
    long CacheMaxSizeBytes = 10L * 1024 * 1024 * 1024,
    bool PasswordConfigured = false)
{
    public void Validate()
    {
        if (!IPAddress.TryParse(ListenAddress, out _)) throw new InvalidOperationException("SFTP 监听地址必须是明确的 IP 地址。");
        if (Port is < 1 or > 65535) throw new InvalidOperationException("SFTP 端口必须介于 1 和 65535 之间。");
        if (CacheMaxSizeBytes is < 1_073_741_824 or > 1_099_511_627_776) throw new InvalidOperationException("SFTP 缓存上限必须介于 1 GiB 和 1 TiB 之间。");
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(RootPath) || !Directory.Exists(RootPath)) throw new InvalidOperationException("启用 SFTP 前必须选择一个存在的共享根目录。");
        if (string.IsNullOrWhiteSpace(UserName)) throw new InvalidOperationException("启用 SFTP 前必须设置用户名。");
        if (!PasswordConfigured) throw new InvalidOperationException("启用 SFTP 前必须设置密码。");
    }
}
