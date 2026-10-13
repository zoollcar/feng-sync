using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace FengSync.UiTests;

[Collection(UiAcceptanceCollection.Name)]
public sealed class UiAcceptanceTests
{
    private readonly ITestOutputHelper _output;
    public UiAcceptanceTests(ITestOutputHelper output) => _output = output;
    private static bool OnlineServicesEnabled => string.Equals(Environment.GetEnvironmentVariable("FENGSYNC_TEST_ONLINE_SERVICES"), "1", StringComparison.Ordinal);
    /// <summary>
    /// Off by default. A scenario normally runs on a <see cref="PrivateDesktop"/>: a display space of its
    /// own, where the application opens ordinary windows and the harness can raise them, move the pointer
    /// and take the foreground without any of it reaching the user's session. Set
    /// <c>FENGSYNC_UI_SHOW_ON_SCREEN=1</c> (or pass <c>-ShowOnScreen</c> to <c>scripts/Test-All.ps1</c>) to
    /// run a scenario here, on the visible desktop, and watch it.
    /// </summary>
    private static bool ShowOnScreen => string.Equals(Environment.GetEnvironmentVariable("FENGSYNC_UI_SHOW_ON_SCREEN"), "1", StringComparison.Ordinal);
    [Fact]
    [Trait("Category", "UI")]
    public Task Local_folders_support_two_way_sync_and_manual_direction_override() => RunAsync("local");

    [Fact]
    [Trait("Category", "UI")]
    public Task Local_file_move_is_visible_in_comparison_and_sync_progress() => RunAsync("local-move");

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
    public Task Built_in_sftp_service_starts_and_stops_through_the_settings_ui() => RunAsync("sftp-service");

    [Fact]
    [Trait("Category", "UI")]
    public Task Standalone_transfer_window_opens_from_the_cloud_manager_and_starts_empty() => RunAsync("cloud-transfers");

    [Fact]
    [Trait("Category", "UI")]
    public Task Batch_window_runs_a_saved_profile_and_reports_its_summary() => RunAsync("batch-run");

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
    [Trait("Category", "External")]
    public Task Google_drive_test_directory_syncs_to_a_local_folder() => OnlineServicesEnabled ? RunAsync("gdrive") : Task.CompletedTask;

    [Fact]
    [Trait("Category", "UI")]
    [Trait("Category", "External")]
    public Task Google_drive_syncs_ten_files_in_each_mode()
    {
        if (!OnlineServicesEnabled)
            return Task.CompletedTask;
        return RunAsync("gdrive-volume");
    }

    [Fact]
    [Trait("Category", "UI")]
    [Trait("Category", "External")]
    public Task Cloudflare_r2_round_trips_through_the_s3_endpoint()
    {
        if (!OnlineServicesEnabled)
            return Task.CompletedTask;
        return RunAsync("r2");
    }

    [Fact]
    [Trait("Category", "UI")]
    [Trait("Category", "External")]
    public Task Cloudflare_r2_syncs_ten_files_in_each_mode()
    {
        if (!OnlineServicesEnabled)
            return Task.CompletedTask;
        return RunAsync("r2-volume");
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

        var arguments = new List<string> { "-NoProfile", "-File", script, "-Scenario", scenario, "-AppPath", app, "-Workspace", root };
        if (ShowOnScreen) arguments.Add("-ShowOnScreen");

        // One switch decides where the scenario runs, and nothing else does. The default is a private
        // desktop, where the scenario is free to raise windows, move the pointer and take the foreground —
        // all of it lands on a desktop the user is not looking at. Children inherit that desktop, so the
        // application, rclone and the CLI stay there too.
        using var desktop = ShowOnScreen ? null : PrivateDesktop.Create();
        var outputPath = desktop is null ? null : Path.Combine(Path.GetTempPath(), $"fengsync-ui-{scenario}-{Guid.NewGuid():N}.log");
        using var process = desktop is null
            ? Process.Start(RedirectedStartInfo(arguments)) ?? throw new InvalidOperationException("Unable to start PowerShell UI test host.")
            : desktop.Start("pwsh", arguments, root, outputPath!);

        var timer = Stopwatch.StartNew();
        // A redirected pipe has to be drained while the scenario runs: one nobody reads fills up and then
        // blocks the child. The private-desktop path writes to a file instead, so there is nothing to drain.
        var stdout = desktop is null ? process.StandardOutput.ReadToEndAsync() : null;
        var stderr = desktop is null ? process.StandardError.ReadToEndAsync() : null;
        // The opt-in Google Drive matrix makes three independent remote runs.
        var timeoutSeconds = scenario is "gdrive-volume" or "r2-volume" ? 30 * 60 : 15 * 60;
        var exited = process.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))) != exited)
        {
            await TerminateProcessTreeAsync(process);
            var timedOutOutput = await ReadScenarioOutputAsync(outputPath, stdout, stderr);
            _output.WriteLine($"UI scenario={scenario}; desktop={desktop?.Name ?? "visible"}; timeout={timeoutSeconds}s; elapsed={timer.Elapsed}; stdout/stderr:{Environment.NewLine}{timedOutOutput}");
            throw new TimeoutException($"UI scenario '{scenario}' exceeded {timeoutSeconds}s. Output:{Environment.NewLine}{timedOutOutput}");
        }
        timer.Stop();
        var output = await ReadScenarioOutputAsync(outputPath, stdout, stderr);
        _output.WriteLine($"UI scenario={scenario}; desktop={desktop?.Name ?? "visible"}; exit={process.ExitCode}; elapsed={timer.Elapsed}; stdout/stderr:{Environment.NewLine}{output}");
        if (process.ExitCode == 77 && output.Contains("SKIPPED:", StringComparison.Ordinal))
            throw SkipException.ForSkip(output.Trim());
        Assert.True(process.ExitCode == 0, $"UI scenario '{scenario}' failed (exit {process.ExitCode}).{Environment.NewLine}{output}");
        if (outputPath is not null) File.Delete(outputPath);
    }

    private static ProcessStartInfo RedirectedStartInfo(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>
    /// The scenario's output, from whichever stream the launch used. The file is written by the child and
    /// closed when it exits, so a read can still race the last handle being released.
    /// </summary>
    private static async Task<string> ReadScenarioOutputAsync(string? path, Task<string>? stdout, Task<string>? stderr)
    {
        if (path is null)
            return (stdout is null ? string.Empty : await stdout) + Environment.NewLine + (stderr is null ? string.Empty : await stderr);
        for (var attempt = 0; ; attempt++)
        {
            try { return await File.ReadAllTextAsync(path); }
            catch (IOException) when (attempt < 20) { await Task.Delay(100); }
            catch (IOException) { return $"<the scenario output at '{path}' could not be read.>"; }
        }
    }

    private static async Task TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // taskkill walks the tree, which matters here: the harness's own children — the application,
                // rclone, the CLI — are grandchildren of this process, and killing only the host would leave
                // them for the next scenario's cleanup to find.
                using var kill = Process.Start(new ProcessStartInfo("taskkill", $"/PID {process.Id} /T /F") { UseShellExecute = false, CreateNoWindow = true });
                if (kill is not null) await kill.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException) { /* it exited between the check and kill */ }
        catch (System.ComponentModel.Win32Exception) { /* taskkill could not be started */ }
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* disposed/exited process */ }
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
