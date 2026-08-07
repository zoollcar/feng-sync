namespace FengSync.Core.Mount;

/// <summary>Compile-time defaults for <c>rclone mount</c> invocations that Feng Sync itself starts.</summary>
public static class MountOptions
{
    /// <summary>Per-session cache root; each mount gets its own subdirectory so cleanup is straightforward.</summary>
    public static string CacheRoot => Path.Combine(AppDataPaths.Root, "mount", "cache");

    public static string CacheDirectoryFor(Guid sessionId) => Path.Combine(CacheRoot, sessionId.ToString("N"));

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

    /// <summary>Per-call global configuration. CacheDir is not a mount CLI flag here.</summary>
    public static object CreateLocalConfiguration(string cacheDirectory) => new { CacheDir = cacheDirectory };

}
