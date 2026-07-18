using FengSync.Core;
using FengSync.Core.Capabilities;
using FengSync.Core.Configuration;

namespace FengSync.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Migrator_upgrades_legacy_schema_without_losing_defaults()
    {
        var migrated = new ConfigurationMigrator().Migrate(new ApplicationSettings { SchemaVersion = 1, DefaultMaxConcurrentCopies = 5 });
        Assert.Equal(ConfigurationMigrator.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(5, migrated.DefaultMaxConcurrentCopies);
    }

    [Fact]
    public void Effective_settings_merge_profile_overrides_without_mutating_defaults()
    {
        var defaults = new ApplicationSettings { DefaultMaxConcurrentCopies = 4, DefaultVerifyCopies = true, DefaultFilter = new SyncFilter(Exclude: ["*.tmp"]) };
        var profile = SyncProfile.Create("P", "left", "right") with { Settings = new ProfileSettings(MaxConcurrentCopies: 2, VerifyCopies: false) };

        var effective = EffectiveProfileSettings.Resolve(profile, defaults);

        Assert.Equal(2, effective.MaxConcurrentCopies);
        Assert.False(effective.VerifyCopies);
        Assert.Equal(["*.tmp"], effective.Filter.Exclude);
        Assert.Equal(4, defaults.DefaultMaxConcurrentCopies);
    }

    [Fact]
    public void Effective_settings_uses_application_time_tolerance_when_profile_inherits_it()
    {
        var defaults = new ApplicationSettings { DefaultTimeToleranceSeconds = 11 };

        var effective = EffectiveProfileSettings.Resolve(SyncProfile.Create("P", "left", "right"), defaults);

        Assert.Equal(11, effective.TimeToleranceSeconds);
    }

    [Fact]
    public void Effective_settings_keeps_profile_time_tolerance_override()
    {
        var profile = SyncProfile.Create("P", "left", "right") with { Settings = new ProfileSettings(TimeToleranceSeconds: 7) };

        Assert.Equal(7, EffectiveProfileSettings.Resolve(profile, new ApplicationSettings { DefaultTimeToleranceSeconds = 11 }).TimeToleranceSeconds);
    }

    [Fact]
    public void Configuration_validator_reports_invalid_global_settings_ranges()
    {
        var errors = ConfigurationValidator.Validate(new ApplicationSettings
        {
            DefaultTimeToleranceSeconds = -1,
            LogRetentionDays = 0,
            NetworkRetryCount = -1
        });

        Assert.Contains(errors, error => error.Contains("时间容差"));
        Assert.Contains(errors, error => error.Contains("日志保留"));
        Assert.Contains(errors, error => error.Contains("重试次数"));
    }

    [Fact]
    public async Task Settings_store_keeps_backup_and_recovers_from_corrupt_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fengsync-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            await store.SaveAsync(new ApplicationSettings { DefaultMaxConcurrentCopies = 5 });
            await File.WriteAllTextAsync(path, "not json");

            var result = await store.LoadAsync();

            Assert.True(result.RecoveredFromCorruption);
            Assert.Equal(3, result.Settings.DefaultMaxConcurrentCopies);
            Assert.NotNull(result.BackupPath);
            Assert.True(File.Exists(result.BackupPath!));
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Fact]
    public async Task Settings_store_backs_up_and_persists_a_migrated_legacy_schema()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fengsync-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"SchemaVersion\":1,\"DefaultMaxConcurrentCopies\":5}");

            var result = await new SettingsStore(path).LoadAsync();

            Assert.True(result.Migrated);
            Assert.Equal(1, result.MigratedFromSchemaVersion);
            Assert.NotNull(result.MigrationBackupPath);
            Assert.True(File.Exists(result.MigrationBackupPath!));
            Assert.Contains("\"SchemaVersion\":1", await File.ReadAllTextAsync(result.MigrationBackupPath!));
            Assert.Equal(ConfigurationMigrator.CurrentSchemaVersion, result.Settings.SchemaVersion);
            Assert.Equal(ConfigurationMigrator.CurrentSchemaVersion, (await new SettingsStore(path).LoadAsync()).Settings.SchemaVersion);
        }
        finally { foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Theory]
    [InlineData("local", "local", SyncMode.Custom)]
    public void Unsupported_profiles_are_blocked(string left, string right, SyncMode mode)
    {
        var result = new FeatureCapabilityService().Evaluate(SyncProfile.Create("P", left, right) with { Mode = mode });

        Assert.False(result.CanRun);
        Assert.NotEmpty(result.Blockers);
    }

    [Fact]
    public void Profile_validator_rejects_same_endpoint_and_invalid_archive()
    {
        var profile = SyncProfile.Create("P", "C:\\work", "C:\\work") with
        {
            Settings = new ProfileSettings(Versioning: new VersioningPolicy(VersioningMode.TimestampedArchive, "C:\\work\\archive"))
        };

        Assert.False(ProfileValidator.Validate(profile).IsValid);
    }

    [Theory]
    [InlineData("sftp://host:not-a-port/docs")]
    [InlineData("sftp://")]
    public void Profile_validator_rejects_invalid_remote_endpoint(string endpoint)
    {
        var profile = SyncProfile.Create("P", endpoint, "C:\\right");

        Assert.False(ProfileValidator.Validate(profile).IsValid);
    }

    [Fact]
    public void Profile_validator_rejects_unimplemented_remote_protocols()
    {
        var profile = SyncProfile.Create("P", "https://example.test/files", "C:\\right");

        Assert.False(ProfileValidator.Validate(profile).IsValid);
    }

    [Fact]
    public void Profile_validator_rejects_archive_equal_to_an_endpoint()
    {
        var profile = SyncProfile.Create("P", "C:\\work", "C:\\right") with
        {
            Settings = new ProfileSettings(Versioning: new VersioningPolicy(VersioningMode.TimestampedArchive, "C:\\work"))
        };

        Assert.False(ProfileValidator.Validate(profile).IsValid);
    }

    [Fact]
    public async Task Profile_store_update_rejects_a_conflicting_name_without_overwriting_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fengsync-profiles-{Guid.NewGuid():N}.json");
        try
        {
            var first = SyncProfile.Create("first", "C:\\left-a", "C:\\right-a");
            var second = SyncProfile.Create("second", "C:\\left-b", "C:\\right-b");
            var store = new ProfileStore(path);
            await store.SaveAsync([first, second]);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(second with { Name = "FIRST" }));

            Assert.Equal(["first", "second"], (await store.LoadAsync()).Select(x => x.Name));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
