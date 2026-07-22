using System.Diagnostics;

namespace FengSync.Tests;

public sealed class SftpProtocolIntegrationTests
{
    [Fact]
    [Trait("Category", "Protocol")]
    [Trait("Category", "Integration")]
    public async Task Cli_round_trips_a_unicode_file_through_the_real_pinned_sftp_host()
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "tests", "FengSync.Tests", "Scripts", "Invoke-SftpProtocolIntegration.ps1");
        var cli = Path.Combine(root, "src", "FengSync.Cli", "bin", BuildConfiguration, "net10.0", "FengSync.Cli.exe");
        Assert.True(File.Exists(script), $"SFTP protocol script was not found: {script}");
        Assert.True(File.Exists(cli), $"CLI build output was not found: {cli}");

        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(script);
        start.ArgumentList.Add("-Workspace"); start.ArgumentList.Add(root); start.ArgumentList.Add("-CliPath"); start.ArgumentList.Add(cli);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start SFTP protocol test host.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"Real SFTP protocol integration failed (exit {process.ExitCode}).{Environment.NewLine}{await stdout}{Environment.NewLine}{await stderr}");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FengSync.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root containing FengSync.sln was not found.");
    }

    private static string BuildConfiguration => AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
}
