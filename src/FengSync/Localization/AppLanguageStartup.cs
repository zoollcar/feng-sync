using System.Diagnostics;
using System.Windows;
using FengSync.Core.Configuration;

namespace FengSync.Localization;

/// <summary>
/// The two things every entry point has to do before it draws anything: pick the language the user saved,
/// and make sure every window that appears later picks it up too.
/// </summary>
public static class AppLanguageStartup
{
    static AppLanguageStartup() => AppLanguageContext.Current.Applied += RetagOpenWindows;

    /// <summary>
    /// Reads the saved language and applies it. Called before the first window is created, and blocking on
    /// purpose: a window that renders its first frame in one language and the rest of itself in another is a
    /// worse bug than a few milliseconds of startup, and the file being read is one small local JSON.
    /// A damaged or absent file leaves the default in place.
    /// </summary>
    public static void ApplySavedLanguage()
    {
        try
        {
            var settings = new SettingsStore().LoadAsync().GetAwaiter().GetResult().Settings;
            AppLanguageContext.Current.Apply(settings.Language);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Unable to read the saved interface language; keeping the default: " + ex);
        }
    }

    /// <summary>
    /// Gives every window — the ones declared in XAML and the ones built in code — the framework's own
    /// language. A class handler on <see cref="Window.LoadedEvent"/> rather than a line per window, because a
    /// line per window is a line someone forgets; it covers windows that are created after a language switch
    /// as well as the ones already on screen.
    /// </summary>
    public static void RegisterWindowLanguage()
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window window) window.Language = AppLanguageContext.Current.Tag; }));
    }

    /// <summary>
    /// Re-applies to the windows already open. Called from <see cref="AppLanguageContext.Apply"/> through the
    /// <see cref="AppLanguageContext.Applied"/> event, because <c>Language</c> is an inherited property and
    /// changing the source object does not push a new value into children.
    /// </summary>
    public static void RetagOpenWindows()
    {
        var application = Application.Current;
        if (application is null) return;
        // The language is a process-wide setting but the window list belongs to the UI thread, and a caller
        // outside a dispatcher (a test, a background continuation) has to hand the work over rather than
        // reach across. A dispatcher that is shutting down is not worth an exception: there is nobody left
        // to re-tag.
        if (!application.Dispatcher.CheckAccess())
        {
            if (application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished) return;
            application.Dispatcher.BeginInvoke(RetagOpenWindows);
            return;
        }
        foreach (Window window in application.Windows) window.Language = AppLanguageContext.Current.Tag;
    }
}
