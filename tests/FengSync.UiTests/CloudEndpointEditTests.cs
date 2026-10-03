using System.IO;
using FengSync.Core;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Editing an existing endpoint has to read the settings rclone actually holds and write them back in the
/// shape rclone expects. Two ways of getting that wrong are silent: an enforced-prefix S3 endpoint keeps
/// its credentials in the hidden backing remote rather than in the alias the endpoint list shows, and
/// rclone obscures a password field that is not already obscured — so writing back an obscured SFTP
/// password without declaring it would corrupt the stored password.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudEndpointEditTests
{
    [Fact]
    public async Task An_edit_target_reads_the_credentials_of_an_enforced_prefix_s3_endpoint()
    {
        await WithIsolatedShellAsync(async () =>
        {
            var values = new S3EndpointValues("docs", "Backblaze", "key-id", "secret-value", "us-west-004",
                "https://s3.us-west-004.backblazeb2.com", "bucket", "team/files", "Other");
            await CloudEndpointService.SaveS3Async("docs", values, replace: false);

            var account = Assert.Single(await CloudEndpointService.LoadEndpointAccountsAsync());
            var target = await CloudEndpointService.LoadEditTargetAsync(account);

            Assert.Equal(EndpointType.S3, target.Kind);
            Assert.Equal("docs", target.RemoteName);
            // The alias carries only its target; the credentials live in the backing remote.
            Assert.Equal("key-id", target.Parameter("access_key_id"));
            Assert.Equal("secret-value", target.Parameter("secret_access_key"));
            Assert.Equal("https://s3.us-west-004.backblazeb2.com", target.Parameter("endpoint"));
            // rclone's own value, not the display name Feng Sync shows: Backblaze is rclone's generic "Other".
            Assert.Equal("Other", target.Parameter("provider"));
            // The bucket and prefix are Feng Sync's metadata, because an alias target is not enumerable.
            Assert.Equal("bucket", target.Account.Metadata?.Bucket);
            Assert.Equal("team/files", target.Account.Metadata?.Subdirectory);
        });
    }

    [Fact]
    public async Task Saving_an_edited_sftp_endpoint_keeps_the_stored_password_intact()
    {
        await WithIsolatedShellAsync(async () =>
        {
            await CloudEndpointService.CreateRemoteAsync(EndpointType.Sftp, "nas",
                new Dictionary<string, string> { ["host"] = "nas.local", ["port"] = "2222", ["user"] = "bob", ["pass"] = "hunter2" });

            var stored = await StoredPasswordAsync();
            Assert.NotEmpty(stored);
            // rclone keeps the password obscured, which is why an edit form cannot show it.
            Assert.NotEqual("hunter2", stored);

            // What the editor does when the password box is left empty: re-send the stored obscured value
            // and declare it as such.
            var kept = CloudEndpointService.ResolveStoredPassword("", stored);
            Assert.True(kept.AlreadyObscured);
            await SaveSftpAsync(kept.Value, kept.AlreadyObscured);

            // Byte-identical. Obscuring it a second time would store a different string and break the login.
            Assert.Equal(stored, await StoredPasswordAsync());
            Assert.Equal("changed.local", await StoredParameterAsync("host"));

            // A typed password is plaintext, so the write must obscure it.
            var typed = CloudEndpointService.ResolveStoredPassword("new-secret", stored);
            Assert.False(typed.AlreadyObscured);
            await SaveSftpAsync(typed.Value, typed.AlreadyObscured);

            var rewritten = await StoredPasswordAsync();
            Assert.NotEqual(stored, rewritten);
            Assert.NotEqual("new-secret", rewritten);
        });
    }

    private static async Task SaveSftpAsync(string password, bool alreadyObscured) =>
        await CloudEndpointService.CreateRemoteAsync(EndpointType.Sftp, "nas",
            new Dictionary<string, string> { ["host"] = "changed.local", ["port"] = "2222", ["user"] = "bob", ["pass"] = password },
            alreadyObscured);

    private static async Task<string> StoredPasswordAsync() => await StoredParameterAsync("pass");

    private static async Task<string> StoredParameterAsync(string key)
    {
        var account = Assert.Single(await CloudEndpointService.LoadEndpointAccountsAsync());
        return (await CloudEndpointService.LoadEditTargetAsync(account)).Parameter(key);
    }

    /// <summary>
    /// Redirects every durable path — rclone.conf and cloud-endpoints.json — so the test never touches the
    /// real application data, and installs a shell whose rclone daemon is real but owned by the test.
    /// </summary>
    private static async Task WithIsolatedShellAsync(Func<Task> body)
    {
        var dataRoot = NewDirectory();
        var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        var previousHost = AppServices.TryCurrent;
        StubShell? host = null;
        try
        {
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", dataRoot);
            host = new StubShell();
            AppServices.Install(host);
            await body();
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            AppServices.Install(previousHost);
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
            TryDelete(dataRoot);
        }
    }

    /// <summary>A shell host with a real rclone daemon but no WPF Application.</summary>
    private sealed class StubShell : IAppServiceHost, IAsyncDisposable
    {
        private readonly RcloneLifecycleHost _rclone = new();

        public StubShell() => MountService = new RcloneMountService(_rclone);

        public RcloneLifecycleHost RcloneHost => _rclone;
        public IRcloneLifecycleClient RcloneLifecycle => _rclone;
        public RcloneMountService MountService { get; }
        public CloudTransferQueue CloudTransfers { get; } = new();
        public ValueTask DisposeAsync() => _rclone.DisposeAsync();
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-cloud-edit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
