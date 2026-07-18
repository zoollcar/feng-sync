namespace FengSync.Core.Configuration;

/// <summary>Immutable runtime snapshot; callers never mutate application settings while using a profile.</summary>
public sealed record EffectiveProfileSettings(int MaxConcurrentCopies, bool VerifyCopies, SyncFilter Filter, VersioningPolicy Versioning, int TimeToleranceSeconds)
{
    public static EffectiveProfileSettings Resolve(SyncProfile profile, ApplicationSettings application)
    {
        var settings = profile.Settings;
        // Legacy fields remain readable so old JSON and callers continue to work during migration.
        return new(settings?.MaxConcurrentCopies ?? profile.MaxConcurrentCopies,
            settings?.VerifyCopies ?? profile.VerifyCopies,
            settings?.Filter ?? profile.Filter ?? application.DefaultFilter,
            settings?.Versioning ?? profile.Versioning ?? application.DefaultVersioning,
            settings?.TimeToleranceSeconds ?? application.DefaultTimeToleranceSeconds);
    }
}
