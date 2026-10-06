using System.Text.Json;

namespace FengSync.Core.Configuration;

public sealed record SettingsLoadResult(
    ApplicationSettings Settings,
    bool RecoveredFromCorruption = false,
    string? BackupPath = null,
    bool Migrated = false,
    int? MigratedFromSchemaVersion = null,
    string? MigrationBackupPath = null);

/// <summary>Atomic settings persistence. Invalid input is retained as a timestamped backup rather than discarded.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    public SettingsStore(string? path = null) => _path = path ?? Path.Combine(AppDataPaths.Root, "FengSync.local.json");

    /// <summary>
    /// The blocking read for callers that genuinely cannot await. The interface language has to be known before
    /// the first window is created, and that moment is inside the dispatcher frame, on the very thread a task
    /// continuation would need — so blocking there on <see cref="LoadAsync"/> is a deadlock rather than a slow
    /// start, and this overload exists to make that impossible. It is one small local JSON; reading it
    /// synchronously is the point, not a shortcut.
    /// </summary>
    public SettingsLoadResult Load(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new(new ApplicationSettings());
        try
        {
            ApplicationSettings settings;
            using (var stream = File.OpenRead(_path))
                settings = JsonSerializer.Deserialize<ApplicationSettings>(stream) ?? new ApplicationSettings();
            var (migrated, originalSchemaVersion, backup) = Migrate(settings);
            if (backup is null) return new(migrated);
            Save(migrated, ct);
            return new(migrated, Migrated: true, MigratedFromSchemaVersion: originalSchemaVersion, MigrationBackupPath: backup);
        }
        catch (JsonException) { return QuarantineCorruptFile(); }
    }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new(new ApplicationSettings());
        try
        {
            ApplicationSettings settings;
            await using (var stream = File.OpenRead(_path))
                settings = await JsonSerializer.DeserializeAsync<ApplicationSettings>(stream, cancellationToken: ct) ?? new ApplicationSettings();
            var (migrated, originalSchemaVersion, backup) = Migrate(settings);
            if (backup is null) return new(migrated);
            await SaveAsync(migrated, ct);
            return new(migrated, Migrated: true, MigratedFromSchemaVersion: originalSchemaVersion, MigrationBackupPath: backup);
        }
        catch (JsonException) { return QuarantineCorruptFile(); }
    }

    /// <summary>The blocking counterpart of <see cref="SaveAsync"/>, for the same caller that cannot await.</summary>
    public void Save(ApplicationSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var errors = ConfigurationValidator.Validate(settings);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, _path, true);
    }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken ct = default)
    {
        var errors = ConfigurationValidator.Validate(settings);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var stream = File.Create(temporary)) await JsonSerializer.SerializeAsync(stream, settings, cancellationToken: ct);
        File.Move(temporary, _path, true);
    }

    /// <summary>
    /// The half of a load that does not depend on how the bytes arrived: migrate, and when the migration is an
    /// on-disk change, retain the source before replacing it so the user can inspect/recover legacy settings
    /// even if a later release regresses. A null backup means nothing has to be written back.
    /// </summary>
    private (ApplicationSettings Settings, int OriginalSchemaVersion, string? Backup) Migrate(ApplicationSettings settings)
    {
        var originalSchemaVersion = settings.SchemaVersion;
        var migrated = new ConfigurationMigrator().Migrate(settings);
        if (originalSchemaVersion == migrated.SchemaVersion) return (migrated, originalSchemaVersion, null);
        var backup = _path + $".schema-v{originalSchemaVersion}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak";
        File.Copy(_path, backup, overwrite: false);
        return (migrated, originalSchemaVersion, backup);
    }

    private SettingsLoadResult QuarantineCorruptFile()
    {
        var backup = _path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak";
        File.Move(_path, backup, true);
        return new(new ApplicationSettings(), true, backup);
    }
}
