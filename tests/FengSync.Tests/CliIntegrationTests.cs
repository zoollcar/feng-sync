using System.Diagnostics;
using System.Text.Json;
using FengSync.Core;

namespace FengSync.Tests;

public sealed class CliIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-cli-" + Guid.NewGuid().ToString("N"));
    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");
    private string Data => Path.Combine(_root, "appdata");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Left);
        Directory.CreateDirectory(Right);
        Directory.CreateDirectory(Data);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Compare_and_run_are_black_box_json_commands_that_sync_files()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "proof.txt"), "from-cli");
        var profileFile = Path.Combine(_root, "profile.fengsync.json");
        var profile = SyncProfile.Create("CLI integration", Left, Right) with { Mode = SyncMode.Update };
        await File.WriteAllTextAsync(profileFile, JsonSerializer.Serialize(profile));

        var compare = await RunCliAsync("compare", "--profile", profileFile);
        Assert.Equal(0, compare.ExitCode);
        Assert.Equal("Success", compare.Json.RootElement.GetProperty("exitCode").GetString());
        Assert.Equal(1, compare.Json.RootElement.GetProperty("planned").GetInt32());

        var run = await RunCliAsync("run", "--profile", profileFile, "--non-interactive", "--json-log");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("Success", run.Json.RootElement.GetProperty("exitCode").GetString());
        Assert.Equal("from-cli", await File.ReadAllTextAsync(Path.Combine(Right, "proof.txt")));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(4, "unknown")]
    [InlineData(4, "compare")]
    [InlineData(4, "run", "--profile", "missing-profile-id")]
    public async Task Invalid_commands_return_one_json_error_and_configuration_exit_code(int expectedExitCode, params string[] arguments)
    {
        var result = await RunCliAsync(arguments);

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Json.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Malformed_profile_returns_configuration_error_without_leaking_file_contents()
    {
        var profileFile = Path.Combine(_root, "malformed.fengsync.json");
        const string secretMarker = "must-not-appear-in-cli-output";
        await File.WriteAllTextAsync(profileFile, "{\"secret\":\"" + secretMarker + "\"");

        var result = await RunCliAsync("compare", "--profile", profileFile);

        Assert.Equal(4, result.ExitCode);
        Assert.DoesNotContain(secretMarker, result.Json.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Compare_reports_unresolved_two_way_conflict_with_contract_exit_code()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "same.txt"), "left");
        await File.WriteAllTextAsync(Path.Combine(Right, "same.txt"), "right");
        var profileFile = Path.Combine(_root, "conflict.fengsync.json");
        await File.WriteAllTextAsync(profileFile, JsonSerializer.Serialize(SyncProfile.Create("CLI conflict", Left, Right)));

        var result = await RunCliAsync("compare", "--profile", profileFile);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("Conflict", result.Json.RootElement.GetProperty("exitCode").GetString());
        Assert.False(result.Json.RootElement.GetProperty("canExecute").GetBoolean());
    }

    private async Task<(int ExitCode, JsonDocument Json)> RunCliAsync(params string[] arguments)
    {
        var root = FindRepositoryRoot();
        var cli = Path.Combine(root, "src", "FengSync.Cli", "bin", BuildConfiguration, "net10.0", "FengSync.Cli.dll");
        Assert.True(File.Exists(cli), $"CLI build output was not found: {cli}");

        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["FENGSYNC_DATA_DIR"] = Data;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch FengSync.Cli.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var lines = stdout.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        try { return (process.ExitCode, JsonDocument.Parse(lines[0])); }
        catch (JsonException exception) { throw new Xunit.Sdk.XunitException($"CLI did not emit one JSON result.{Environment.NewLine}stdout: {stdout}{Environment.NewLine}stderr: {stderr}{Environment.NewLine}{exception}"); }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FengSync.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root containing FengSync.sln was not found.");
    }

    private static string BuildConfiguration => AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
}
