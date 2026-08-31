using System.Diagnostics;
using FengSync.Core;
using FengSync.Core.Rclone.Transport;

namespace FengSync.Tests.Rclone;

public sealed class WindowsProxyIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Actual_WinInet_state_is_propagated_to_a_real_rclone_child_without_exposing_credentials()
    {
        var root = FindRepositoryRoot();
        var rclone = Path.Combine(root, "src", "FengSync", "Assets", "rclone", "rclone.exe");
        Assert.True(File.Exists(rclone), $"Bundled rclone was not found: {rclone}");
        var registry = new WindowsRegistryProxyReader().Read();
        var start = new ProcessStartInfo(rclone, "version")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var resolved = RcloneEnvironment.Prepare(start, winInet: new WindowsRegistryProxyReader());
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch bundled rclone.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("rclone", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("netdns=go", start.Environment["GODEBUG"]);
        Assert.Equal(resolved.Source.ToString(), start.Environment["FENGSYNC_RCLONE_PROXY_SOURCE"]);
        Assert.Contains("127.0.0.1", start.Environment["NO_PROXY"] ?? "");
        Assert.DoesNotContain(registry.ProxyServer ?? "__none__", stdout + stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FengSync.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root containing FengSync.sln was not found.");
    }
}
