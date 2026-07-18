namespace FengSync.Core.Configuration;

/// <summary>Explicit, forward-only settings schema migration boundary.</summary>
public sealed class ConfigurationMigrator
{
    public const int CurrentSchemaVersion = 2;

    public ApplicationSettings Migrate(ApplicationSettings settings)
    {
        if (settings.SchemaVersion is < 1 or > CurrentSchemaVersion)
            throw new InvalidOperationException($"不支持的程序设置版本：{settings.SchemaVersion}。");
        // Schema 2 reserves the split defaults model. Legacy settings already deserialize into
        // the equivalent typed defaults, so migration only records the durable version marker.
        return settings with { SchemaVersion = CurrentSchemaVersion };
    }
}
