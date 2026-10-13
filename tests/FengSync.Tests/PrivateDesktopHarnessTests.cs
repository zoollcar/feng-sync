using System.Text.RegularExpressions;

namespace FengSync.Tests;

/// <summary>
/// Guardrails for the UI acceptance harness. Running the WPF scenarios must not disturb whoever is using the
/// machine: no window raised, no cursor moved, nothing drawn over their work, no taskbar button. That property
/// is no longer maintained by moving windows out of the way — it is maintained by running the whole scenario
/// somewhere else, on a private Windows desktop the user is not looking at. These checks pin that arrangement
/// and the pieces it depends on, because each of them is the kind of thing a later change re-breaks: a
/// fallback to the visible desktop, a re-introduced <c>-32000</c>, a <c>Process.MainWindowHandle</c> that
/// starts naming the wrong window.
///
/// They are source-level because the alternative — observing the real desktop — is the very thing being
/// protected, and because the two properties that matter most here were found by measurement and would
/// otherwise have to be re-measured by hand:
///
/// * synthesized pointer input cannot work on a private desktop. Measured with a control run of the same
///   probe on both desktops: on the private one <c>GetCursorPos</c> and <c>mouse_event</c> fail with
///   ERROR_ACCESS_DENIED and <c>SetCursorPos</c> returns false, while on the visible one all three succeed.
///   That is why the pointer calls stay behind <c>-ShowOnScreen</c> — not politeness, but reachability.
/// * <c>Process.MainWindowHandle</c> is a heuristic and stops being the shell on a private desktop, where the
///   application also owns the input-indicator overlays.
/// </summary>
public sealed class PrivateDesktopHarnessTests
{
    /// <summary>
    /// Calls that move or depend on the physical pointer. Each of these must sit behind the harness's
    /// <c>$script:showOnScreen</c> switch, because a private desktop denies them outright and the visible
    /// desktop belongs to whoever is using the machine.
    /// </summary>
    private static readonly string[] PhysicalInputCalls =
    [
        "mouse_event(",
        "SetCursorPos(",
        "[System.Windows.Forms.Cursor]::Position",
        "[Windows.Forms.Cursor]::Position",
        "SetForegroundWindow("
    ];

    /// <summary>
    /// What keeping a window out of the user's way used to cost, and what the private desktop replaced. None
    /// of it may come back: every one of these was either a window that had already been seen by the time it
    /// was moved, or an application-side seam that existed only to serve the tests.
    /// </summary>
    private static readonly string[] ParkingArtifacts =
    [
        "-32000",
        "SetWindowPos",
        "ShowWindow",
        "Park-",
        "offScreenOrigin",
        "FENGSYNC_OFFSCREEN_WINDOWS",
        "TestWindowPlacement"
    ];

