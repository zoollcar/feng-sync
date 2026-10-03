namespace FengSync.Core.Mount;

/// <summary>Compile-time defaults for <c>rclone mount</c> invocations that Feng Sync itself starts.</summary>
public static class MountOptions
{
    /// <summary>
    /// Root of the VFS cache. The daemon is started with this as <c>--cache-dir</c>; there is no per-mount
    /// cache directory, because rclone fixes the cache root for the whole process at startup.
    /// </summary>
    public static string CacheRoot => AppDataPaths.MountCache;

    public const long CacheMaxSizeBytes = 10L * 1024 * 1024 * 1024;
    public static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(1);
    public static readonly TimeSpan WriteBack = TimeSpan.FromSeconds(5);

    /// <summary>Typed VFS option block consumed by <c>mount/mount</c>.</summary>
    public static object CreateVfsOptions() => new
    {
        CacheMode = "writes",
        CacheMaxSize = CacheMaxSizeBytes,
        CacheMaxAge = "1h",
        WriteBack = "5s",
        NoChecksum = true,
        NoModTime = true
    };
}
