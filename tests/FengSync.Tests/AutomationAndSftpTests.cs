using FengSync.Core;
using FengSync.Core.Automation;
using FengSync.Core.SftpServer;

namespace FengSync.Tests;

public sealed class AutomationAndSftpTests
{
    [Fact]
    public async Task Task_scheduler_never_puts_secrets_in_command()
    {
        var calls = new List<string>();
        var scheduler = new WindowsTaskSchedulerService((file, arguments, _) => { calls.Add(file + " " + arguments); return Task.FromResult(new ScheduledProcessResult(0, "", "")); });
        await scheduler.CreateOrReplaceAsync(new ScheduledProfileTask("FengSync-test", "profile-42", "FengSync.Cli.exe", "daily", "secret=must-not-appear"));
        Assert.Contains("--profile profile-42", Assert.Single(calls));
        Assert.DoesNotContain("secret=must-not-appear", calls[0]);
    }

    [Fact]
    public void Enabled_sftp_requires_existing_root_username_and_password()
    {
        Assert.Throws<InvalidOperationException>(() => new SftpServerOptions(Enabled: true).Validate());
        var root = Path.Combine(Path.GetTempPath(), "fengsync-sftp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new SftpServerOptions(Enabled: true, RootPath: root, UserName: "alice").Validate());
            new SftpServerOptions(Enabled: true, RootPath: root, UserName: "alice", PasswordConfigured: true).Validate();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Sftp_settings_store_round_trips_non_secret_configuration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fengsync-sftp-settings-" + Guid.NewGuid().ToString("N")); var path = Path.Combine(directory, "sftp-server.json"); Directory.CreateDirectory(directory);
        try
        {
            var store = new SftpServerSettingsStore(path);
            await store.SaveAsync(new SftpServerOptions(true, true, "127.0.0.1", 2223, directory, "alice", CacheMaxSizeBytes: 2L * 1024 * 1024 * 1024, PasswordConfigured: true));
            var restored = await store.LoadAsync();
            Assert.Equal(directory, restored.RootPath); Assert.Equal("alice", restored.UserName); Assert.True(restored.PasswordConfigured);
            Assert.DoesNotContain("not-in-plaintext", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Password_store_encrypts_password_outside_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fengsync-sftp-password-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "secret.dat");
        try { var store = new SftpPasswordStore(path); await store.SaveAsync("not-in-plaintext"); Assert.Equal("not-in-plaintext", await store.LoadAsync()); Assert.DoesNotContain("not-in-plaintext", await File.ReadAllTextAsync(path)); }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Password_store_preserves_the_previous_password_when_atomic_replace_fails()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fengsync-sftp-password-atomic-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "secret.dat");
        try
        {
            var store = new SftpPasswordStore(path);
            await store.SaveAsync("previous-password");
            await using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync("replacement-password"));
            Assert.Equal("previous-password", await store.LoadAsync());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Password_store_clears_ciphertext_that_dpapi_cannot_decrypt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fengsync-sftp-password-corrupt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "secret.dat");
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            var store = new SftpPasswordStore(path);
            Assert.Null(await store.LoadAsync());
            Assert.False(store.HasPassword);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Legacy_sftp_configuration_is_deleted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fengsync-sftp-legacy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var path = Path.Combine(directory, "sftp-server.json");
        try { await File.WriteAllTextAsync(path, "{\"SchemaVersion\":3,\"Accounts\":[]}"); var store = new SftpServerSettingsStore(path); await store.LoadAsync(); Assert.True(store.LegacyConfigurationRemoved); Assert.False(File.Exists(path)); }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Host_key_store_uses_a_stable_private_path_and_fingerprint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fengsync-host-key-" + Guid.NewGuid().ToString("N"));
        try { var store = new SftpHostKeyStore(directory); var first = store.GetKeyReference(); var second = store.GetKeyReference(); Assert.True(File.Exists(first.Path)); Assert.Equal(first, second); }
        finally { Directory.Delete(directory, true); }
    }
}
