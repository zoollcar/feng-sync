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
    public void Migrator_upgrades_schema_two_with_update_defaults()
    {
        var migrated = new ConfigurationMigrator().Migrate(new ApplicationSettings { SchemaVersion = 2 });
        Assert.Equal(3, migrated.SchemaVersion);
        Assert.True(migrated.AutoCheckForUpdates);
        Assert.Null(migrated.LastUpdateCheckUtc);
    }

    [Fact]
    public void Validator_requires_sidebar_width_and_valid_skipped_version()
    {
        Assert.NotEmpty(ConfigurationValidator.Validate(new ApplicationSettings { MainWindowSidebarWidth = 400 }));
        Assert.NotEmpty(ConfigurationValidator.Validate(new ApplicationSettings { SkippedUpdateVersion = "bad" }));
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
    public void Profile_validator_accepts_an_s3_remote_endpoint()
    {
        var profile = SyncProfile.Create("P", "s3://archive-bucket/backups", "C:\\right");

        Assert.True(ProfileValidator.Validate(profile).IsValid);
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

    [Fact]
    public void Resume_threshold_defaults_to_five_hundred_megabytes()
    {
        // The product default: files at or above 500 MiB are staged under a deterministic name so an
        // interrupted transfer can continue; smaller ones use rclone's faster multi-stream copy.
        Assert.Equal(500L * 1024 * 1024, new ApplicationSettings().DefaultResumeMinimumBytes);
    }

    [Fact]
    public void Effective_settings_prefers_the_profile_resume_threshold_over_the_application_default()
    {
        var defaults = new ApplicationSettings { DefaultResumeMinimumBytes = 500L * 1024 * 1024 };
        var profile = SyncProfile.Create("P", "left", "right") with { Settings = new ProfileSettings(ResumeMinimumBytes: 1024) };

        Assert.Equal(1024, EffectiveProfileSettings.Resolve(profile, defaults).ResumeMinimumBytes);
    }

    [Fact]
    public void Effective_settings_inherits_the_application_resume_threshold_when_the_profile_does_not_set_one()
    {
        var defaults = new ApplicationSettings { DefaultResumeMinimumBytes = 7 };

        Assert.Equal(7, EffectiveProfileSettings.Resolve(SyncProfile.Create("P", "left", "right"), defaults).ResumeMinimumBytes);
    }

    [Fact]
    public void Validator_accepts_zero_but_rejects_a_negative_resume_threshold()
    {
        // Zero is meaningful ("continue every file"), so it must not be treated as "unset".
        Assert.Empty(ConfigurationValidator.Validate(new ApplicationSettings { DefaultResumeMinimumBytes = 0 }));
        Assert.NotEmpty(ConfigurationValidator.Validate(new ApplicationSettings { DefaultResumeMinimumBytes = -1 }));
    }

    [Theory]
    [InlineData("500M", 500L * 1024 * 1024)]
    [InlineData("500MB", 500L * 1024 * 1024)]
    [InlineData("500MiB", 500L * 1024 * 1024)]
    [InlineData("500m", 500L * 1024 * 1024)]
    [InlineData("2 GB", 2L * 1024 * 1024 * 1024)]
    [InlineData("1.5T", (long)(1.5 * 1024 * 1024 * 1024 * 1024))]
    [InlineData("1K", 1024)]
    [InlineData("1024", 1024)]
    [InlineData("0", 0)]
    public void Byte_size_parses_the_forms_the_settings_windows_accept(string text, long expected)
    {
        Assert.True(ByteSize.TryParse(text, out var bytes), text);
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("500X")]
    [InlineData("-1")]
    [InlineData("1e30")]
    public void Byte_size_rejects_input_it_cannot_scale(string? text)
    {
        Assert.False(ByteSize.TryParse(text, out var bytes));
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Byte_size_format_round_trips_through_parse()
    {
        // The settings window writes Format() and reads TryParse(), so every value it can emit has to
        // survive the trip unchanged.
        foreach (var value in new long[] { 0, 1, 1024, 500L * 1024 * 1024, 1024L * 1024 * 1024 * 1024, 1L << 40 })
        {
            var text = ByteSize.Format(value);
            Assert.True(ByteSize.TryParse(text, out var parsed), text);
            Assert.Equal(value, parsed);
        }
    }

    [Theory]
    [InlineData(500L * 1024 * 1024, "500M")]
    [InlineData(2L * 1024 * 1024 * 1024, "2G")]
    [InlineData(1024L, "1K")]
    [InlineData(4096L, "4K")]
    [InlineData(1L, "1")]
    [InlineData(0L, "0")]
    public void Byte_size_is_shown_with_the_largest_exact_unit(long bytes, string expected)
    {
        // Round-tripping alone would not catch Format() degrading to a raw byte count, which is what
        // the settings boxes actually display.
        Assert.Equal(expected, ByteSize.Format(bytes));
    }
}
