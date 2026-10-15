namespace FengSync.Core.Configuration;

/// <summary>Application-wide behavior and defaults for newly created/inheriting profiles.</summary>
public sealed record ApplicationSettings
{
    public int SchemaVersion { get; init; } = ConfigurationMigrator.CurrentSchemaVersion;
    public int DefaultMaxConcurrentCopies { get; init; } = 3;
    public bool DefaultVerifyCopies { get; init; } = true;
    /// <summary>
    /// Files at or above this size keep a resumable staging file, so an interrupted download can be
    /// continued instead of restarted. 0 continues every file, however small. Defaults to 500 MB.
    /// </summary>
    public long DefaultResumeMinimumBytes { get; init; } = 500L * 1024 * 1024;
    public SyncFilter DefaultFilter { get; init; } = SyncFilter.Empty;
    public VersioningPolicy DefaultVersioning { get; init; } = new();
    /// <summary>Timestamp drift accepted by newly created/inheriting profiles.</summary>
    public int DefaultTimeToleranceSeconds { get; init; } = 2;
    public bool ShowCompleted { get; init; } = true;
    public int LogRetentionDays { get; init; } = 30;
    public bool NotifyOnCompletion { get; init; }
    public int NetworkRetryCount { get; init; } = 3;
    public bool StartWithWindows { get; init; }
    public string? LastSelectedProfileId { get; init; }
    public bool AutoCheckForUpdates { get; init; } = true;
    public DateTimeOffset? LastUpdateCheckUtc { get; init; }
    public string? LatestReleaseEtag { get; init; }
    public string? SkippedUpdateVersion { get; init; }
    /// <summary>Persisted main-window Profile workspace width in device-independent pixels.</summary>
    public double MainWindowSidebarWidth { get; init; } = 260;

    /// <summary>
    /// How many cloud uploads/downloads the cloud manager runs at once. Deliberately separate from
    /// <see cref="DefaultMaxConcurrentCopies"/>: a sync run is one planned batch over a profile, while the
    /// cloud manager is an interactive queue the user watches and reorders by hand, so tuning one must not
    /// silently retune the other.
    /// </summary>
    public int CloudMaxConcurrentTransfers { get; init; } = 2;

    /// <summary>
    /// Automatic retries for a cloud transfer before it is reported as failed. 0 means a failure is
    /// reported immediately and only the user's own 断点续传 restarts it.
    /// </summary>
    public int CloudRetryCount { get; init; } = 2;

    /// <summary>
    /// Where the file browser downloads to when the user accepts the default location. Empty means the
    /// user is asked for a path on every download.
    /// </summary>
    public string? CloudDownloadDirectory { get; init; }

    /// <summary>What an upload does when the destination directory already holds the same file name.</summary>
    public CloudUploadConflictPolicy CloudUploadConflict { get; init; } = CloudUploadConflictPolicy.Ask;

    /// <summary>
    /// How the file browser draws the rows it has. A display preference, not a behaviour switch: the same
    /// filtered, ordered rows are behind both, so what 全选 / 复制 / 删除 act on never depends on this.
    /// Defaults to <see cref="CloudBrowserViewMode.List"/> because a listing costs nothing to draw, while
    /// tiles pull picture bytes for every image row they show.
    /// </summary>
    public CloudBrowserViewMode CloudBrowserView { get; init; } = CloudBrowserViewMode.List;

    /// <summary>
    /// Notifies when a cloud transfer batch drains. Separate from <see cref="NotifyOnCompletion"/> because
    /// that one fires for a scheduled sync run, which the user may never be watching.
    /// </summary>
    public bool CloudNotifyOnCompletion { get; init; } = true;

    /// <summary>
    /// Re-lists unfinished cloud transfers when a shell starts. Off means the transfer window loads them on
    /// first open instead, which is what a user who never uses the cloud manager wants.
    /// </summary>
    public bool CloudResumeOnStartup { get; init; } = true;

    /// <summary>
    /// Upload ceiling for cloud transfers, in bytes per second. 0 means no limit, which is what every
    /// installation had before this setting existed. Applied through <c>core/bwlimit</c>, which is a
    /// <b>process-wide</b> rate: rclone refuses per-remote specs there, so this cannot be narrowed to one
    /// endpoint (see <see cref="CloudTransferLimit"/>).
    /// </summary>
    public long CloudUploadLimitBytesPerSecond { get; init; }

    /// <summary>Download ceiling for cloud transfers, in bytes per second. 0 means no limit.</summary>
    public long CloudDownloadLimitBytesPerSecond { get; init; }

    /// <summary>
    /// The daily window cloud transfers may run in. <see langword="null"/> means any time, which is the
    /// behaviour every installation had before this setting existed.
    /// </summary>
    public CloudTransferTimeWindow? CloudTransferTimeWindow { get; init; }

    /// <summary>
    /// What to do once a batch of cloud transfers has drained with nothing failed. Defaults to doing
    /// nothing, because the other three take the machine away from the user; whichever is chosen is
    /// confirmed before it happens.
    /// </summary>
    public CloudAfterTransferAction CloudAfterTransfer { get; init; } = CloudAfterTransferAction.None;

