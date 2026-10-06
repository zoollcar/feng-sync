using System.Globalization;
using System.Resources;
using FengSync.Core.Configuration;

namespace FengSync.Localization;

/// <summary>
/// Reads one string out of the resource set for a language. The lookup is deliberately **not** silent:
/// a key that names nothing comes back as the key itself rather than as an empty string, because an empty
/// label is a layout bug nobody can trace while <c>Mgr.NewEndpoint</c> on a button is visible from the
/// first screenshot. <see cref="FengSync.UiTests.LanguageCoverageTests"/> fails the gate on any of them.
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Manager =
        new("FengSync.Localization.Strings", typeof(Strings).Assembly);

    /// <summary>
    /// Reads <paramref name="key"/> in <paramref name="language"/>.
    /// </summary>
    /// <remarks>
    /// The neutral (Simplified Chinese) set is what a language without its own satellite falls back to, and
    /// that fallback is Chinese rather than English on purpose: this application was written in Chinese, so a
    /// missing English string degrades to the text that has always been there instead of to a second language
    /// the user did not choose.
    /// </remarks>
    internal static string Get(string key, AppLanguage language)
    {
        var culture = language == AppLanguage.English ? CultureInfo.GetCultureInfo("en") : CultureInfo.InvariantCulture;
        try { return Manager.GetString(key, culture) ?? key; }
        catch (MissingManifestResourceException) { return key; }
    }
}

/// <summary>
/// The one way production code reads a localized string: <c>Tr.F("Mgr.NewEndpoint")</c>. A method rather than
/// a property per key, because a generated accessor class would be a second file to keep in step with the
/// resx and a compile error the moment they drift; the coverage test is what keeps the two honest here.
/// </summary>
internal static class Tr
{
    public static string F(string key) => AppLanguageContext.Current[key];

    /// <summary>
    /// A string with holes in it. The format string is ours and the values are the arguments, never the
    /// other way round, so a profile called <c>{0}</c> is text and not a format string.
    /// </summary>
    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, F(key), arguments);
}
