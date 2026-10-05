namespace FengSync.Core.Mount;

/// <summary>Compile-time defaults for <c>rclone mount</c> invocations that Feng Sync itself starts.</summary>
public static class MountOptions
{
    /// <summary>
    /// Root of the VFS cache. The daemon is started with this as <c>--cache-dir</c>; there is no per-mount
    /// cache directory, because rclone fixes the cache root for the whole process at startup.
    /// </summary>
    public static string CacheRoot => AppDataPaths.MountCache;

    /// <summary>
    /// The historical cache ceiling, and the default a mount starts from when the application setting has not
    /// been changed. rclone's own default is -1 (no limit), which is a real alternative — the cache then grows
    /// until the disk fills — so 0 in a settings box means "unlimited" and is translated into omitting the key
    /// rather than sending a number rclone would read as "a cache of zero bytes".
    /// </summary>
    public const long DefaultCacheMaxSizeBytes = 10L * 1024 * 1024 * 1024;

    public static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(1);
    public static readonly TimeSpan WriteBack = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DirCacheTime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>The cache mode every mount used before the option existed, and the default.</summary>
    public const MountCacheMode DefaultCacheMode = MountCacheMode.Writes;

    /// <summary>The rclone spelling of a cache mode, as <c>vfsOpt.CacheMode</c> accepts it.</summary>
    public static string WireCacheMode(MountCacheMode mode) => mode switch
    {
        MountCacheMode.Off => "off",
        MountCacheMode.Minimal => "minimal",
        MountCacheMode.Full => "full",
        _ => "writes"
    };

    /// <summary>What the cache mode costs, in the terms the picker has to be honest about.</summary>
    public static string DescribeCacheMode(MountCacheMode mode) => mode switch
    {
        MountCacheMode.Off => "不缓存：每次读取都走网络，最省磁盘但最慢；写入也直接落到云端。",
        MountCacheMode.Minimal => "只缓存读到的内容，不缓存写入；写文件时上传更频繁。",
        MountCacheMode.Writes => "缓存读取并缓冲写入，兼顾速度与磁盘占用（默认）。",
        _ => "全部缓存，包括正在写入的整个文件；最快，但一次大写入会占满缓存目录。"
    };

    /// <summary>
    /// The <c>vfsOpt</c> block for <c>mount/mount</c>. Passing nothing (or an empty
    /// <see cref="MountOptionsSettings"/>) reproduces the payload every mount used before per-mount options
    /// existed, so an unconfigured mount behaves exactly as it always did.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> CreateVfsOptions(MountOptionsSettings? settings = null)
    {
        var options = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["CacheMode"] = WireCacheMode(settings?.CacheMode ?? DefaultCacheMode),
            ["CacheMaxAge"] = Seconds(settings?.CacheMaxAge ?? CacheMaxAge),
            ["WriteBack"] = Seconds(settings?.WriteBack ?? WriteBack),
            ["NoChecksum"] = settings?.NoChecksum ?? true,
            ["NoModTime"] = settings?.NoModTime ?? true
        };
        // Only keys with a value worth stating are sent. rclone's sentinels for "unset" are -1 for both sizes,
        // so writing 0 to mean "unlimited" would not be a no-op — it is the one mistake this builder exists
        // to make impossible. A ceiling of 0 therefore means "no ceiling" and is expressed by leaving the key
        // out entirely, which is also what an unconfigured mount did before this option existed.
        var cacheMaxSize = settings?.CacheMaxSizeBytes ?? DefaultCacheMaxSizeBytes;
        if (cacheMaxSize > 0) options["CacheMaxSize"] = cacheMaxSize;
        if (settings?.CacheMinFreeSpaceBytes is > 0) options["CacheMinFreeSpace"] = settings.CacheMinFreeSpaceBytes;
        if (settings?.DiskSpaceTotalSize is > 0) options["DiskSpaceTotalSize"] = settings.DiskSpaceTotalSize;
        if (settings?.DirCacheTime is { } dirCacheTime) options["DirCacheTime"] = Seconds(dirCacheTime);
        if (settings?.PollInterval is { } pollInterval) options["PollInterval"] = Seconds(pollInterval);
        if (settings?.ReadOnly == true) options["ReadOnly"] = true;
        if (settings?.NoSeek == true) options["NoSeek"] = true;
        return options;
    }

    /// <summary>
    /// The <c>mountOpt</c> block. Empty until something is asked for, which is what it always was: Windows
    /// picked the volume label itself and the mount was a fixed disk rather than a network drive.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> CreateMountOptions(MountOptionsSettings? settings = null)
    {
        var options = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(settings?.VolumeName)) options["VolumeName"] = settings!.VolumeName!.Trim();
        if (settings?.NetworkMode == true) options["NetworkMode"] = true;
        return options;
    }

    /// <summary>
    /// A duration as rclone's parser reads it. Seconds rather than Go's <c>1h0m0s</c> form because it is exact
    /// for every value, needs no per-unit branching, and was measured to land unchanged in
    /// <c>vfs/stats.opt</c> (<c>"3600s"</c> → 3600000000000 ns).
    /// </summary>
    public static string Seconds(TimeSpan value) => ((long)value.TotalSeconds).ToString() + "s";

    /// <summary>
    /// Option combinations rclone <b>accepts</b> but documents as wrong. A warning rather than a refusal,
    /// because the refusal would be invented: measured against the bundled v1.75.0, a mount with
    /// <c>PollInterval=600s</c> beside <c>DirCacheTime=60s</c> succeeded and both values landed unchanged in
    /// <c>vfs/stats.opt</c>. rclone enforces the rule nowhere; only <c>options/info</c> states it, and it
    /// states it as a "must" — so the user is told, and the mount is still theirs to make.
    /// </summary>
    public static IReadOnlyList<string> Warnings(MountOptionsSettings? settings)
    {
        if (settings is null) return [];
        var warnings = new List<string>();
        var dirCache = settings.DirCacheTime ?? DirCacheTime;
        var poll = settings.PollInterval ?? PollInterval;
        if (poll >= dirCache)
            warnings.Add($"轮询间隔（{poll.TotalSeconds:0} 秒）不小于目录缓存时间（{dirCache.TotalSeconds:0} 秒）："
                + "rclone 自己的帮助文本要求轮询间隔更小，否则目录缓存每次都在轮询之前就过期了。");
        return warnings;
    }
}
