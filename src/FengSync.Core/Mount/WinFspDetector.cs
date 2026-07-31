using System.Runtime.Versioning;

namespace FengSync.Core.Mount;

/// <summary>Outcome of probing for the WinFsp user-mode filesystem driver that rclone mount requires on Windows.</summary>
public sealed record WinFspStatus(bool Installed, string Summary)
{
    public static WinFspStatus Ok() => new(true, "WinFsp 已安装。");
    public static WinFspStatus Missing(string detail) => new(false, "未检测到 WinFsp。" + detail);
}

/// <summary>Detects WinFsp via its well-known registry keys so the UI can warn before mount attempts.</summary>
[SupportedOSPlatform("windows")]
public static class WinFspDetector
{
    private static readonly string[] RegistryKeys =
    {
        @"SOFTWARE\WOW6432Node\WinFsp",
        @"SOFTWARE\WinFsp"
    };

    public static WinFspStatus Detect()
    {
        if (!OperatingSystem.IsWindows()) return WinFspStatus.Missing("当前平台不是 Windows。");
        try
        {
            using var baseKey = Microsoft.Win32.Registry.LocalMachine;
            foreach (var path in RegistryKeys)
            {
                using var sub = baseKey.OpenSubKey(path);
                if (sub is not null) return WinFspStatus.Ok();
            }
            return WinFspStatus.Missing("请前往 https://winfsp.dev/ 安装 WinFsp，它是 rclone mount 的运行时依赖。");
        }
        catch (Exception ex)
        {
            return WinFspStatus.Missing("无法读取注册表：" + ex.Message);
        }
    }
}