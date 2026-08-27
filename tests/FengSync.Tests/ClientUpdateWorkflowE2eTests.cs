using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using FengSync.Core.Updates;

namespace FengSync.Tests;

/// <summary>
/// Client-side release hand-off tests. They never contact GitHub, start WPF, or
/// use a real profile/settings directory: the HTTP transport and updater launch
/// are both fakes and every file is below a random temporary portable layout.
/// </summary>
public sealed class ClientUpdateWorkflowE2eTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FengSync-client-update-tests", Guid.NewGuid().ToString("N"));
    public ClientUpdateWorkflowE2eTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Fake_release_downloads_validates_and_hands_off_with_exact_updater_arguments()
    {
        var layout = await CreateLayoutAsync();
        var zip = await CreatePackageAsync(layout.Payload, validManifest: true);
        var launches = new List<ProcessStartInfo>();
        var handoff = await CreateWorkflow(zip, launches).DownloadValidateAndLaunchAsync(Release(zip), layout.Executable, layout.Installation, "1.0.0");

        Assert.Equal(layout.Installation, handoff.InstallationDirectory);
        Assert.True(File.Exists(handoff.DetachedUpdaterPath));
        Assert.Single(launches);
        var args = launches[0].ArgumentList.ToArray();
        Assert.Equal(new[] { "--wait-pid", "--staging", "--installation", "--executable", "--old-manifest", "--new-manifest", "--from-version", "--to-version" }, args.Where(x => x.StartsWith("--", StringComparison.Ordinal)).ToArray());
        Assert.Contains(layout.Installation, args); Assert.Contains(layout.Executable, args); Assert.Contains("1.0.0", args); Assert.Contains("2.0.0", args);
        Assert.Equal("old profile", await File.ReadAllTextAsync(Path.Combine(layout.UserData, "profiles.json")));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "my-notes.txt")));
        Directory.Delete(handoff.TaskDirectory, true);
    }

    [Fact]
    public async Task Bad_hash_is_rejected_before_extraction_or_updater_launch()
    {
        var layout = await CreateLayoutAsync(); var zip = await CreatePackageAsync(layout.Payload, validManifest: true);
        var launches = new List<ProcessStartInfo>();
        var workflow = new UpdateInstallWorkflow(new HttpClient(new ReleaseHandler(zip, new string('0', 64))), start => { launches.Add(start); return new Process(); });

        await Assert.ThrowsAsync<InvalidDataException>(() => workflow.DownloadValidateAndLaunchAsync(Release(zip), layout.Executable, layout.Installation, "1.0.0"));
        Assert.Empty(launches);
        Assert.Equal("old executable", await File.ReadAllTextAsync(layout.Executable));
    }

    [Fact]
    public async Task Invalid_manifest_is_rejected_before_updater_launch_and_installation_is_unchanged()
    {
        var layout = await CreateLayoutAsync(); var zip = await CreatePackageAsync(layout.Payload, validManifest: false);
        var launches = new List<ProcessStartInfo>();

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateWorkflow(zip, launches).DownloadValidateAndLaunchAsync(Release(zip), layout.Executable, layout.Installation, "1.0.0"));
        Assert.Empty(launches);
        Assert.Equal("old executable", await File.ReadAllTextAsync(layout.Executable));
        Assert.Equal("old manifest", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "release-manifest.json")));
    }

    [Fact]
    public async Task Detached_updater_copy_or_start_failure_never_exits_or_modifies_installation()
    {
        var layout = await CreateLayoutAsync(); var zip = await CreatePackageAsync(layout.Payload, validManifest: true);
        string? failedTaskDirectory = null;
        var copyFailure = new UpdateInstallWorkflow(new HttpClient(new ReleaseHandler(zip)), _ => new Process(), (_, destination) =>
        {
            failedTaskDirectory = Path.GetDirectoryName(destination);
            File.WriteAllText(destination, "partial updater");
            throw new IOException("copy injected");
        });
        await Assert.ThrowsAsync<IOException>(() => copyFailure.DownloadValidateAndLaunchAsync(Release(zip), layout.Executable, layout.Installation, "1.0.0"));
        Assert.NotNull(failedTaskDirectory);
        Assert.False(Directory.Exists(failedTaskDirectory));

        string? failedLaunchTaskDirectory = null;
        var startFailure = new UpdateInstallWorkflow(new HttpClient(new ReleaseHandler(zip)), start =>
        {
            failedLaunchTaskDirectory = start.WorkingDirectory;
            return null;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => startFailure.DownloadValidateAndLaunchAsync(Release(zip), layout.Executable, layout.Installation, "1.0.0"));
        Assert.NotNull(failedLaunchTaskDirectory);
        Assert.False(Directory.Exists(failedLaunchTaskDirectory));
        Assert.Equal("old executable", await File.ReadAllTextAsync(layout.Executable));
        Assert.Equal("old manifest", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "release-manifest.json")));
    }

    [Fact]
    public async Task Unsafe_or_unavailable_installation_is_rejected_without_any_http_request()
    {
        var layout = await CreateLayoutAsync(); var calls = 0;
        var workflow = new UpdateInstallWorkflow(new HttpClient(new DelegateHandler(_ => { calls++; throw new InvalidOperationException("HTTP must not run"); })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.DownloadValidateAndLaunchAsync(Release(Array.Empty<byte>()), layout.Executable, Path.Combine(_root, "different-base"), "1.0.0"));
        Assert.Equal(0, calls);

        var unavailable = Path.Combine(_root, "missing", "FengSync.exe");
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.DownloadValidateAndLaunchAsync(Release(Array.Empty<byte>()), unavailable, Path.GetDirectoryName(unavailable)!, "1.0.0"));
        Assert.Equal(0, calls);
    }

    private UpdateInstallWorkflow CreateWorkflow(byte[] zip, List<ProcessStartInfo> launches) => new(new HttpClient(new ReleaseHandler(zip)), start => { launches.Add(start); return new Process(); });
    private static GitHubReleaseInfo Release(byte[] zip) => new("test", "v2.0.0", "", new Uri("https://github.com/example/release"), new Uri("https://example.test/package.zip"), new Uri("https://example.test/package.zip.sha256"), zip.Length, null);

    private async Task<Layout> CreateLayoutAsync()
    {
        var installation = Path.Combine(_root, Guid.NewGuid().ToString("N"), "portable"); var payload = Path.Combine(_root, Guid.NewGuid().ToString("N"), "payload"); var userData = Path.Combine(_root, Guid.NewGuid().ToString("N"), "user-data");
        Directory.CreateDirectory(installation); Directory.CreateDirectory(payload); Directory.CreateDirectory(userData);
        var executable = Path.Combine(installation, "FengSync.exe"); await File.WriteAllTextAsync(executable, "old executable"); await File.WriteAllTextAsync(Path.Combine(installation, "release-manifest.json"), "old manifest"); await File.WriteAllTextAsync(Path.Combine(installation, "my-notes.txt"), "keep me"); await File.WriteAllTextAsync(Path.Combine(userData, "profiles.json"), "old profile");
        await File.WriteAllTextAsync(Path.Combine(payload, "FengSync.exe"), "new executable"); await File.WriteAllTextAsync(Path.Combine(payload, "FengSync.Updater.exe"), "updater bytes");
        return new(installation, payload, userData, executable);
    }

    private async Task<byte[]> CreatePackageAsync(string payload, bool validManifest)
    {
        var files = Directory.GetFiles(payload).Select(path => new ReleaseManifestFile(Path.GetFileName(path), new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant())).OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
        await new ReleaseManifest("FengSync", validManifest ? "2.0.0" : "9.9.9", "win-x64", files).SaveAsync(Path.Combine(payload, "release-manifest.json"));
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip"); ZipFile.CreateFromDirectory(payload, zip); return await File.ReadAllBytesAsync(zip);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    private sealed record Layout(string Installation, string Payload, string UserData, string Executable);
    private sealed class ReleaseHandler(byte[] zip, string? checksum = null) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { var content = request.RequestUri!.AbsolutePath.EndsWith("sha256", StringComparison.Ordinal) ? new StringContent((checksum ?? Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()) + "  package.zip") : new ByteArrayContent(zip); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); } }
    private sealed class DelegateHandler(Action<HttpRequestMessage> action) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { action(request); throw new UnreachableException(); } }
}