    /// <summary>
    /// Ceiling for the mount VFS cache under <see cref="AppDataPaths.MountCache"/>. It is the default a new
    /// mount starts from; a single mount can override it in the mount picker. 0 means unlimited, which is
    /// rclone's own default and is a real choice — the cache then grows until the disk fills.
    /// </summary>
    public long MountCacheMaxSizeBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Fixed proxy for the rclone processes this application starts, for example <c>proxy.corp:8080</c> or
    /// <c>socks5://127.0.0.1:1080</c>. Empty inherits the environment variables and then the Windows
    /// Internet Settings.
    /// </summary>
    /// <remarks>
    /// Named after rclone rather than after the cloud page it is edited on: there is one daemon per
    /// process and its proxy is fixed at process start, so this governs every rclone operation — cloud
    /// browsing, mounts, the SFTP service and profile sync runs alike. It never touches the system proxy.
    /// </remarks>
    public string? RcloneProxy { get; init; }

    /// <summary>
    /// Which language the shell's own text is shown in. A display preference rather than a behaviour
    /// switch: it selects the resource set every visible string is read from, and nothing else changes.
    /// Defaults to <see cref="AppLanguage.Chinese"/> because that is the language this application was
    /// written in, and a setting nobody has touched must not repaint the app under them.
    /// </summary>
    public AppLanguage Language { get; init; } = AppLanguage.Chinese;

    /// <summary>
    /// The port the LAN transfer window listens on — both the TCP transfer and the UDP discovery, which are
    /// different protocols on one number so that one firewall rule each can name it.
    /// </summary>
    /// <remarks>
    /// Fixed rather than chosen by the operating system, because a rule and a QR code both have to carry a
    /// number a person can type. Kept in the settings file rather than beside the listener because a network
    /// that already occupies 59871 is a thing the user changes once, on one machine, and remembers.
    /// The value is the same number <c>FengSync.Lan.LanOptions.DefaultPort</c> starts from; Core cannot see
    /// that library, and a settings default nobody validates against is how the two would drift.
    /// </remarks>
    public int LanTransferPort { get; init; } = 59871;

    /// <summary>
    /// Where files another device sends to this computer land. <see langword="null"/> means the system's own
    /// Downloads folder, resolved when the window opens rather than stored here — the shape
    /// <see cref="CloudDownloadDirectory"/> has, for the same reason: a path that no longer exists must read as
    /// "fall back", not as "the user chose nothing".
    /// </summary>
    public string? LanReceiveDirectory { get; init; }

    /// <summary>
    /// Whether a device that is already on the trust list may send without another click each time. On by
    /// default because the first contact is already confirmed by a person; turning it off makes every transfer
    /// ask, which is what a shared office machine wants.
    /// </summary>
    public bool LanAutoAcceptFromTrusted { get; init; } = true;

    /// <summary>
    /// Whether the cloud manager has already shown its first-run guide (E5). Off by default, and it is the
    /// only thing that makes the guide stop: <c>CloudFirstRunGuide.ShouldShow</c> also declines to show it to
    /// somebody who already has endpoints, so this flag is what silences it for a user who read it and said
    /// nothing. Saved by the window that owns the guide, not by <c>SettingsWindow</c>.
    /// </summary>
    public bool CloudGuideSeen { get; init; }
}

/// <summary>
/// The shell's languages. Persisted as a number, so the list is append-only: renumbering an existing
/// member would silently repaint somebody's saved choice as a different language.
/// </summary>
public enum AppLanguage
{
    /// <summary>简体中文。The default.</summary>
    Chinese,
    /// <summary>English.</summary>
    English
}

/// <summary>
/// What an upload does when the destination already holds a file with the same name. The upload path
/// predates this setting and asked once per batch; the policy makes the answer reusable.
/// </summary>
public enum CloudUploadConflictPolicy
{
    /// <summary>Ask about each conflicted name, with the choice of applying one answer to the rest.</summary>
    Ask,
    /// <summary>Replace the existing file without asking.</summary>
    Overwrite,
    /// <summary>Leave the existing file alone and upload only the names that are free.</summary>
    Skip,
    /// <summary>
    /// Keep both files: the incoming one lands beside the existing one under a free name. The answer a user
    /// reaches for when they are not sure the two files are the same file, and the only one that never loses
    /// either side.
    /// </summary>
    KeepBoth
}

/// <summary>
/// How the file browser draws its rows. Persisted as a number, so the list is append-only: renumbering an
/// existing member would silently repaint somebody's saved choice as a different view.
/// </summary>
public enum CloudBrowserViewMode
{
    /// <summary>One row per file, with 类型 / 大小 / 修改时间 columns. The default, and the only view that reads nothing.</summary>
    List,
    /// <summary>Square tiles that show a picture for an image row and the file icon for every other row.</summary>
    Grid,
}
