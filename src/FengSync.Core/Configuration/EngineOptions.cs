using FengSync.Core.Configuration;

namespace FengSync.Core;

/// <summary>
/// Engine feature flags from the M6 rollout plan. The flags let tests and
/// advanced users opt into the new pipeline before it is enabled by default.
/// They are intentionally not exposed in the main UI; the long-term plan is to
/// retire them entirely as each subsystem stabilises.
/// </summary>
public sealed record EngineFeatureFlags(
    bool SnapshotV2 = true,
    bool LazyHash = true,
    bool VerifierV2 = true,
    bool BaselineV2 = true,
    bool JournalWal = true,
    bool DeviceScheduler = true,
    bool RcloneBatch = false)
{
    public static EngineFeatureFlags Defaults { get; } = new();

    /// <summary>
    /// Stable snapshot of the flags for the duration of a run. Tests rely on
    /// this so a flag flip from the CLI does not leak across concurrent runs.
    /// </summary>
    public static EngineFeatureFlags Resolve(string? raw = null) => string.IsNullOrWhiteSpace(raw) ? Defaults : Parse(raw);

    private static EngineFeatureFlags Parse(string raw)
    {
        var flags = Defaults;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = part.Split('=');
            var value = key.Length > 1 && bool.TryParse(key[1], out var b) ? b : true;
            var name = key[0];
            flags = name switch
            {
                "snapshot-v2" => flags with { SnapshotV2 = value },
                "lazy-hash" => flags with { LazyHash = value },
                "verifier-v2" => flags with { VerifierV2 = value },
                "baseline-v2" => flags with { BaselineV2 = value },
                "journal-wal" => flags with { JournalWal = value },
                "device-scheduler" => flags with { DeviceScheduler = value },
                "rclone-batch" => flags with { RcloneBatch = value },
                _ => flags
            };
        }
        return flags;
    }
}

/// <summary>
/// Run-scoped configuration aggregated from the application settings, profile
/// overrides and the engine feature flags. Holding the resolved values in one
/// struct keeps the new executor simple and lets tests assert on the flags
/// used for a specific run.
/// </summary>
public sealed record EngineOptions(EngineFeatureFlags Flags, int MaxConcurrentCopies, bool VerifyCopies)
{
    public static EngineOptions From(EffectiveProfileSettings effective, EngineFeatureFlags flags) =>
        new(flags, effective.MaxConcurrentCopies, effective.VerifyCopies);
}