    private static readonly Regex FunctionBlock = new(@"^function\s+(?<name>\S+).*?(?=^function\s|\z)", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    [Fact]
    public void The_scenario_harness_runs_on_a_private_desktop_unless_a_developer_asks_to_watch()
    {
        var script = ReadHarnessFile("tests", "FengSync.UiTests", "Scripts", "Invoke-UiScenario.ps1");

        // The switch has to exist and be a switch: a mandatory parameter would force every caller to choose,
        // and a default of $true would put the windows back on screen for everyone who forgot.
        Assert.Contains("[switch]$ShowOnScreen", script, StringComparison.Ordinal);
        Assert.Contains("$script:showOnScreen = [bool]$ShowOnScreen", script, StringComparison.Ordinal);
    }

    [Fact]
    public void No_step_of_the_harness_reaches_for_the_pointer_without_the_show_on_screen_switch()
    {
        var script = ReadHarnessFile("tests", "FengSync.UiTests", "Scripts", "Invoke-UiScenario.ps1");

        var unguarded = new List<string>();
        foreach (Match block in FunctionBlock.Matches(script))
        {
            var body = block.Value;
            if (!PhysicalInputCalls.Any(call => body.Contains(call, StringComparison.Ordinal))) continue;
            // A function that touches the pointer must consult the switch. That is the whole contract: the
            // switch is false by default, so the function's physical path is unreachable unless a developer
            // deliberately asked to watch a scenario run — and on a private desktop it could not work anyway.
            if (!body.Contains("$script:showOnScreen", StringComparison.Ordinal)) unguarded.Add(block.Groups["name"].Value);
        }

        Assert.True(unguarded.Count == 0,
            "These harness functions move or depend on the physical pointer without consulting $script:showOnScreen, "
            + "so a default run would either disturb the user's desktop or silently do nothing: " + string.Join(", ", unguarded));
    }

    [Fact]
    public void No_window_parking_survives_in_the_harness()
    {
        var script = ReadHarnessFile("tests", "FengSync.UiTests", "Scripts", "Invoke-UiScenario.ps1");

        // Parking was the old answer and it never fully worked: a window placed at -32000 is still a window, it
        // still activates, and it therefore still took the foreground. The private desktop is the answer now, so
        // a re-introduced parking helper would be both dead weight and a regression.
        var found = ParkingArtifacts.Where(artifact => script.Contains(artifact, StringComparison.Ordinal)).ToList();
        Assert.True(found.Count == 0,
            "The harness still contains window-parking artifacts, which the private desktop replaced: " + string.Join(", ", found));
    }

    [Fact]
    public void The_application_carries_no_test_only_window_placement()
    {
        // The seam that used to move the shell and the transfer window off screen is gone, and so is the
        // environment variable that switched it on. A production window that positions itself for a test is
        // exactly the coupling the private desktop removes.
        Assert.False(File.Exists(Path.Combine(RepositoryRoot(), "src", "FengSync", "Services", "TestWindowPlacement.cs")),
            "TestWindowPlacement.cs is back. Window placement for the tests belongs to the private desktop, not the application.");
        Assert.False(File.Exists(Path.Combine(RepositoryRoot(), "tests", "FengSync.UiTests", "TestWindowPlacementTests.cs")),
            "TestWindowPlacementTests.cs is back, so something reintroduced the application-side placement seam.");

        foreach (var window in new[]
        {
            new[] { "src", "FengSync", "MainWindow.xaml.cs" },
            new[] { "src", "FengSync", "Views", "CloudTransferWindow.xaml.cs" },
        })
        {
            var source = ReadHarnessFile(window);
            Assert.DoesNotContain("TestWindowPlacement", source, StringComparison.Ordinal);
            Assert.DoesNotContain("FENGSYNC_OFFSCREEN_WINDOWS", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_harness_never_asks_the_process_which_window_is_the_main_one()
    {
        var script = ReadHarnessFile("tests", "FengSync.UiTests", "Scripts", "Invoke-UiScenario.ps1");

        // Process.MainWindowHandle is a heuristic — the first visible top-level window the process owns — and on
        // a private desktop the application owns several, because the input-indicator overlays are created
        // in-process a few seconds in and sit above everything. It then names an overlay, and every lookup
        // rooted at "the main window" fails with a missing element while the shell is plainly still there.
        // Measured: ui-shell failed exactly this way ("Missing UI element: CompareButton") until the shell was
        // resolved structurally. Comments may still name the API, so only real code is inspected.
        var code = string.Join('\n', script.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

        Assert.DoesNotContain(".MainWindowHandle", code, StringComparison.Ordinal);
        Assert.Contains("function Get-MainWindowHandle", code, StringComparison.Ordinal);
        Assert.Contains("function Get-LiveMain { return [System.Windows.Automation.AutomationElement]::FromHandle((Get-MainWindowHandle)) }", code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_capture_on_the_private_desktop_comes_from_the_window_not_the_screen()
    {
        var script = ReadHarnessFile("tests", "FengSync.UiTests", "Scripts", "Invoke-UiScenario.ps1");

        // A desktop that is not being displayed has no screen to copy from, so CopyFromScreen cannot work there
        // (measured: it throws "The handle is invalid"). The capture has to be rendered by the window itself,
        // and PW_RENDERFULLCONTENT is what makes a composited window render instead of handing back an empty
        // frame. The screen copy stays for the opt-in on-screen mode, which is the only mode with a screen.
        var body = SingleFunction(script, "Capture-Element");
        var render = body.IndexOf("PrintWindow(", StringComparison.Ordinal);
        var copy = body.IndexOf("CopyFromScreen(", StringComparison.Ordinal);

        Assert.True(render >= 0, "Capture-Element no longer renders the window itself, so a private-desktop capture would have no source.");
        Assert.Contains("PW_RENDERFULLCONTENT", body, StringComparison.Ordinal);
        Assert.Contains("-not $script:showOnScreen", body, StringComparison.Ordinal);
        Assert.True(copy < 0 || render < copy, "Capture-Element copies from the screen before it renders the window, which cannot work on a private desktop.");
    }

    [Fact]
    public void The_test_host_starts_the_scenario_on_a_private_desktop_and_forwards_the_switch_when_it_is_set()
    {
        var tests = ReadHarnessFile("tests", "FengSync.UiTests", "UiAcceptanceTests.cs");

        Assert.Contains("FENGSYNC_UI_SHOW_ON_SCREEN", tests, StringComparison.Ordinal);
        Assert.Contains("if (ShowOnScreen) arguments.Add(\"-ShowOnScreen\");", tests, StringComparison.Ordinal);
        // One switch decides where a scenario runs, and the default is the private desktop. There must be no
        // catch that quietly falls back to the visible desktop: a run that silently lands on the user's screen
        // is worse than one that fails, because not landing there is the entire point.
        Assert.Contains("using var desktop = ShowOnScreen ? null : PrivateDesktop.Create();", tests, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateDesktop.Create() ?? ", tests, StringComparison.Ordinal);
    }

    [Fact]
    public void The_private_desktop_hands_the_child_the_desktop_and_fails_loudly_when_it_cannot()
    {
        var desktop = ReadHarnessFile("tests", "FengSync.UiTests", "PrivateDesktop.cs");

        // STARTUPINFO.lpDesktop is the only way to launch a process onto a desktop, and only CreateProcess can
        // set it — ProcessStartInfo has no equivalent. Children inherit it, so launching the harness host here
        // is enough for everything it starts in turn.
        Assert.Contains("lpDesktop = Name", desktop, StringComparison.Ordinal);
        Assert.Contains("CreateProcessW", desktop, StringComparison.Ordinal);
        Assert.Contains("CreateDesktopW", desktop, StringComparison.Ordinal);
        // Creating the desktop must throw rather than fall back, and so must starting the child on it.
        Assert.Contains("Could not create the private desktop", desktop, StringComparison.Ordinal);
        Assert.Contains("Could not start '", desktop, StringComparison.Ordinal);
        // CreateProcess does not search PATH for a bare name the way Process.Start does, so the launcher
        // resolves it first; without that, "pwsh" fails with a bare file-not-found.
        Assert.Contains("ResolveApplication", desktop, StringComparison.Ordinal);
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

    private static string SingleFunction(string script, string name)
    {
        foreach (Match block in FunctionBlock.Matches(script))
            if (block.Groups["name"].Value == name) return block.Value;
        throw new InvalidOperationException($"The harness no longer defines '{name}'.");
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
