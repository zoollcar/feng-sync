using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FengSync.Core.Configuration;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// The cloud page's proxy field. Two halves fail independently: the box has to exist and show what is
/// saved, and the page's Apply path has to copy it into the snapshot the shell persists. A field that is
/// missing from <c>BuildSettings</c> leaves the box displaying a value that never reaches the daemon.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public sealed class CloudProxySettingsUiTests
{
    [Fact]
    public void The_cloud_page_shows_the_saved_proxy()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var window = OpenCloudPage(new ApplicationSettings { RcloneProxy = "proxy.corp:8080" });

            Assert.Equal("proxy.corp:8080", FindById<TextBox>(window, "SettingsCloudProxy").Text);
            window.Close();
        });
    }

    [Fact]
    public void Editing_the_proxy_reaches_the_saved_settings()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            ApplicationSettings? applied = null;
            var window = OpenCloudPage(new ApplicationSettings(),
                settings => { applied = settings; return Task.CompletedTask; });

            FindById<TextBox>(window, "SettingsCloudProxy").Text = " socks5://127.0.0.1:1080 ";
            window.ApplyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            // Trimmed, and present in the snapshot the window hands over. The address is what the user
            // pasted; the surrounding spaces would become part of the proxy URL the child is given.
            Assert.Equal("socks5://127.0.0.1:1080", applied!.RcloneProxy);

            // Clearing it must persist as "inherit", not as a leftover address the user can no longer see.
            FindById<TextBox>(window, "SettingsCloudProxy").Text = "   ";
            window.ApplyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("", applied!.RcloneProxy);

            window.Close();
        });
    }

    /// <summary>
    /// Opens the window on the cloud page. Selecting the navigation item is what a click does — the page
    /// switch reads the item's Content — and the page is rebuilt from scratch on every navigation.
    /// </summary>
    private static SettingsWindow OpenCloudPage(ApplicationSettings settings, Func<ApplicationSettings, Task>? apply = null)
    {
        var window = new SettingsWindow(settings, apply ?? (_ => Task.CompletedTask), () => Task.CompletedTask,
            () => Task.FromResult(0), null);
        window.Navigation.SelectedItem = window.Navigation.Items.Cast<ListBoxItem>()
            .Single(item => (string)item.Content == "云盘");
        return window;
    }

    /// <summary>
    /// The cloud page is built in code and its controls are identified by
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
