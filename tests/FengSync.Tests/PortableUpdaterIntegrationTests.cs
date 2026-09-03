using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FengSync.Core.Updates;

namespace FengSync.Tests;

/// <summary>Runs the standalone updater only under its explicitly allowed temp test root.</summary>
public sealed class PortableUpdaterIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FengSync-updater-tests", Guid.NewGuid().ToString("N"));
    public PortableUpdaterIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Updater_replaces_manifest_files_preserves_unknown_files_and_confirms_success()
    {
        var layout = await CreateLayoutAsync();
        var userData = Path.Combine(_root, "isolated-user-data"); Directory.CreateDirectory(userData);
        await File.WriteAllTextAsync(Path.Combine(userData, "FengSync.local.json"), "{\"AutoCheckForUpdates\":false}");
        await File.WriteAllTextAsync(Path.Combine(userData, "profiles.json"), "[{\"Name\":\"e2e profile\"}]");
        File.WriteAllText(Path.Combine(layout.Task, "success"), "confirmed");
        var exit = await RunUpdaterAsync(layout, null);
        Assert.True(exit == 0, File.Exists(Path.Combine(layout.Task, "FengSync-update-error.log")) ? await File.ReadAllTextAsync(Path.Combine(layout.Task, "FengSync-update-error.log")) : "no updater log");
        Assert.Equal("new-one", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "one.bin")));
        Assert.Equal("new-two", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "two.bin")));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "my-notes.txt")));
        Assert.False(Directory.Exists(layout.Staging));
        Assert.False(Directory.Exists(Path.Combine(layout.Task, "backup")));
        Assert.Contains("2.0.0", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "release-manifest.json")));
        Assert.Contains("false", await File.ReadAllTextAsync(Path.Combine(userData, "FengSync.local.json")));
        Assert.Contains("e2e profile", await File.ReadAllTextAsync(Path.Combine(userData, "profiles.json")));
    }

    [Fact]
    public async Task Updater_accepts_unsorted_legacy_manifest_and_preserves_unknown_files()
    {
        var layout = await CreateLayoutAsync();
        var old = await ReleaseManifest.LoadAsync(layout.OldManifest);
        await (old with { Files = old.Files.Reverse().ToArray() }).SaveAsync(layout.OldManifest);
        await File.WriteAllTextAsync(Path.Combine(layout.Task, "success"), "confirmed");

        Assert.Equal(0, await RunUpdaterAsync(layout, null));
        Assert.Equal("new-one", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "one.bin")));
        Assert.Equal("new-two", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "two.bin")));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "my-notes.txt")));
    }

    [Theory]
    [InlineData("unsafe-old")]
    [InlineData("duplicate-old")]
    [InlineData("unsorted-new")]
    public async Task Legacy_order_compatibility_retains_manifest_safety_checks(string invalidKind)
    {
        var layout = await CreateLayoutAsync();
        var path = invalidKind == "unsorted-new" ? layout.NewManifest : layout.OldManifest;
        var manifest = await ReleaseManifest.LoadAsync(path);
        var files = manifest.Files.Reverse().ToArray();
        if (invalidKind == "unsafe-old") files[1] = files[1] with { Path = "../outside.bin" };
        if (invalidKind == "duplicate-old") files[1] = files[0];
        await (manifest with { Files = files }).SaveAsync(path);

        Assert.Equal(11, await RunUpdaterAsync(layout, null));
        Assert.Equal("old-one", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "one.bin")));
        Assert.Equal("old-two", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "two.bin")));
        Assert.False(Directory.Exists(Path.Combine(layout.Task, "backup")));
    }

    [Fact]
    public async Task Updater_copy_failure_rolls_back_and_log_does_not_expose_credentials()
    {
        var layout = await CreateLayoutAsync();
        var exit = await RunUpdaterAsync(layout, 2);
        Assert.True(exit == 14, File.Exists(Path.Combine(layout.Task, "FengSync-update-error.log")) ? await File.ReadAllTextAsync(Path.Combine(layout.Task, "FengSync-update-error.log")) : "no updater log");
        Assert.Equal("old-one", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "one.bin")));
        Assert.Equal("old-two", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "two.bin")));
        Assert.Contains("1.0.0", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "release-manifest.json")));
        Assert.True(Directory.Exists(Path.Combine(layout.Task, "backup")));
        var log = await File.ReadAllTextAsync(Path.Combine(layout.Task, "FengSync-update-error.log"));
        Assert.DoesNotContain("secret-password", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Updater_reports_new_application_start_failure_without_claiming_success()
    {
        var layout = await CreateLayoutAsync();
        // Stage an invalid replacement EXE while retaining the valid old EXE for
        // backup. The updater must surface a restart failure after replacement.
        await File.WriteAllTextAsync(Path.Combine(layout.Staging, "FengSync.exe"), "not an executable");
        await WriteManifestAsync(layout.Staging, "2.0.0", ["one.bin", "two.bin", "FengSync.exe"]);
        var exit = await RunUpdaterAsync(layout, null);
        var log = await File.ReadAllTextAsync(Path.Combine(layout.Task, "FengSync-update-error.log"));
        Assert.True(exit == 16, $"exit={exit}; log={log}");
        Assert.Contains("new-start-failed", log);
        Assert.False(File.Exists(Path.Combine(layout.Task, "success")));
    }

    [Fact]
    public async Task Updater_rejects_tampered_staging_payload_before_touching_installation()
    {
        var layout = await CreateLayoutAsync();
        await File.WriteAllTextAsync(Path.Combine(layout.Staging, "two.bin"), "attacker-modified");

        var exit = await RunUpdaterAsync(layout, null);

        Assert.Equal(11, exit);
        Assert.Equal("old-one", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "one.bin")));
        Assert.Equal("old-two", await File.ReadAllTextAsync(Path.Combine(layout.Installation, "two.bin")));
        var log = await File.ReadAllTextAsync(Path.Combine(layout.Task, "FengSync-update-error.log"));
        Assert.Contains("new-manifest-invalid-or-tampered", log);
    }

    private async Task<Layout> CreateLayoutAsync()
    {
        var installation = Path.Combine(_root, "installation"); var task = Path.Combine(_root, "task"); var staging = Path.Combine(task, "staging");
        Directory.CreateDirectory(installation); Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(installation, "one.bin"), "old-one");
        await File.WriteAllTextAsync(Path.Combine(installation, "two.bin"), "old-two");
        await File.WriteAllTextAsync(Path.Combine(installation, "my-notes.txt"), "keep me");
        await File.WriteAllTextAsync(Path.Combine(staging, "one.bin"), "new-one");
        await File.WriteAllTextAsync(Path.Combine(staging, "two.bin"), "new-two");
        var oldManifest = await WriteManifestAsync(installation, "1.0.0", ["one.bin", "two.bin"]);
        var newManifest = await WriteManifestAsync(staging, "2.0.0", ["one.bin", "two.bin"]);
        // Reuse the updater's WinExe host as an inert restart target.  It rejects the
        // application's arguments and exits without opening a console, unlike cmd.exe
        // (which leaves an interactive shell open for every integration-test run).
        var executable = Path.Combine(installation, "FengSync.exe"); File.Copy(FindUpdater(), executable);
        return new(installation, task, staging, oldManifest, newManifest, executable);
    }

    private static async Task<string> WriteManifestAsync(string directory, string version, IEnumerable<string> files)
    {
        var entries = new List<object>();
        foreach (var file in files.OrderBy(x => x, StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, file));
            entries.Add(new { path = file, size = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
        }
        var path = Path.Combine(directory, "release-manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { product = "FengSync", version, runtime = "win-x64", files = entries }));
        return path;
    }

    private static async Task<int> RunUpdaterAsync(Layout layout, int? failAfter)
    {
        var updater = FindUpdater();
        var start = new ProcessStartInfo(updater) { UseShellExecute = false };
        foreach (var value in new[] { "--wait-pid", "-1", "--staging", layout.Staging, "--installation", layout.Installation, "--executable", layout.Executable, "--old-manifest", layout.OldManifest, "--new-manifest", layout.NewManifest, "--from-version", "1.0.0", "--to-version", "2.0.0" }) start.ArgumentList.Add(value);
        start.Environment["FENGSYNC_UPDATER_TEST_ROOT"] = Path.GetDirectoryName(layout.Installation)!;
        if (failAfter is not null) start.Environment["FENGSYNC_UPDATER_FAIL_AFTER_FILE_COUNT"] = failAfter.Value.ToString();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start updater.");
        await process.WaitForExitAsync(); return process.ExitCode;
    }

    private static string FindUpdater()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "FengSync.Updater", "bin", "Debug", "net10.0-windows", "win-x64", "FengSync.Updater.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Build FengSync.Updater before its integration tests.");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    private sealed record Layout(string Installation, string Task, string Staging, string OldManifest, string NewManifest, string Executable);
}
