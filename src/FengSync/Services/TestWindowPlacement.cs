using System.Windows;

namespace FengSync.Services;

/// <summary>
/// Desktop-free test runs. The UI acceptance harness launches this application to drive it through UI
/// Automation, and while it does, nothing may be drawn on the screen of whoever is using the machine.
/// Moving a window once it has been shown is not enough: it is on screen, and it is the foreground window,
/// for as long as anything outside this process takes to notice it — and the shell is on screen for the
/// whole of its own startup, which is seconds. So when the harness asks, by setting
/// <c>FENGSYNC_OFFSCREEN_WINDOWS=1</c> on the child process, a window opens outside the visible desktop
/// instead, and is never drawn there at all.
/// </summary>
/// <remarks>
/// The switch is read from the environment, like <c>FENGSYNC_FORCE_SOFTWARE_RENDERING</c> and
/// <c>FENGSYNC_DATA_DIR</c>, and for the same reason: only a process the harness started can reach this
/// behaviour, and a normal run never reaches it at all.
/// </remarks>
public static class TestWindowPlacement
{
    /// <summary>Far outside every virtual desktop, so no monitor can show a window placed here.</summary>
    public const double OffScreenOrigin = -32000;

    /// <summary>True when this process was started by the desktop-free UI harness.</summary>
    public static bool Requested =>
        Environment.GetEnvironmentVariable("FENGSYNC_OFFSCREEN_WINDOWS") == "1";

    /// <summary>
    /// Opens <paramref name="window"/> outside the visible desktop when the harness asked for it, and leaves
    /// it alone otherwise. Call this before the window is shown — from the constructor — because that is the
    /// last moment a position can be chosen without the window having been on screen first.
    /// </summary>
    public static void Apply(Window window)
    {
        if (!Requested) return;
        // Manual rather than CenterScreen or CenterOwner: those are the window manager's own choices and
        // would put the window straight back onto a monitor.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = OffScreenOrigin;
        window.Top = OffScreenOrigin;
    }
}
