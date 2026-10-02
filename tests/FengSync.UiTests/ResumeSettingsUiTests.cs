using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The resume threshold is configured in two windows, and the desktop acceptance scenarios that drive
/// them are the flakiest part of the suite because they need a foreground desktop session. These tests
/// construct the windows in-process instead, so "the control exists, shows the value that would actually
/// be used, and only accepts input once the profile stops inheriting" is checked deterministically.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class ResumeSettingsUiTests
{
    private const long TwoGigabytes = 2L * 1024 * 1024 * 1024;

    [Fact]
    public void A_profile_editor_shows_the_application_threshold_it_would_inherit()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var window = new ProfileEditorWindow(SyncProfile.Create("p", @"C:\left", @"C:\right"),
                new ApplicationSettings { DefaultResumeMinimumBytes = TwoGigabytes });

            Assert.True(window.UseDefaultResume.IsChecked);
            // Showing the inherited value matters: an empty box would hide which threshold is in force.
            Assert.Equal("2G", window.ResumeMinimumBox.Text);
            Assert.False(window.ResumeMinimumBox.IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void A_profile_that_sets_its_own_threshold_can_edit_it()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var profile = SyncProfile.Create("p", @"C:\left", @"C:\right") with { Settings = new ProfileSettings(ResumeMinimumBytes: 1024) };
            var window = new ProfileEditorWindow(profile, new ApplicationSettings());

            Assert.False(window.UseDefaultResume.IsChecked);
            Assert.Equal("1K", window.ResumeMinimumBox.Text);
            Assert.True(window.ResumeMinimumBox.IsEnabled);
            window.Close();
        });
    }

    [Fact]
    public void The_settings_center_exposes_the_resume_threshold()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var window = new SettingsWindow(new ApplicationSettings { DefaultResumeMinimumBytes = TwoGigabytes },
                _ => Task.CompletedTask, () => Task.CompletedTask, () => Task.FromResult(0), null);

            Assert.Equal("2G", FindById<TextBox>(window, "SettingsResumeMinimum").Text);
            window.Close();
        });
    }

    /// <summary>
    /// The general settings page is built in code and its controls are identified by
    /// <see cref="AutomationProperties.AutomationIdProperty"/>, so they are found by walking the tree —
    /// logically first, because the visual tree of a window that was never shown only exists after a
    /// layout pass.
    /// </summary>
    private static T FindById<T>(FrameworkElement root, string id) where T : FrameworkElement
    {
        if (FindInLogicalTree(root) is { } logical) return logical;
        root.Measure(new Size(900, 4000));
        root.Arrange(new Rect(0, 0, 900, 4000));
        return FindInVisualTree(root) ?? throw new InvalidOperationException($"界面缺少 AutomationId 为 {id} 的控件。");

        bool Matches(DependencyObject candidate) =>
            candidate is T match && (string?)match.GetValue(AutomationProperties.AutomationIdProperty) == id;

        T? FindInLogicalTree(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent))
            {
                if (child is not DependencyObject node) continue;
                if (Matches(node)) return (T)node;
                if (FindInLogicalTree(node) is { } nested) return nested;
            }
            return null;
        }

        T? FindInVisualTree(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (Matches(child)) return (T)child;
                if (FindInVisualTree(child) is { } nested) return nested;
            }
            return null;
        }
    }

    /// <summary>
    /// The windows rely on the shared theme dictionaries that the main shell merges in App.xaml. A test
    /// process has no App, so install them on whichever Application already exists (never a second one:
    /// WPF allows a single Application per AppDomain).
    /// </summary>
    private static void EnsureApplicationResources()
    {
        var application = Application.Current ?? new Application();
        if (application.Resources.MergedDictionaries.Count > 0) return;
        foreach (var resource in new[] { "DesignTokens.xaml", "Typography.xaml", "Icons.xaml", "Controls.xaml" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/FengSync;component/Themes/{resource}")
            });
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
