using System.Globalization;

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

    /// <summary>Append the standard Feng Sync mount arguments to <paramref name="args"/>.</summary>
    public static void AppendMountArguments(List<string> args, string remoteSpec, string mountPoint, string cacheDirectory, string configPath)
    {
        // rclone mount is read-write by default; only --read-only / --ro switches it. Don't pass
        // --read-write explicitly — it's not a flag in rclone 1.74.x and is rejected as
        // "unknown flag :-- read-write".
        args.Add("mount");
        args.Add(remoteSpec);
        args.Add(mountPoint);
        args.Add("--no-checksum");
        args.Add("--no-modtime");
        args.Add("--vfs-cache-mode");
        args.Add("writes");
        args.Add("--cache-dir");
        args.Add(cacheDirectory);
        args.Add("--vfs-cache-max-size");
        args.Add(CacheMaxSizeBytes.ToString(CultureInfo.InvariantCulture));
        args.Add("--vfs-cache-max-age");
        args.Add("1h");
        args.Add("--vfs-write-back");
        args.Add("5s");
        args.Add("--config");
        args.Add(configPath);
    }
}