using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Markup;
using FengSync.Core.Configuration;

namespace FengSync.Localization;

/// <summary>
/// The live language selection. One instance for the life of the process, mutated in place — a page binds to
/// <see cref="Current"/> through <c>{x:Static}</c>, so replacing the instance would leave every already-loaded
/// page bound to an object nobody raises an event on. Changing the language therefore raises
/// <see cref="PropertyChanged"/> with an <b>empty</b> property name, which WPF reads as "every property
/// changed" and which re-evaluates the indexer bindings the pages use.
/// </summary>
/// <remarks>
/// <see cref="CurrentCulture"/> moves with it so that the framework's own text — the TextBox context menu,
/// for instance, which is chosen by the element's <c>Language</c> rather than by anything we wrote — follows
/// the same choice. It is applied to the current thread and, deliberately, not written to the OS: a process
/// that changes the machine-wide locale as a side effect of a settings toggle is a worse bug than an
/// English context menu inside a Chinese window.
/// </remarks>
public sealed class AppLanguageContext : INotifyPropertyChanged
{
    public static AppLanguageContext Current { get; } = new();

    private AppLanguage _language = AppLanguage.Chinese;

    /// <summary>Never an out-of-range value: a number read from a damaged settings file falls back.</summary>
    public AppLanguage Language => Enum.IsDefined(_language) ? _language : AppLanguage.Chinese;

    /// <summary>
    /// The WPF <c>Language</c> for this choice, so a shell root can hand it to the framework for its own
    /// strings. Uses the IETF tag rather than the .NET name because that is what <c>XmlLanguage</c> wants.
    /// </summary>
    public XmlLanguage Tag => XmlLanguage.GetLanguage(Language == AppLanguage.English ? "en-US" : "zh-CN");

    /// <summary>The string for <paramref name="key"/> in the current language. See <see cref="Strings"/>.</summary>
    public string this[string key] => Strings.Get(key, Language);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised after the language actually changed, for the things a binding cannot reach — chiefly
    /// re-tagging the open windows' inherited <c>Language</c>. See <see cref="AppLanguageStartup.RetagOpenWindows"/>.
    /// </summary>
    public event Action? Applied;

    /// <summary>
    /// Switches the shell's language. Idempotent: applying the language already in force does not raise the
    /// change, so a settings window that writes back an unchanged value cannot repaint the app under the user.
    /// </summary>
    public void Apply(AppLanguage language)
    {
        var chosen = Enum.IsDefined(language) ? language : AppLanguage.Chinese;
        CultureInfo.CurrentUICulture = chosen == AppLanguage.English
            ? CultureInfo.GetCultureInfo("en-US")
            : CultureInfo.GetCultureInfo("zh-CN");
        if (chosen == _language) return;
        _language = chosen;
        // Empty, not null: WPF's binding treats an empty property name as "all of them", and a null one is
        // an ArgumentNullException by the time it reaches PropertyChangedEventArgs.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        Applied?.Invoke();
    }

    /// <summary>The two language names, each written in its own language — the one place a language list is shown.</summary>
    public static IReadOnlyList<(AppLanguage Language, string Label)> Choices() =>
    [
        (AppLanguage.Chinese, "简体中文"),
        (AppLanguage.English, "English")
    ];

    /// <summary>The label for one language, in that language (so the list reads 「简体中文 / English」).</summary>
    public static string LabelFor(AppLanguage language) =>
        Choices().First(choice => choice.Language == (Enum.IsDefined(language) ? language : AppLanguage.Chinese)).Label;

    /// <summary>Names a value for a settings file and a diagnostics report, in the language it is written in.</summary>
    public static string Describe(AppLanguage language) => LabelFor(language);
}

/// <summary>
/// The CultureInfo thread shim, kept so a caller outside a WPF dispatcher still gets the same choice.
/// </summary>
internal static class LanguageCulture
{
    public static CultureInfo CurrentCulture =>
        AppLanguageContext.Current.Language == AppLanguage.English
            ? CultureInfo.GetCultureInfo("en-US")
            : CultureInfo.GetCultureInfo("zh-CN");
}
