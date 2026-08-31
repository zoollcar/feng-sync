using System.Diagnostics;

namespace FengSync.Tests.Mount;

public sealed class RealWinFspMountIntegrationTests
{
    [Fact]
    [Trait("Category", "External")]
    [Trait("Category", "WinFsp")]
    public async Task Bundled_rclone_mounts_reads_and_unmounts_a_real_WinFsp_directory()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FENGSYNC_TEST_REAL_MOUNT"), "1", StringComparison.Ordinal))
            return;

        var root = Path.Combine(Path.GetTempPath(), "fengsync-real-mount-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var mount = Path.Combine(root, "mount");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "proof.txt"), "mounted-through-winfsp");
        Process? process = null;
        try
        {
            var rclone = Path.Combine(FindRepositoryRoot(), "src", "FengSync", "Assets", "rclone", "rclone.exe");
            var start = new ProcessStartInfo(rclone)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("mount");
            start.ArgumentList.Add(source);
            start.ArgumentList.Add(mount);
            start.ArgumentList.Add("--vfs-cache-mode");
            start.ArgumentList.Add("writes");
            process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start rclone mount.");

            var visible = Path.Combine(mount, "proof.txt");
            await WaitUntilAsync(() => File.Exists(visible) || process.HasExited, TimeSpan.FromSeconds(20));
            if (process.HasExited)
                throw new Xunit.Sdk.XunitException("rclone mount exited early: " + await process.StandardError.ReadToEndAsync());
            Assert.Equal("mounted-through-winfsp", await File.ReadAllTextAsync(visible));
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            await WaitUntilAsync(() => !Directory.Exists(mount) || !File.Exists(Path.Combine(mount, "proof.txt")), TimeSpan.FromSeconds(10));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(100);
        Assert.True(condition(), "The real WinFsp mount did not reach the expected state before timeout.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FengSync.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root containing FengSync.sln was not found.");
    }
}
