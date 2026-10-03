using System.IO;
using FengSync.Core;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Configuration;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// An S3 endpoint's prefix is enforced by an rclone <c>alias</c>, not by this application's UI. These tests
/// pin the three things that would silently turn the prefix back into a mere convention: the metadata rule
/// that an alias-backed endpoint browses from an empty root, the derived remote names, and — against the
/// bundled rclone — that no path can address a key outside the prefix.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class S3PrefixBoundaryTests
{
    [Fact]
    public void An_enforced_prefix_endpoint_browses_from_its_own_root()
    {
        var enforced = new CloudEndpointMetadata("docs", "alias", "Backblaze", "bucket", "team/files", "docs_s3");

        Assert.Equal("bucket/team/files", enforced.Prefix);
        // The alias root already is the prefix, so the browsing path must stay empty. Repeating the prefix
        // would address bucket/team/files/bucket/team/files, which rclone would report as not found.
        Assert.Equal("", enforced.RootPath);
        Assert.True(enforced.PrefixIsEnforced);

        var account = new CloudEndpointAccount(new RcloneAccount("docs", "alias"), enforced);
        Assert.Equal("", account.RootPath);
        Assert.Equal("bucket/team/files", account.Prefix);
        Assert.True(account.PrefixIsEnforced);
        Assert.Contains("/bucket/team/files", account.Display, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metadata_written_before_prefixes_were_enforced_still_loads()
    {
        // An existing cloud-endpoints.json has no AliasOf field at all. Losing that field's default would
        // silently strip the prefix off every endpoint the user already configured.
        var directory = NewDirectory();
        var path = Path.Combine(directory, "cloud-endpoints.json");
        try
        {
            await File.WriteAllTextAsync(path,
                """[{"RemoteName":"legacy","Type":"s3","Provider":"AWS","Bucket":"bucket","Subdirectory":"team"}]""");

            var restored = Assert.Single(await new CloudEndpointMetadataStore(path).LoadAsync()).Value;

            Assert.Equal("bucket/team", restored.Prefix);
            Assert.Equal("bucket/team", restored.RootPath);
            Assert.False(restored.PrefixIsEnforced);
        }
        finally { TryDelete(directory); }
    }

    [Fact]
    public void The_backing_remote_and_alias_target_are_derived_from_the_endpoint_name()
    {
        Assert.Equal("docs_s3", CloudEndpointService.BackingRemoteName("docs"));
        Assert.Equal("docs_s3:bucket/team", CloudEndpointService.AliasTarget("docs_s3", "bucket/team"));
        // Stray separators must not produce "docs_s3://bucket" or a trailing slash.
        Assert.Equal("docs_s3:bucket", CloudEndpointService.AliasTarget("docs_s3", "/bucket/"));
        // An empty prefix is a valid rclone target meaning "the whole backing remote".
        Assert.Equal("docs_s3:", CloudEndpointService.AliasTarget("docs_s3", ""));
        Assert.Equal("docs_s3:bucket", CloudEndpointService.AliasParameters("docs_s3", "bucket")["remote"]);
    }

    [Fact]
    public void An_alias_is_exposed_as_an_s3_endpoint() =>
        Assert.Equal(EndpointType.S3, CloudEndpointService.KindFromRcloneType("alias"));

    [Fact]
    public void Only_the_alias_of_an_enforced_prefix_endpoint_is_listed_as_an_endpoint()
    {
        var metadata = new Dictionary<string, CloudEndpointMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["docs"] = new("docs", "alias", "Backblaze", "bucket", "team", "docs_s3")
        };
        var types = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["docs"] = "alias",        // the endpoint the user sees
            ["docs_s3"] = "s3",        // its hidden backing store
            ["other_bucket"] = "s3",   // a plain endpoint created before prefixes were enforced
            ["nas"] = "sftp",
            ["handmade"] = "alias"     // not ours: its target could be anything, so its flavour is unknown
        };

        var listed = CloudEndpointService.SelectEndpointRemotes(types, metadata).Select(account => account.Name).ToArray();

        Assert.Equal(new[] { "docs", "nas", "other_bucket" }, listed);
    }

    [Fact]
    public void A_backing_store_whose_alias_is_gone_is_listed_again()
    {
        // Otherwise deleting an endpoint by hand would strand a credential-bearing remote in rclone.conf
        // that the endpoint list never shows again, leaving no way to delete it from the UI.
        var metadata = new Dictionary<string, CloudEndpointMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["docs"] = new("docs", "alias", "Backblaze", "bucket", "team", "docs_s3")
        };
        var types = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["docs_s3"] = "s3" };

        var listed = CloudEndpointService.SelectEndpointRemotes(types, metadata).Select(account => account.Name).ToArray();

        Assert.Equal(new[] { "docs_s3" }, listed);
    }

    [Fact]
    public async Task Saving_and_deleting_an_s3_endpoint_manages_the_alias_and_its_backing_remote()
    {
        var dataRoot = NewDirectory();
        var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        var previousHost = AppServices.TryCurrent;
        StubShell? host = null;
        try
        {
            // Redirect everything durable — rclone.conf and cloud-endpoints.json — so the test never
            // touches the real application data. Creating an S3 remote needs no network: rclone only
            // validates the credentials when the remote is first used.
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", dataRoot);
            host = new StubShell();
            AppServices.Install(host);
            var values = new S3EndpointValues("docs", "Backblaze", "key", "secret", "us-west-004",
                "https://s3.us-west-004.backblazeb2.com", "bucket", "team/files", "Other");

            await CloudEndpointService.SaveS3Async("docs", values, replace: false);

            // The alias carries the user's name and the prefix; the credentials stay in the backing remote.
            var config = await File.ReadAllTextAsync(Path.Combine(dataRoot, "rclone", "rclone.conf"));
            Assert.Contains("remote = docs_s3:bucket/team/files", config, StringComparison.Ordinal);
            Assert.DoesNotContain("secret_access_key",
                config[config.IndexOf("[docs]", StringComparison.Ordinal)..], StringComparison.Ordinal);

            // The endpoint list shows the alias only, and it browses from an empty root because the alias
            // root already is the prefix.
            var account = Assert.Single(await CloudEndpointService.LoadEndpointAccountsAsync());
            Assert.Equal("docs", account.Name);
            Assert.Equal("alias", account.Type);
            Assert.Equal("bucket/team/files", account.Prefix);
            Assert.Equal("", account.RootPath);
            Assert.True(account.PrefixIsEnforced);

            // Re-saving under the same name replaces instead of accumulating remotes.
            await CloudEndpointService.SaveS3Async("docs", values, replace: true);
            Assert.Equal(2, (await RemoteNamesAsync(host)).Count);

            // Deleting removes both, so no credential-bearing remote is stranded in rclone.conf.
            await CloudEndpointService.DeleteAsync("docs");
            Assert.Empty(await RemoteNamesAsync(host));
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            AppServices.Install(previousHost);
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
            TryDelete(dataRoot);
        }
    }

    private static async Task<IReadOnlyList<string>> RemoteNamesAsync(StubShell host) =>
        await new RcloneConfigurationClient(host.RcloneHost).ListRemoteNamesAsync();

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

    [Fact]
    public async Task An_alias_makes_the_prefix_a_boundary_rclone_enforces()
    {
        var root = NewDirectory();
        try
        {
            var tree = Path.Combine(root, "tree");
            Directory.CreateDirectory(Path.Combine(tree, "prefix", "sub"));
            Directory.CreateDirectory(Path.Combine(tree, "other"));
            File.WriteAllText(Path.Combine(tree, "prefix", "a.txt"), "a");
            File.WriteAllText(Path.Combine(tree, "prefix", "sub", "b.txt"), "b");
            File.WriteAllText(Path.Combine(tree, "other", "secret.txt"), "secret");

            await using var daemon = await RcloneDaemon.StartAsync(
                BundledRclone.ExecutablePath, Path.Combine(root, "rclone.conf"), CancellationToken.None);
            var client = new RcloneConfigurationClient(daemon.Client);
            var options = new RcloneConfigOptions(Obscure: true, NoOutput: true);
            // A local backend stands in for the object store: the boundary lives in the alias, not in the
            // backend, so this exercises exactly the enforcement a real S3 endpoint relies on.
            await client.CreateAsync("store", "local", new Dictionary<string, string>(), options, CancellationToken.None);
            var target = Path.Combine(tree, "prefix").Replace('\\', '/');
            await client.CreateAsync("scoped", "alias", CloudEndpointService.AliasParameters("store", target), options, CancellationToken.None);

            // Every path below the alias, files included. The sibling directory and its file must never
            // appear, and rclone reports the intermediate directory as well as its contents.
            var paths = await ListAllPathsAsync(daemon.Client, "scoped:", "", CancellationToken.None);
            Assert.Equal(new[] { "a.txt", "sub", "sub/b.txt" }, paths);

            // Nothing above the prefix resolves. rclone refuses both ".." and a sibling path outright
            // rather than clamping them into the prefix, so no path can reach outside it.
            await Assert.ThrowsAnyAsync<Exception>(() => ListAllPathsAsync(daemon.Client, "scoped:", "..", CancellationToken.None));
            await Assert.ThrowsAnyAsync<Exception>(() => ListAllPathsAsync(daemon.Client, "scoped:", "../other", CancellationToken.None));
            await Assert.ThrowsAnyAsync<Exception>(() => ListAllPathsAsync(daemon.Client, "scoped:", "/other", CancellationToken.None));

            // Writes are confined too. Against the bundled rclone a ".." upload is clamped into the prefix
            // (the file lands under prefix/other/), and refusing it outright would be equally acceptable —
            // what must never happen is a file appearing outside the prefix.
            var source = Path.Combine(root, "upload.txt");
            File.WriteAllText(source, "u");
            await CopyIntoAsync(daemon.Client, source, "scoped:", "uploaded.txt", CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(tree, "prefix", "uploaded.txt")));

            try { await CopyIntoAsync(daemon.Client, source, "scoped:", "../other/escaped.txt", CancellationToken.None); }
            catch (Exception) { }
            Assert.False(File.Exists(Path.Combine(tree, "other", "escaped.txt")));
        }
        finally { TryDelete(root); }
    }

    /// <summary>Every path rclone reports below <paramref name="path"/>, files included.</summary>
    private static async Task<string[]> ListAllPathsAsync(RcloneRcClient client, string fs, string path, CancellationToken ct)
    {
        var response = await client.CallAsync("operations/list", new { fs, remote = path, opt = new { recurse = true } }, ct);
        if (!response.TryGetProperty("list", out var list)) return [];
        return list.EnumerateArray()
            .Select(item => item.TryGetProperty("Path", out var value) ? value.GetString() ?? "" : "")
            .Where(value => value.Length > 0)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Uploads one local file into <paramref name="fs"/> at <paramref name="remotePath"/>.</summary>
    private static Task CopyIntoAsync(RcloneRcClient client, string localFile, string fs, string remotePath, CancellationToken ct) =>
        client.CallAsync("operations/copyfile", new
        {
            srcFs = Path.GetDirectoryName(localFile)!.Replace('\\', '/'),
            srcRemote = Path.GetFileName(localFile),
            dstFs = fs,
            dstRemote = remotePath
        }, ct);

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-s3-prefix-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
