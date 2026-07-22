using System.Diagnostics;
using Xunit;
using Xunit.Sdk;

namespace FengSync.UiTests;

public sealed class UiAcceptanceTests
{
    [Fact]
    [Trait("Category", "UI")]
    public Task Local_folders_support_two_way_sync_and_manual_direction_override() => RunAsync("local");

    [Fact]
    [Trait("Category", "UI")]
    public Task Sync_modes_change_the_result_as_configured() => RunAsync("modes");

    [Fact]
    [Trait("Category", "UI")]
    public Task Per_file_selection_can_exclude_an_item_before_sync() => RunAsync("selection");

    [Fact]
    [Trait("Category", "UI")]
    public Task Sftp_source_syncs_to_a_local_folder() => RunAsync("sftp-to-local");

    [Fact]
    [Trait("Category", "UI")]
    public Task Sftp_endpoint_is_created_and_selected_through_the_real_endpoint_ui() => RunAsync("sftp-ui");

    [Fact]
    [Trait("Category", "UI")]
    public Task Profile_lifecycle_persists_edited_endpoints_and_rejects_cancelled_changes() => RunAsync("profile");

    [Fact]
    [Trait("Category", "UI")]
    public Task Profile_filter_configuration_controls_the_real_sync_result() => RunAsync("profile-filter");

    [Fact]
    [Trait("Category", "UI")]
    public Task Profile_delete_threshold_requires_its_name_before_a_mirror_can_proceed() => RunAsync("delete-threshold");

    [Fact]
    [Trait("Category", "UI")]
    public Task Application_settings_apply_and_persist_across_a_real_ui_reopen() => RunAsync("settings");

    [Fact]
    [Trait("Category", "UI")]
    public Task Operations_menu_exposes_the_record_created_by_a_real_sync() => RunAsync("history");

    [Fact]
    [Trait("Category", "UI")]
    public Task Schedule_ui_creates_tests_and_deletes_a_unique_windows_task() => RunAsync("schedule");

    [Fact]
    [Trait("Category", "UI")]
    [Trait("Category", "External")]
    public Task Google_drive_test_directory_syncs_to_a_local_folder() => RunAsync("gdrive");

    // The SFTP server settings dialog has specialized host lifecycle setup and
    // remains a dedicated compatibility scenario until it is folded into the
    // unified host. Profile editing is already covered by the unified `profile`
    // lifecycle scenario above.
    [Fact]
    [Trait("Category", "UI")]
    public Task Sftp_server_settings_can_start_and_stop_the_real_host() => RunCompatibilityAsync("Run-SftpServerSettingsAcceptance.ps1");

    private static async Task RunAsync(string scenario)
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "tests", "FengSync.UiTests", "Scripts", "Invoke-UiScenario.ps1");
        // The WPF project is referenced by this test project, so the matching
        // configuration's executable is copied beside the test assembly.
        var app = Path.Combine(AppContext.BaseDirectory, "FengSync.exe");
        if (!File.Exists(script)) throw new FileNotFoundException("UI test scenario script was not found.", script);
        if (!File.Exists(app)) throw new FileNotFoundException("Feng Sync must be built before UI tests run.", app);

        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(script);
        start.ArgumentList.Add("-Scenario"); start.ArgumentList.Add(scenario);
        start.ArgumentList.Add("-AppPath"); start.ArgumentList.Add(app);
        start.ArgumentList.Add("-Workspace"); start.ArgumentList.Add(root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start PowerShell UI test host.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout) + Environment.NewLine + (await stderr);
        if (process.ExitCode == 77 && output.Contains("SKIPPED:", StringComparison.Ordinal))
            throw SkipException.ForSkip(output.Trim());
        Assert.True(process.ExitCode == 0, $"UI scenario '{scenario}' failed (exit {process.ExitCode}).{Environment.NewLine}{output}");
    }

    private static async Task RunCompatibilityAsync(string scriptName, bool skipBuild = false)
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "tests", "gui", scriptName);
        if (!File.Exists(script)) throw new FileNotFoundException("UI compatibility scenario script was not found.", script);
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(script);
        if (skipBuild) start.ArgumentList.Add("-SkipBuild");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start UI compatibility scenario host.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"UI compatibility scenario '{scriptName}' failed (exit {process.ExitCode}).{Environment.NewLine}{await stdout}{Environment.NewLine}{await stderr}");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FengSync.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root containing FengSync.sln was not found.");
    }
}
