using System.Windows.Markup;

namespace FengSync.Localization;

/// <summary>
/// Hands <see cref="AppLanguageContext.Current"/> to a XAML binding as its source, so a page can write
/// <c>Text="{Binding [Key], Source={l:Lang}}"</c>.
/// </summary>
/// <remarks>
/// It is a markup extension and not a per-page resource because a resource would have to be declared in
/// every one of the twenty page XAML files, and a shared key is one fewer thing to forget. It is a markup
/// extension and not a generated per-key property because the source has to stay one long-lived object
/// that raises a change — see <see cref="AppLanguageContext"/> — and a property per key would not.
/// </remarks>
[MarkupExtensionReturnType(typeof(AppLanguageContext))]
public sealed class LangExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => AppLanguageContext.Current;
}
