using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace FengSync.UiTests;

public sealed class UiAcceptanceTests
{
    private readonly ITestOutputHelper _output;
    public UiAcceptanceTests(ITestOutputHelper output) => _output = output;
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
    public Task Main_window_shell_keeps_the_profile_workspace_and_toolbar_geometry() => RunAsync("ui-shell");

    [Fact]
    [Trait("Category", "UI")]
    public Task Main_window_shell_is_visible_with_default_native_rendering() => RunAsync("ui-shell-native");

    [Fact]
    [Trait("Category", "UI")]
    public Task Main_window_shell_is_visible_with_forced_software_rendering() => RunAsync("ui-shell-software");

    [Fact]
    [Trait("Category", "UI")]
    public Task Main_window_visual_matrix_captures_supported_sizes_without_clipping_or_toolbar_overlap() => RunAsync("ui-visual-matrix");

    [Fact]
    [Trait("Category", "UI")]
    public Task Fluent_settings_persist_update_preferences_and_sidebar_width() => RunAsync("update-settings");

    [Fact]
    [Trait("Category", "UI")]
    public Task About_window_displays_the_built_product_version() => RunAsync("about");

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

    [Fact]
    [Trait("Category", "UI")]
    [Trait("Category", "External")]
    public Task Google_drive_compare_and_sync_covers_flat_10_100_files_and_100_folders_in_each_mode()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FENGSYNC_INCLUDE_GOOGLE_DRIVE_VOLUME"), "1", StringComparison.Ordinal))
            return Task.CompletedTask;
        return RunAsync("gdrive-volume");
    }

    private async Task RunAsync(string scenario)
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "tests", "FengSync.UiTests", "Scripts", "Invoke-UiScenario.ps1");
        // The WPF project is referenced by this test project, so the matching
        // configuration's executable is copied beside the test assembly.
        var app = Path.Combine(AppContext.BaseDirectory, "FengSync.exe");
        if (!File.Exists(script)) throw new FileNotFoundException("UI test scenario script was not found.", script);
        if (!File.Exists(app)) throw new FileNotFoundException("Feng Sync must be built before UI tests run.", app);

        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(script);
        start.ArgumentList.Add("-Scenario"); start.ArgumentList.Add(scenario);
        start.ArgumentList.Add("-AppPath"); start.ArgumentList.Add(app);
        start.ArgumentList.Add("-Workspace"); start.ArgumentList.Add(root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start PowerShell UI test host.");
        var timer = Stopwatch.StartNew();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        // The regular scenarios should finish in minutes. The opt-in Drive
        // matrix performs nine real remote workloads and therefore gets a
        // scenario-level emergency brake large enough not to drive progression.
        var timeoutSeconds = scenario == "gdrive-volume" ? 2 * 60 * 60 : 15 * 60;
        var exited = process.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))) != exited)
        {
            await TerminateProcessTreeAsync(process);
            var timedOutOutput = (await stdout) + Environment.NewLine + (await stderr);
            _output.WriteLine($"UI scenario={scenario}; timeout={timeoutSeconds}s; elapsed={timer.Elapsed}; stdout/stderr:{Environment.NewLine}{timedOutOutput}");
            throw new TimeoutException($"UI scenario '{scenario}' exceeded {timeoutSeconds}s. Output:{Environment.NewLine}{timedOutOutput}");
        }
        timer.Stop();
        var output = (await stdout) + Environment.NewLine + (await stderr);
        _output.WriteLine($"UI scenario={scenario}; exit={process.ExitCode}; elapsed={timer.Elapsed}; stdout/stderr:{Environment.NewLine}{output}");
        if (process.ExitCode == 77 && output.Contains("SKIPPED:", StringComparison.Ordinal))
            throw SkipException.ForSkip(output.Trim());
        Assert.True(process.ExitCode == 0, $"UI scenario '{scenario}' failed (exit {process.ExitCode}).{Environment.NewLine}{output}");
    }

    private static async Task TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* it exited between the check and kill */ }
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { /* disposed/exited process */ }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FengSync.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root containing FengSync.sln was not found.");
    }
}
