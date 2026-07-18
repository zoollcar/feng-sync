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
    public async Task<SettingsLoadResult> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return new(new ApplicationSettings());
        try
        {
            ApplicationSettings settings;
            await using (var stream = File.OpenRead(_path))
                settings = await JsonSerializer.DeserializeAsync<ApplicationSettings>(stream, cancellationToken: ct) ?? new ApplicationSettings();
            var originalSchemaVersion = settings.SchemaVersion;
            settings = new ConfigurationMigrator().Migrate(settings);
            if (originalSchemaVersion == settings.SchemaVersion) return new(settings);

            // A migration is an on-disk change: retain the source before replacing it so the
            // user can inspect/recover legacy settings even if a later release regresses.
            var backup = _path + $".schema-v{originalSchemaVersion}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak";
            File.Copy(_path, backup, overwrite: false);
            await SaveAsync(settings, ct);
            return new(settings, Migrated: true, MigratedFromSchemaVersion: originalSchemaVersion, MigrationBackupPath: backup);
        }
        catch (JsonException)
        {
            var backup = _path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak";
            File.Move(_path, backup, true);
            return new(new ApplicationSettings(), true, backup);
        }
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
}
