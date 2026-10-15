using FengSync.Core.Configuration;
using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers the three choices the LAN transfer window writes to the settings file, and the one number two
/// components have to agree on without being able to see each other.
/// </summary>
/// <remarks>
/// The port is the interesting one. It is a firewall rule name, a QR-code suffix, and something a person may
/// have to type, so a settings default that drifted from the listener's default would be a feature that works
/// until the day it does not — and no test would say which of the two was wrong.
/// </remarks>
public sealed class LanSettingsTests
{
    [Fact]
    public void The_default_port_is_the_number_the_listener_would_bind()
    {
        Assert.Equal(LanOptions.DefaultPort, new ApplicationSettings().LanTransferPort);
    }

    [Fact]
    public void The_validator_bounds_are_the_ones_the_listener_accepts()
    {
        // Core cannot reference the listener, so these two numbers are kept honest here rather than by a
        // compile-time link: the validator refuses exactly what LanOptions.Validate refuses.
        Assert.DoesNotContain(ConfigurationValidator.Validate(new ApplicationSettings { LanTransferPort = LanOptions.MinimumPort }),
            error => error.Contains("局域网快传"));
        Assert.DoesNotContain(ConfigurationValidator.Validate(new ApplicationSettings { LanTransferPort = LanOptions.MaximumPort }),
            error => error.Contains("局域网快传"));
        Assert.Contains(ConfigurationValidator.Validate(new ApplicationSettings { LanTransferPort = LanOptions.MinimumPort - 1 }),
            error => error.Contains("局域网快传"));
        Assert.Contains(ConfigurationValidator.Validate(new ApplicationSettings { LanTransferPort = LanOptions.MaximumPort + 1 }),
            error => error.Contains("局域网快传"));
    }

    [Fact]
    public void A_default_installation_is_a_valid_one()
    {
        // The cheap fact that catches a new setting whose default its own validator refuses — a settings file
        // nobody has edited would then fail to save, and the error would name something else entirely.
        Assert.Empty(ConfigurationValidator.Validate(new ApplicationSettings()));
    }

    [Fact]
    public async Task A_settings_file_that_predates_the_feature_loads_with_the_feature_at_its_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fengsync-lan-{Guid.NewGuid():N}.json");
        try
        {
            // A real file from before this feature: a schema this store accepts, and no key that mentions a
            // listener. An absent choice must read as the choice the product makes, not as 0 or as false.
            await File.WriteAllTextAsync(path,
                $"{{\"SchemaVersion\":{ConfigurationMigrator.CurrentSchemaVersion},\"DefaultMaxConcurrentCopies\":5}}");

            var settings = (await new SettingsStore(path).LoadAsync()).Settings;

            Assert.Equal(5, settings.DefaultMaxConcurrentCopies);
            Assert.Equal(LanOptions.DefaultPort, settings.LanTransferPort);
            Assert.Null(settings.LanReceiveDirectory);
            Assert.True(settings.LanAutoAcceptFromTrusted);
            Assert.Empty(ConfigurationValidator.Validate(settings));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file);
        }
    }

    [Fact]
    public async Task Every_lan_choice_written_by_the_window_is_read_back_by_the_next_one()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fengsync-lan-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(path);
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), $"fengsync-lan-dir-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var written = new ApplicationSettings
            {
                LanTransferPort = 60125,
                LanReceiveDirectory = directory,
                LanAutoAcceptFromTrusted = false,
            };
            await store.SaveAsync(written);

            var read = (await store.LoadAsync()).Settings;
            Assert.Equal(written.LanTransferPort, read.LanTransferPort);
            Assert.Equal(directory, read.LanReceiveDirectory);
            Assert.False(read.LanAutoAcceptFromTrusted);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file);
        }
    }
}
