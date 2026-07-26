namespace FengSync.Core.Configuration;

/// <summary>Explicit, forward-only settings schema migration boundary.</summary>
public sealed class ConfigurationMigrator
{
    public const int CurrentSchemaVersion = 3;

    public ApplicationSettings Migrate(ApplicationSettings settings)
    {
        if (settings.SchemaVersion is < 1 or > CurrentSchemaVersion)
            throw new InvalidOperationException($"不支持的程序设置版本：{settings.SchemaVersion}。");
        // Schema 3 adds a nullable-on-disk-in-practice sidebar width. Older files deserialize
        // the record default; invalid values are normalized by the shell on load.
        return settings with { SchemaVersion = CurrentSchemaVersion };
    }
}
