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
}

/// <summary>
/// What an upload does when the destination already holds a file with the same name. The upload path
/// predates this setting and asked once per batch; the policy makes the answer reusable.
/// </summary>
public enum CloudUploadConflictPolicy
{
    /// <summary>Ask once per batch, which is the historical behaviour.</summary>
    Ask,
    /// <summary>Replace the existing file without asking.</summary>
    Overwrite,
    /// <summary>Leave the existing file alone and upload only the names that are free.</summary>
    Skip
}
