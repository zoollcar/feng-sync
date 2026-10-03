using System.IO;
using System.Text.Json;
using FengSync.Core;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Configuration;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// Renaming an endpoint has no rclone operation behind it: the section in rclone.conf is rewritten, and the
/// two files that remember the endpoint by name are migrated. These tests cover the three ways that can go
/// wrong silently — a name that collides with a hidden backing store, a profile left pointing at a remote
/// that no longer exists, and a rewrite that a running daemon does not pick up.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudEndpointRenameTests
{
    // ---- name validation ------------------------------------------------------------------------------

    [Fact]
    public void A_blank_name_is_refused()
    {
        var context = new CloudEndpointRenameContext(["docs"], [], []);
        Assert.NotNull(CloudEndpointRename.Validate("   ", "docs", context));
    }

    [Fact]
    public void The_current_name_is_not_a_rename()
    {
        var context = new CloudEndpointRenameContext(["docs"], [], []);
        // Case-insensitively equal too: rclone would end up with two sections differing only in case.
        Assert.NotNull(CloudEndpointRename.Validate("docs", "docs", context));
        Assert.NotNull(CloudEndpointRename.Validate("DOCS", "docs", context));
    }

    [Fact]
    public void A_name_already_taken_by_an_endpoint_is_refused()
    {
        var context = new CloudEndpointRenameContext(["docs", "other"], [], []);
        Assert.NotNull(CloudEndpointRename.Validate("other", "docs", context));
    }

    [Fact]
    public void A_name_taken_by_a_hidden_backing_store_is_refused()
    {
        // "other_s3" is not an endpoint, so the endpoint list never shows it — but taking the name would
        // overwrite the storage another endpoint reads.
        var context = new CloudEndpointRenameContext(["docs", "other", "other_s3"], [], []);
        Assert.NotNull(CloudEndpointRename.Validate("other", "docs", context));
    }

    [Fact]
    public void A_new_name_whose_own_backing_store_is_taken_is_refused()
    {
        var context = new CloudEndpointRenameContext(["docs", "archive_s3"], [], []);
        Assert.NotNull(CloudEndpointRename.Validate("archive", "docs", context));
    }

    [Fact]
    public void A_name_that_only_metadata_still_knows_is_refused()
    {
        // The remote can be gone from rclone.conf while the record survives; reusing the name would inherit
        // the old endpoint's prefix and provider.
        var context = new CloudEndpointRenameContext(["docs"], ["stale"], []);
        Assert.NotNull(CloudEndpointRename.Validate("stale", "docs", context));
    }

    [Fact]
    public void A_mounted_endpoint_cannot_be_renamed()
    {
        // A live mount holds the filesystem string "<name>:", resolved when it was mounted. The rename has to
        // wait, or the mount silently points at nothing on its next remount.
        var context = new CloudEndpointRenameContext(["docs"], [], ["docs"]);
        var error = CloudEndpointRename.Validate("archive", "docs", context);
        Assert.NotNull(error);
        Assert.Contains("取消挂载", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_free_name_is_accepted()
    {
        var context = new CloudEndpointRenameContext(["docs", "other"], ["other"], []);
        Assert.Null(CloudEndpointRename.Validate("archive", "docs", context));
    }

    // ---- profile migration ----------------------------------------------------------------------------

    [Fact]
    public void Every_scheme_a_profile_can_use_is_repointed()
    {
        foreach (var scheme in EndpointSchemes.AllSchemes)
        {
            Assert.Equal($"{scheme}://archive/team/files",
                EndpointSchemes.RenameRemote($"{scheme}://docs/team/files", "docs", "archive"));
        }
    }

    [Fact]
    public void A_remote_name_that_merely_starts_with_the_old_one_is_left_alone()
    {
        // Prefix matching would rewrite an unrelated endpoint and quietly send a sync task to the wrong place.
        Assert.Equal("sftp://docs2/team", EndpointSchemes.RenameRemote("sftp://docs2/team", "docs", "archive"));
        Assert.Equal("sftp://docs-archive/", EndpointSchemes.RenameRemote("sftp://docs-archive/", "docs", "archive"));
    }

    [Fact]
    public void A_local_path_and_a_nameless_uri_are_left_alone()
    {
        Assert.Equal(@"C:\docs\team", EndpointSchemes.RenameRemote(@"C:\docs\team", "docs", "archive"));
        Assert.Equal(@"\\server\share\docs", EndpointSchemes.RenameRemote(@"\\server\share\docs", "docs", "archive"));
        Assert.Equal("sftp://", EndpointSchemes.RenameRemote("sftp://", "docs", "archive"));
        Assert.Equal("", EndpointSchemes.RenameRemote("", "docs", "archive"));
    }

    [Fact]
    public void A_profile_on_the_renamed_endpoint_keeps_its_path_and_the_others_are_untouched()
    {
        var profiles = new List<SyncProfile>
        {
            SyncProfile.Create("moved", "sftp://docs/team/files", @"C:\local\docs"),
            SyncProfile.Create("elsewhere", "s3://other/", "gdrive://third/root"),
            SyncProfile.Create("plain", @"C:\a", @"C:\b")
        };

        var rewritten = CloudEndpointRename.RewriteProfiles(profiles, "docs", "archive");

        Assert.Equal("sftp://archive/team/files", rewritten[0].LeftPath);
        Assert.Equal(@"C:\local\docs", rewritten[0].RightPath);
        Assert.Equal("s3://other/", rewritten[1].LeftPath);
        Assert.Equal("gdrive://third/root", rewritten[1].RightPath);
        Assert.Equal(@"C:\a", rewritten[2].LeftPath);
        // Only the profile that actually moved counts, so the status line does not overstate the change.
        Assert.Single(rewritten.Where((profile, index) => profile != profiles[index]));
    }

    // ---- end to end, against the bundled rclone -------------------------------------------------------

    [Fact]
    public async Task Renaming_an_sftp_endpoint_keeps_the_obscured_password_and_moves_the_profiles()
    {
        await WithRealShellAsync(async () =>
        {
            await CloudEndpointService.CreateRemoteAsync(EndpointType.Sftp, "nas",
                new Dictionary<string, string> { ["host"] = "nas.local", ["port"] = "2222", ["user"] = "bob", ["pass"] = "hunter2" });
            await CloudEndpointService.SaveMetadataAsync("nas", EndpointType.Sftp, "team/docs");
            var before = await StoredParametersAsync("nas");
            // rclone stores the password obscured, so the copy has to be byte-exact rather than re-obscured.
            Assert.NotEqual("hunter2", before["pass"]);

            var profiles = new ProfileStore();
            await profiles.SaveAsync(
            [
                SyncProfile.Create("docs", "sftp://nas/team/docs", @"C:\local\docs"),
                SyncProfile.Create("other", "sftp://other/team", @"C:\local\other")
            ]);

            var result = await CloudEndpointRename.RenameAsync("nas", "archive");

            Assert.Equal("nas", result.OldName);
            Assert.Equal("archive", result.NewName);
            Assert.Equal(1, result.RewrittenProfiles);

            var after = await StoredParametersAsync("archive");
            Assert.Equal(before["pass"], after["pass"]);
            Assert.Equal(before["host"], after["host"]);
            Assert.Equal(before["user"], after["user"]);

            var names = await RemoteNamesAsync();
            Assert.Contains("archive", names);
            Assert.DoesNotContain("nas", names);

            var account = Assert.Single(await CloudEndpointService.LoadEndpointAccountsAsync());
            Assert.Equal("archive", account.Name);
            Assert.Equal("team/docs", account.Prefix);

            var loaded = await profiles.LoadAsync();
            Assert.Contains(loaded, profile => profile.LeftPath == "sftp://archive/team/docs");
            Assert.Contains(loaded, profile => profile.LeftPath == "sftp://other/team");
        });
    }

    [Fact]
    public async Task Renaming_an_enforced_prefix_s3_endpoint_moves_the_hidden_backing_store_too()
    {
        await WithRealShellAsync(async () =>
        {
            var values = new S3EndpointValues("docs", "Backblaze", "key-id", "secret-value", "us-west-004",
                "https://s3.us-west-004.backblazeb2.com", "bucket", "team/files", "Other");
            await CloudEndpointService.SaveS3Async("docs", values, replace: false);
            var before = await StoredParametersAsync("docs_s3");

            var profiles = new ProfileStore();
            await profiles.SaveAsync([SyncProfile.Create("docs", "s3://docs", @"C:\local\docs")]);

            await CloudEndpointRename.RenameAsync("docs", "archive");

            // Both halves move: the credentials keep living in the backing store, and the alias — which is the
            // endpoint the UI shows — has to be repointed at the copy.
            var account = Assert.Single(await CloudEndpointService.LoadEndpointAccountsAsync());
            Assert.Equal("archive", account.Name);
            Assert.Equal("alias", account.Type);
            Assert.Equal("archive_s3", account.Metadata?.AliasOf);
            Assert.Equal("bucket/team/files", account.Prefix);
            Assert.Equal("", account.RootPath);
            Assert.True(account.PrefixIsEnforced);

            var after = await StoredParametersAsync("archive_s3");
            Assert.Equal(before["access_key_id"], after["access_key_id"]);
            Assert.Equal(before["secret_access_key"], after["secret_access_key"]);
            Assert.Equal(before["provider"], after["provider"]);
            Assert.Equal(before["endpoint"], after["endpoint"]);

            var alias = await StoredParametersAsync("archive");
            Assert.Equal("archive_s3:bucket/team/files", alias["remote"]);

            var names = await RemoteNamesAsync();
            Assert.DoesNotContain("docs", names);
            Assert.DoesNotContain("docs_s3", names);

            Assert.Equal("s3://archive", (await profiles.LoadAsync()).Single().LeftPath);
        });
    }

    // ---- rollback -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_rename_the_daemon_does_not_confirm_puts_all_three_files_back()
    {
        var dataRoot = NewDirectory();
        var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        var previousHost = AppServices.TryCurrent;
        try
        {
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", dataRoot);
            // The daemon answers for the old name but never for the new one, which is what a rewrite rclone
            // failed to pick up would look like.
            AppServices.Install(new ScriptedShell(new ScriptedRclone((operation, payload) => operation switch
            {
                "config/listremotes" => Json("""{"remotes":["docs","other"]}"""),
                "config/get" when Argument(payload, "name") == "docs" =>
                    Json("""{"type":"sftp","host":"nas.local","user":"bob","pass":"2Wq9Q3mZ8tLpQ0vR"}"""),
                _ => Json("{}")
            })));

            var configPath = BundledRclone.ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            const string config = "[docs]\ntype = sftp\nhost = nas.local\nuser = bob\npass = 2Wq9Q3mZ8tLpQ0vR\n\n[other]\ntype = s3\n";
            await File.WriteAllTextAsync(configPath, config);

            var metadata = new CloudEndpointMetadataStore();
            await metadata.UpsertAsync(new CloudEndpointMetadata("docs", "sftp", "SFTP", "team/docs", ""));
            var metadataBefore = await File.ReadAllTextAsync(metadata.FilePath);

            var profiles = new ProfileStore();
            await profiles.SaveAsync([SyncProfile.Create("docs", "sftp://docs/team/docs", @"C:\local\docs")]);
            var profilesBefore = await File.ReadAllTextAsync(profiles.FilePath);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CloudEndpointRename.RenameAsync("docs", "archive"));

            Assert.Contains("已还原", error.Message, StringComparison.Ordinal);
            // All three go back together: a config that still has the old section, next to a profile that
            // already points at the new name, is worse than no rename at all.
            Assert.Equal(config, await File.ReadAllTextAsync(configPath));
            Assert.Equal(metadataBefore, await File.ReadAllTextAsync(metadata.FilePath));
            Assert.Equal(profilesBefore, await File.ReadAllTextAsync(profiles.FilePath));
        }
        finally
        {
            AppServices.Install(previousHost);
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
            TryDelete(dataRoot);
        }
    }

    [Fact]
    public async Task A_rename_that_never_reached_the_config_leaves_every_file_alone()
    {
        var dataRoot = NewDirectory();
        var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        var previousHost = AppServices.TryCurrent;
        try
        {
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", dataRoot);
            AppServices.Install(new ScriptedShell(new ScriptedRclone((operation, _) => operation switch
            {
                "config/listremotes" => Json("""{"remotes":["docs","other"]}"""),
                "config/get" => Json("""{"type":"sftp","host":"nas.local"}"""),
                _ => Json("{}")
            })));

            var configPath = BundledRclone.ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            const string config = "[docs]\ntype = sftp\nhost = nas.local\n\n[other]\ntype = s3\n";
            await File.WriteAllTextAsync(configPath, config);

            // "other" already exists, so validation refuses before anything is written.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CloudEndpointRename.RenameAsync("docs", "other"));

            Assert.Contains("已存在", error.Message, StringComparison.Ordinal);
            Assert.Equal(config, await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            AppServices.Install(previousHost);
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
            TryDelete(dataRoot);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static async Task<IReadOnlyDictionary<string, string>> StoredParametersAsync(string name) =>
        await new RcloneConfigurationClient(AppServices.Current.RcloneLifecycle).GetRemoteParametersAsync(name);

    private static async Task<IReadOnlyList<string>> RemoteNamesAsync() =>
        await new RcloneConfigurationClient(AppServices.Current.RcloneLifecycle).ListRemoteNamesAsync();

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static string? Argument(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Redirects every durable path — rclone.conf, cloud-endpoints.json and profiles.json — so the test never
    /// touches the real application data, and installs a shell whose rclone daemon is real but owned by the test.
    /// </summary>
    private static async Task WithRealShellAsync(Func<Task> body)
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

    /// <summary>A shell whose rclone answers from a script, so a failure can be placed at one exact step.</summary>
    private sealed class ScriptedShell(IRcloneLifecycleClient client) : IAppServiceHost, IAsyncDisposable
    {
        private readonly RcloneLifecycleHost _unused = new();

        public RcloneLifecycleHost RcloneHost => _unused;
        public IRcloneLifecycleClient RcloneLifecycle { get; } = client;
        public RcloneMountService MountService { get; } = new(client);
        public CloudTransferQueue CloudTransfers { get; } = new();
        public ValueTask DisposeAsync() => _unused.DisposeAsync();
    }

    private sealed class ScriptedRclone(Func<string, JsonElement, JsonElement?> handler) : IRcloneLifecycleClient
    {
        public Task<JsonElement> CallAsync(string operation, object payload, CancellationToken cancellationToken = default)
        {
            var arguments = JsonSerializer.SerializeToElement(payload);
            return Task.FromResult(handler(operation, arguments) ?? Json("{}"));
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-cloud-rename-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
