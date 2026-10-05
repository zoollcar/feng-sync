namespace FengSync.Core.Mount;

/// <summary>
/// Which rclone VFS cache mode a mount runs in. Persisted as a number in <c>mounts.json</c>, so the members
/// are append-only: renumbering one would silently repaint somebody's saved choice as a different mode.
/// </summary>
public enum MountCacheMode
{
    /// <summary>No cache at all: every read goes to the network. The honest replacement for "files on demand".</summary>
    Off,
    /// <summary>Cache only what is read, never what is written.</summary>
    Minimal,
    /// <summary>Cache reads and buffer writes. The default, and what every mount used before this existed.</summary>
    Writes,
    /// <summary>Cache everything, including whole files opened for writing.</summary>
    Full
}

/// <summary>
/// One mount's option overrides. Every member is an override: <see langword="null"/> means "whatever Feng Sync
/// used before this record existed", so an empty instance and no instance at all build the same payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this record exists at all.</b> rclone <i>silently ignores</i> an option name it does not recognise,
/// so a misspelling in a mount request produces a perfectly successful mount that behaves like the default.
/// Measured against the bundled v1.75.0: a request naming <c>cacheModee</c>/<c>CacheMaxSizee</c>/<c>diskSpaceTotal</c>
/// mounted fine and left <c>vfs/stats.opt</c> at <c>CacheMode=0</c> (off), <c>CacheMaxSize=-1</c> and
/// <c>DiskSpaceTotalSize=-1</c> — no error, no warning, nothing in the log. The only defence is that the
/// option names live in exactly one place (this record's <c>Create*</c> builders) and a test asserts the keys
/// that go on the wire.
/// </para>
/// <para>
/// <b>What is deliberately absent.</b> <c>umask</c>, <c>allow_non_empty</c>, <c>allow_other</c> and
/// <c>default_permissions</c> are not offered, because <c>options/info</c> documents all four as "not supported
/// on Windows" and this application only runs on Windows. A switch that rclone accepts and then ignores is the
/// exact failure this record is built to prevent, so offering them would be worse than not offering them.
/// </para>
/// <para>
/// <b>Key spelling.</b> The nested <c>vfsOpt</c>/<c>mountOpt</c> blocks take the option's Go field name
/// (PascalCase: <c>CacheMaxSize</c>, <c>DiskSpaceTotalSize</c>, <c>VolumeName</c>), which is what
/// <c>options/info</c> reports as <c>FieldName</c> and what <c>vfs/stats.opt</c> echoes back. The flat
/// top-level form instead takes the CLI name (<c>vfs_cache_max_size</c>); this code always uses the nested
/// form. Both spellings happen to work because Go's JSON decoder matches field names case-insensitively —
/// but a <i>typo</i> does not, which is why the distinction matters.
/// </para>
/// </remarks>
public sealed record MountOptionsSettings
{
    /// <summary>Label Windows shows for the mounted volume. Windows and macOS only; empty means the drive letter.</summary>
    public string? VolumeName { get; init; }

    /// <summary>
    /// Mount as a remote network drive instead of a fixed disk. Windows only, and the reason a UNC mount point
    /// behaves the way it does (see <see cref="MountTargetKind.NetworkShare"/>).
    /// </summary>
    public bool NetworkMode { get; init; }

    /// <summary>Which cache mode to run in. <see langword="null"/> keeps the historical <c>writes</c>.</summary>
    public MountCacheMode? CacheMode { get; init; }

    /// <summary>
    /// Ceiling for the VFS cache. <see langword="null"/> keeps the historical application default
    /// (<see cref="MountOptions.DefaultCacheMaxSizeBytes"/>); <b>0 means no ceiling at all</b>, which is
    /// expressed by omitting the key rather than by sending a number — rclone's sentinel for "unset" is -1,
    /// so a literal 0 would be read as a zero-byte cache instead of as "unlimited".
    /// </summary>
    public long? CacheMaxSizeBytes { get; init; }

    /// <summary>Free space rclone tries to leave on the cache disk. <see langword="null"/> leaves rclone's -1.</summary>
    public long? CacheMinFreeSpaceBytes { get; init; }

    /// <summary>How long an object may sit unused in the cache.</summary>
    public TimeSpan? CacheMaxAge { get; init; }

    /// <summary>How long after last use a cached write is flushed.</summary>
    public TimeSpan? WriteBack { get; init; }

    /// <summary>How long directory listings are cached.</summary>
    public TimeSpan? DirCacheTime { get; init; }

    /// <summary>How often the mount polls the remote for changes. Must stay below <see cref="DirCacheTime"/>.</summary>
    public TimeSpan? PollInterval { get; init; }

    /// <summary>
    /// Total size to report for the mounted volume, in bytes. Set from the endpoint's own answer to
    /// <c>operations/about</c> when it has one; left <see langword="null"/> when it does not, because writing 0
    /// would report a full disk.
    /// </summary>
    public long? DiskSpaceTotalSize { get; init; }

    /// <summary>Refuse every write through the mount. A read-only mount is a real choice, not a degraded one.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Refuse seeks, which some backends handle badly.</summary>
    public bool NoSeek { get; init; }

    /// <summary>Skip checksum comparison on up/download. On by default, as it always was.</summary>
    public bool NoChecksum { get; init; } = true;

    /// <summary>Do not read or write modification times. On by default, as it always was.</summary>
    public bool NoModTime { get; init; } = true;

    /// <summary>Whether anything here differs from the historical defaults, i.e. whether it is worth persisting.</summary>
    public bool IsDefault =>
        string.IsNullOrWhiteSpace(VolumeName) && !NetworkMode && CacheMode is null or MountCacheMode.Writes
        && CacheMaxSizeBytes is null && CacheMinFreeSpaceBytes is null && CacheMaxAge is null && WriteBack is null
        && DirCacheTime is null && PollInterval is null && DiskSpaceTotalSize is null && !ReadOnly && !NoSeek
        && NoChecksum && NoModTime;
}
