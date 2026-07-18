namespace FengSync.Core.Configuration;

/// <summary>Application-wide behavior and defaults for newly created/inheriting profiles.</summary>
public sealed record ApplicationSettings
{
    public int SchemaVersion { get; init; } = ConfigurationMigrator.CurrentSchemaVersion;
    public int DefaultMaxConcurrentCopies { get; init; } = 3;
    public bool DefaultVerifyCopies { get; init; } = true;
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
}
