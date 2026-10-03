using System.Text.RegularExpressions;

namespace FengSync.Tests;

/// <summary>
/// Guardrails for the UI acceptance harness. Running the WPF scenarios must not disturb whoever is using
/// the machine: no window raised, no cursor moved, nothing drawn over their work. That property lives in
/// the harness script and the two files that hand it the mode, and it is exactly the kind of thing a later
/// change re-breaks by adding one convenient <c>SetCursorPos</c>. These checks are source-level because the
/// alternative — observing the real desktop — is the very thing being protected.
/// </summary>
public sealed class DesktopFreeUiHarnessTests
{
    /// <summary>
    /// Calls that move or depend on the physical pointer, or that take the foreground. Each of these must
    /// sit behind the harness's <c>$script:showOnScreen</c> switch.
    /// </summary>
    private static readonly string[] PhysicalInputCalls =
    [
        "mouse_event(",
        "SetCursorPos(",
        "[System.Windows.Forms.Cursor]::Position",
        "[Windows.Forms.Cursor]::Position",
        "SetForegroundWindow("
    ];

    private static readonly Regex FunctionBlock = new(@"^function\s+(?<name>\S+).*?(?=^function\s|\z)", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    [Fact]
    public void The_scenario_harness_defaults_to_not_showing_its_windows()
    {
        var script = ReadHarnessFile("tests", "FengSync.UiTests", "Scripts", "Invoke-UiScenario.ps1");

        // The switch has to exist and be a switch: a mandatory parameter would force every caller to choose,
        // and a default of $true would put the windows back on screen for everyone who forgot.
        Assert.Contains("[switch]$ShowOnScreen", script, StringComparison.Ordinal);
        Assert.Contains("$script:showOnScreen = [bool]$ShowOnScreen", script, StringComparison.Ordinal);
        // The off-screen origin is what keeps a parked window out of the user's way.
        Assert.Contains("$script:offScreenOrigin = -32000", script, StringComparison.Ordinal);
    }

    [Fact]
    public void No_harness_step_reaches_for_the_pointer_without_the_show_on_screen_switch()
    {
        var script = ReadHarnessFile("tests", "FengSync.UiTests", "Scripts", "Invoke-UiScenario.ps1");

        var unguarded = new List<string>();
        foreach (Match block in FunctionBlock.Matches(script))
        {
            var body = block.Value;
            if (!PhysicalInputCalls.Any(call => body.Contains(call, StringComparison.Ordinal))) continue;
            // A function that touches the pointer must consult the switch. That is the whole contract: the
            // switch is false by default, so the function's physical path is unreachable unless a developer
            // deliberately asked to watch a scenario run.
            if (!body.Contains("$script:showOnScreen", StringComparison.Ordinal)) unguarded.Add(block.Groups["name"].Value);
        }

        Assert.True(unguarded.Count == 0,
            "These harness functions move or depend on the physical pointer without consulting $script:showOnScreen, "
            + "so a default run would disturb the user's desktop: " + string.Join(", ", unguarded));
    }

    [Fact]
    public void The_full_test_runner_only_asks_for_on_screen_windows_when_the_switch_is_passed()
    {
        var runner = ReadHarnessFile("scripts", "Test-All.ps1");

        Assert.Contains("[switch]$ShowOnScreen", runner, StringComparison.Ordinal);
        Assert.Contains("if ($ShowOnScreen) { $env:FENGSYNC_UI_SHOW_ON_SCREEN = '1' }", runner, StringComparison.Ordinal);
        // The variable is cleared afterwards so a later stage in the same session cannot inherit it.
        Assert.Contains("Remove-Item Env:FENGSYNC_UI_SHOW_ON_SCREEN", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void The_test_host_only_forwards_the_switch_when_it_is_set()
    {
        var tests = ReadHarnessFile("tests", "FengSync.UiTests", "UiAcceptanceTests.cs");

        Assert.Contains("FENGSYNC_UI_SHOW_ON_SCREEN", tests, StringComparison.Ordinal);
        Assert.Contains("if (ShowOnScreen) start.ArgumentList.Add(\"-ShowOnScreen\");", tests, StringComparison.Ordinal);
    }

    private static string ReadHarnessFile(params string[] segments)
    {
        var path = Path.Combine([RepositoryRoot(), .. segments]);
        Assert.True(File.Exists(path), $"Expected harness file was not found: {path}");
        return File.ReadAllText(path);
    }

    private static string RepositoryRoot()
    {
        foreach (var startDirectory in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "FengSync.sln"))) return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Feng Sync repository root from the test execution directory.");
    }
}
