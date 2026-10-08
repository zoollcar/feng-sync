using System.Windows;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// A desktop-free UI run must not put anything on the screen of whoever is using the machine, and nothing
/// outside the process can act before a window has been drawn — so the application moves its own windows
/// when the harness asks. This pins the ask (one environment switch, read the way the other test switches
/// are) and what it does to a window.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class TestWindowPlacementTests
{
    [Fact]
    public void A_window_opens_outside_the_visible_desktop_when_the_harness_asked_for_it()
    {
        WithSwitch("1", () => RunSta(() =>
        {
            // CenterScreen is what the transfer window's XAML asks for, and it is the placement that would
            // otherwise put that window in the middle of the user's screen.
            var window = new Window { WindowStartupLocation = WindowStartupLocation.CenterScreen };

            TestWindowPlacement.Apply(window);

            Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);
            Assert.Equal(TestWindowPlacement.OffScreenOrigin, window.Left);
            Assert.Equal(TestWindowPlacement.OffScreenOrigin, window.Top);
        }));
    }

    [Fact]
    public void A_window_keeps_the_placement_it_asked_for_when_nobody_asked_for_the_park()
    {
        WithSwitch(null, () => RunSta(() =>
        {
            var window = new Window { WindowStartupLocation = WindowStartupLocation.CenterScreen };

            TestWindowPlacement.Apply(window);

            Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
            Assert.True(double.IsNaN(window.Left));
            Assert.True(double.IsNaN(window.Top));
        }));
    }

    [Fact]
    public void Only_the_value_the_harness_sets_counts_as_asked()
    {
        WithSwitch("1", () => Assert.True(TestWindowPlacement.Requested));
        WithSwitch("0", () => Assert.False(TestWindowPlacement.Requested));
        WithSwitch("true", () => Assert.False(TestWindowPlacement.Requested));
        WithSwitch(null, () => Assert.False(TestWindowPlacement.Requested));
    }

    /// <summary>
    /// Sets the switch for the length of one check. The variable is process-wide, which is why this class
    /// sits in the collection that keeps such tests from overlapping.
    /// </summary>
    private static void WithSwitch(string? value, Action check)
    {
        var previous = Environment.GetEnvironmentVariable("FENGSYNC_OFFSCREEN_WINDOWS");
        Environment.SetEnvironmentVariable("FENGSYNC_OFFSCREEN_WINDOWS", value);
        try { check(); }
        finally { Environment.SetEnvironmentVariable("FENGSYNC_OFFSCREEN_WINDOWS", previous); }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
