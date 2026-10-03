namespace FengSync.Core;

/// <summary>Central application-data root. Integration tests can isolate all durable state with FENGSYNC_DATA_DIR.</summary>
public static class AppDataPaths
{
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FengSync")
                : Path.GetFullPath(overridden);
        }
    }

    /// <summary>
    /// Where rclone keeps the VFS cache that backs Feng Sync mounts. This has to be handed to the daemon as
    /// a command-line flag: rclone resolves its cache root once when the process starts, so the per-call
    /// <c>_config</c> override on <c>mount/mount</c> is silently ignored.
    /// </summary>
    public static string MountCache => Path.Combine(Root, "mount", "cache");
}
