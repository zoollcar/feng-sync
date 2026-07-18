namespace FengSync.Core.Configuration;

/// <summary>Nullable values deliberately inherit the corresponding application default.</summary>
public sealed record ProfileSettings(
    int? MaxConcurrentCopies = null,
    bool? VerifyCopies = null,
    SyncFilter? Filter = null,
    VersioningPolicy? Versioning = null,
    int? TimeToleranceSeconds = null);
