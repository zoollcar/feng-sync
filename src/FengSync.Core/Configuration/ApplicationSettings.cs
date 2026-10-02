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
}
