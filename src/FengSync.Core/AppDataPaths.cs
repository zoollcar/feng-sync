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

    /// <summary>
    /// Where the browser keeps the small picture bytes it pulled for grid tiles. Its own directory rather than
    /// a corner of <c>cloud-cache</c>: that one holds full copies of files the user dragged out, is keyed by
    /// path, and is worth keeping when a tile cache is not — a tile cache is throwaway by design and has to be
    /// deletable at any moment without touching anything the user asked to keep.
    /// </summary>
    public static string CloudThumbnails => Path.Combine(Root, "cloud-thumbnails");
}
