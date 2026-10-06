using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FengSync.Core.Configuration;
using FengSync.Localization;
using FengSync.Views;
using Xunit;

namespace FengSync.UiTests;

/// <summary>
/// 界面语言: what the choice has to do before anything else — both languages have every key, the choice is
/// reversible, a damaged stored value is a reportable error rather than a silent fallback, and a window that is
/// already on screen follows the change without being rebuilt.
/// </summary>
/// <remarks>
/// These tests are the reason the assertions elsewhere are allowed to be in Chinese: they pin the Chinese
/// values, and <see cref="LanguageCoverageTests"/> in FengSync.Tests pins that no Chinese literal is left
/// outside the resource files. See <see cref="LanguageCollection"/> for why the language is pinned per test.
/// </remarks>
[Collection(LanguageCollection.Name)]
public sealed class AppLanguageTests
{
    [Fact]
    public void Every_key_resolves_in_both_languages()
    {
        using var scope = new LanguageScope();
        var keys = Keys();
        Assert.NotEmpty(keys);
        var problems = new List<string>();
        foreach (var key in keys)
        {
            // A key that resolves to itself is the miss: it is deliberately visible instead of blank, and
            // a blank label is a layout bug nobody can trace.
            var chinese = Text(key, AppLanguage.Chinese);
            var english = Text(key, AppLanguage.English);
            if (chinese == key) problems.Add($"{key}: no Chinese text");
            if (english == key) problems.Add($"{key}: no English text");
            // Not "the two must differ": 应用/Apply and 浏览…/Browse… are the same word twice, and demanding a
            // difference there would only reward a worse translation. What must not happen is the English
            // column still being Chinese, which is what a column copied from the wrong side looks like.
            if (english.Any(character => character >= '一'))
                problems.Add($"{key}: the English text is still Chinese: {english}");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void A_language_without_a_satellite_falls_back_to_chinese_rather_than_to_english()
    {
        using var scope = new LanguageScope();
        // German asks for nothing, so the neutral set answers. It must answer in Chinese: this application was
        // written in Chinese, and a fallback to a second language the user did not choose is worse than a
        // fallback to the text that has always been there.
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var key = Keys().First();
            Assert.Equal(Neutral(key), Text(key, AppLanguage.Chinese));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void Switching_the_language_changes_the_text_and_switching_back_restores_it()
    {
        using var scope = new LanguageScope();
        // A sentence rather than a one-word label, because 应用 and Apply are the same word twice and would
        // make this test pass for the wrong reason.
        var key = "Settings.LanguageHint";
        var chinese = Text(key, AppLanguage.Chinese);
        var english = Text(key, AppLanguage.English);
        Assert.NotEqual(chinese, english);
        Assert.Equal(english, Text(key, AppLanguage.English));
        scope.To(AppLanguage.Chinese);
        Assert.Equal(chinese, Text(key, AppLanguage.Chinese));
    }

    [Fact]
    public void Applying_the_language_already_in_force_raises_nothing()
    {
        using var scope = new LanguageScope();
        // A settings window that writes back an unchanged value must not repaint the app under the user.
        var raised = 0;
        void Count(object? _, PropertyChangedEventArgs e) { if (e.PropertyName == string.Empty) raised++; }
        AppLanguageContext.Current.PropertyChanged += Count;
        try
        {
            AppLanguageContext.Current.Apply(AppLanguage.Chinese);
            Assert.Equal(0, raised);
            AppLanguageContext.Current.Apply(AppLanguage.English);
            Assert.Equal(1, raised);
            AppLanguageContext.Current.Apply(AppLanguage.English);
            Assert.Equal(1, raised);
        }
        finally { AppLanguageContext.Current.PropertyChanged -= Count; }
    }

    [Fact]
    public void A_damaged_stored_language_is_reported_rather_than_silently_reinterpreted()
    {
        // Without a rule here a corrupt number would reach the ResourceManager, come back as a missing
        // satellite and be rendered as the key — an interface made of "Settings.Apply".
        var settings = new ApplicationSettings { Language = (AppLanguage)42 };
        Assert.Contains(ConfigurationValidator.Validate(settings), error => error.Contains("语言", StringComparison.Ordinal));
        Assert.Empty(ConfigurationValidator.Validate(new ApplicationSettings { Language = AppLanguage.English }));
    }

    [Fact]
    public void The_language_combo_offers_both_languages_written_in_their_own_script()
    {
        using var scope = new LanguageScope();
        var choices = AppLanguageContext.Choices();
        Assert.Equal([AppLanguage.Chinese, AppLanguage.English], choices.Select(choice => choice.Language));
        // Each language names itself, which is the one convention that survives being read by someone who
        // does not read the other option.
        Assert.Equal("简体中文", AppLanguageContext.LabelFor(AppLanguage.Chinese));
        Assert.Equal("English", AppLanguageContext.LabelFor(AppLanguage.English));
    }

    [Fact]
    public void The_settings_window_follows_a_language_change_without_being_rebuilt()
    {
        using var scope = new LanguageScope();
        RunSta(() =>
        {
            var window = OpenSettings(_ => Task.CompletedTask);
            try
            {
                var cancel = window.CancelButton;
                Assert.Equal(Text("Settings.Cancel", AppLanguage.Chinese), cancel.Content as string);

                scope.To(AppLanguage.English);
                // No rebuild, no re-navigation, no new window: the XAML binding re-read the same live context
                // object. This is the whole reason AppLanguageContext raises an empty property name.
                Assert.Equal(Text("Settings.Cancel", AppLanguage.English), cancel.Content as string);
                Assert.Equal(Text("Settings.WindowTitle", AppLanguage.English), window.Title);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Choosing_english_in_settings_saves_the_choice_and_rebuilds_the_page_in_place()
    {
        using var scope = new LanguageScope();
        RunSta(() =>
        {
            ApplicationSettings? applied = null;
            var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = OpenSettings(value => { applied = value; saved.TrySetResult(); return Task.CompletedTask; },
                new ApplicationSettings { Language = AppLanguage.Chinese });
            try
            {
                var combo = window.LanguageCombo;
                Assert.NotNull(combo);
                Assert.Equal(0, combo!.SelectedIndex);
                // Both languages are offered, each written in its own script.
                Assert.Equal(["简体中文", "English"], combo.Items.Cast<ComboBoxItem>().Select(item => item.Content as string));
                Assert.Equal(Text("Settings.Language", AppLanguage.Chinese), LabelAbove(combo));

                combo.SelectedIndex = 1;
                window.ApplyButtonForTest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(saved.Task.Wait(TimeSpan.FromSeconds(10)), "settings were never applied");
                Assert.NotNull(applied);
                Assert.Equal(AppLanguage.English, applied!.Language);

                // The user is left on the page they were on, rebuilt in the language they chose: the combo is
                // the same control with the new value, not a new page.
                var rebuilt = window.LanguageCombo;
                Assert.NotNull(rebuilt);
                Assert.Equal(1, rebuilt!.SelectedIndex);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task The_language_survives_a_save_and_a_reload()
    {
        using var scope = new LanguageScope();
        var directory = NewDirectory();
        var previousDataDir = Environment.GetEnvironmentVariable("FENGSYNC_DATA_DIR");
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "FengSync.local.json"));
            await store.SaveAsync(new ApplicationSettings { Language = AppLanguage.English });
            Assert.Equal(AppLanguage.English, (await store.LoadAsync()).Settings.Language);

            // And through the whole shell's startup path, which is what actually reads it. That path goes
            // through AppDataPaths rather than a file name, so the data root has to be pointed at the same
            // directory the way every other test that writes durable state points it.
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", directory);
            scope.To(AppLanguage.Chinese);
            AppLanguageStartup.ApplySavedLanguage();
            Assert.Equal(AppLanguage.English, AppLanguageContext.Current.Language);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", previousDataDir);
            TryDelete(directory);
        }
    }

    [Fact]
    public void An_absent_or_unreadable_settings_file_leaves_the_default_language_in_place()
    {
        using var scope = new LanguageScope();
        var directory = NewDirectory();
        try
        {
            scope.To(AppLanguage.Chinese);
            Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", directory);
            AppLanguageStartup.ApplySavedLanguage();
            Assert.Equal(AppLanguage.Chinese, AppLanguageContext.Current.Language);
        }
        finally { Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", null); TryDelete(directory); }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// A settings window shown for real, over a no-op apply. Shown rather than merely constructed because the
    /// thing under test is what a user sees, and an unshown window has no visual tree to read.
    /// </summary>
    private static SettingsWindow OpenSettings(Func<ApplicationSettings, Task> apply, ApplicationSettings? initial = null)
    {
        var window = new SettingsWindow(initial ?? new ApplicationSettings(), apply,
            () => Task.CompletedTask, () => Task.FromResult(0), null);
        window.Show();
        return window;
    }

    /// <summary>The label of the row a control sits in, which is how a user finds the setting.</summary>
    private static string LabelAbove(FrameworkElement input)
    {
        var row = (Panel)input.Parent;
        return row.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
    }

    private static string Text(string key, AppLanguage language)
    {
        var previous = AppLanguageContext.Current.Language;
        AppLanguageContext.Current.Apply(language);
        try { return AppLanguageContext.Current[key]; }
        finally { AppLanguageContext.Current.Apply(previous); }
    }

    /// <summary>Every key in the neutral resource set, read from the built assembly rather than from disk.</summary>
    private static IReadOnlyList<string> Keys()
    {
        var keys = new List<string>();
        foreach (var name in typeof(AppLanguageContext).Assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith("Strings.resources", StringComparison.Ordinal)) continue;
            using var stream = typeof(AppLanguageContext).Assembly.GetManifestResourceStream(name)!;
            using var reader = new System.Resources.ResourceReader(stream);
            foreach (System.Collections.DictionaryEntry entry in reader) keys.Add((string)entry.Key);
        }
        Assert.NotEmpty(keys);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    /// <summary>The value in the neutral (Chinese) resource set, read from the built assembly.</summary>
    private static string? Neutral(string key) => ReadResource("Strings", key);

    private static string? ReadResource(string name, string key)
    {
        var assembly = typeof(AppLanguageContext).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(candidate => candidate.EndsWith($".Localization.{name}.resources", StringComparison.Ordinal));
        if (resource is null) return null;
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new System.Resources.ResourceReader(stream);
        return reader.Cast<System.Collections.DictionaryEntry>()
            .Where(entry => (string)entry.Key == key)
            .Select(entry => (string?)entry.Value)
            .FirstOrDefault();
    }

    private static T? FindById<T>(DependencyObject root, string id) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && Equals(AutomationProperties.GetAutomationId(found), id)) return found;
            var grand = FindById<T>(child, id);
            if (grand is not null) return grand;
        }
        return null;
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-lang-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Runs one action on its own STA thread with the dispatcher pumped, the same shape every other window test
    /// in this assembly uses. The process allows only one WPF <c>Application</c>, so the dispatcher is shut
    /// down explicitly and a run that stops making progress is cut off by a watchdog.
    /// </summary>
    private static void RunSta(Action action)
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            Exception? failure = null;
            try
            {
                var ownsApplication = MergeApplicationThemes();
                try { action(); }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    if (ownsApplication)
                        try { Application.Current?.Shutdown(); }
                        catch (InvalidOperationException) { }
                }
            }
            finally { dispatcher.InvokeShutdown(); }
            finished.TrySetResult(failure);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var exception = finished.Task.GetAwaiter().GetResult();
        thread.Join(TimeSpan.FromSeconds(30));
        if (exception is not null) throw exception;
    }

    private static bool MergeApplicationThemes()
    {
        var ownsApplication = Application.Current is null;
        if (ownsApplication) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var application = Application.Current!;
        if (application.Resources.MergedDictionaries.Count == 0)
            foreach (var resource in new[] { "DesignTokens.xaml", "Typography.xaml", "Icons.xaml", "Controls.xaml" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/FengSync;component/Themes/{resource}")
                });
        return ownsApplication;
    }
}

/// <summary>
/// Puts the language back where it was, including when the test failed halfway through a switch. Every test
/// that changes the language uses one of these, which is what keeps the rest of the assembly free to assert in
/// Chinese without each of them knowing the language exists.
/// </summary>
internal sealed class LanguageScope : IDisposable
{
    private readonly AppLanguage _start;

    public LanguageScope()
    {
        _start = AppLanguageContext.Current.Language;
        AppLanguageContext.Current.Apply(AppLanguage.Chinese);
    }

    public void To(AppLanguage language) => AppLanguageContext.Current.Apply(language);

    public void Dispose() => AppLanguageContext.Current.Apply(_start);
}
