namespace FengSync.Core;

/// <summary>Locates the rclone executable shipped beside Feng Sync.  Never falls back to PATH.</summary>
public static class BundledRclone
{
    public static string ExecutablePath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "rclone", "rclone.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("Feng Sync 安装包缺少内置 rclone.exe。请重新安装完整的 Windows x64 发行包。", path);
            return path;
        }
    }

    public static string ConfigPath => Path.Combine(AppDataPaths.Root, "rclone", "rclone.conf");
}
