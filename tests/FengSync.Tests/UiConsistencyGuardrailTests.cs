using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FengSync.Tests;

/// <summary>
/// Lightweight source-level guardrails for the WPF design system. These checks deliberately
/// allow the remaining unstyled controls while migration is in progress, but prevent new
/// page-local visual primitives from bypassing the shared Fluent resources.
/// </summary>
public sealed class UiConsistencyGuardrailTests
{
    private static readonly Regex HexColor = new(@"(?<![A-Za-z0-9])#[A-Fa-f0-9]{3,8}(?![A-Za-z0-9])", RegexOptions.CultureInvariant);
    private static readonly Regex ButtonStyleReference = new(@"Style\s*=\s*""\{(?:Static|Dynamic)Resource\s+(?<key>[^}\s]+)\}""", RegexOptions.CultureInvariant);
    private static readonly Regex ButtonElement = new(@"<Button\b(?<attributes>[^>]*)>", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex ButtonTemplate = new(@"<Button\.Template\b|<ControlTemplate\b[^>]*\bTargetType\s*=\s*""(?:\{x:Type\s+)?Button\}?""", RegexOptions.CultureInvariant);
    private static readonly Regex PageLocalButtonVisual = new(@"\b(?:Height|MinHeight|Padding|FontSize|Background|Foreground|BorderBrush)\s*=", RegexOptions.CultureInvariant);
    private static readonly Regex TextOrContentAttribute = new(@"(?<property>Content|Text)\s*=\s*""(?<value>[^""]*)""", RegexOptions.CultureInvariant);

    // Page code may use only these shared button variants. Unstyled controls remain allowed
    // during migration because Controls.xaml supplies the shared default Button style.
    private static readonly HashSet<string> ApprovedButtonStyles = new(StringComparer.Ordinal)
    {
        "PrimaryButtonStyle",
        "SecondaryButtonStyle",
        "TertiaryButtonStyle",
        "DangerButtonStyle",
        "IconButtonStyle",
        "DangerIconButtonStyle",
        "EndpointSwapButtonStyle"
    };

    // Command and status iconography uses FluentIcons.Wpf. Keep this inventory empty;
    // a narrowly documented exception may be added only when the glyph is actual content.
    private static readonly HashSet<SymbolUse> ApprovedSymbolUses = [];

    [Fact]
    public void Inline_hexadecimal_colours_are_defined_only_in_design_tokens()
    {
        var violations = ProductionXamlFiles()
            .Where(file => !string.Equals(file.RelativePath, "Themes/DesignTokens.xaml", StringComparison.Ordinal))
            .SelectMany(file => HexColor.Matches(file.Content).Select(match => Describe(file, match, match.Value)))
            .ToArray();

        AssertNoViolations(
            violations,
            "Inline hexadecimal colours belong in Themes/DesignTokens.xaml. Reference a named brush with StaticResource or DynamicResource from page XAML.");
    }

    [Fact]
    public void Button_style_references_use_the_approved_shared_variants()
    {
        var violations = ProductionXamlFiles()
            .SelectMany(file => ButtonStyleReference.Matches(file.Content).Cast<Match>()
                .Where(match => match.Groups["key"].Value.EndsWith("ButtonStyle", StringComparison.Ordinal)
                    && !ApprovedButtonStyles.Contains(match.Groups["key"].Value))
                .Select(match => Describe(file, match, match.Groups["key"].Value)))
            .ToArray();

        AssertNoViolations(
            violations,
            "Button style references must use the approved shared variants. Add a semantic variant to Themes/Controls.xaml and this allowlist before using it in a page.");
    }

    [Fact]
    public void Every_page_button_has_an_explicit_approved_semantic_style()
    {
        var violations = ProductionXamlFiles()
            .Where(file => !file.RelativePath.StartsWith("Themes/", StringComparison.Ordinal))
            .SelectMany(file => ButtonElement.Matches(file.Content).Cast<Match>()
                .Where(match => !ButtonStyleReference.Matches(match.Groups["attributes"].Value).Cast<Match>()
                    .Any(style => ApprovedButtonStyles.Contains(style.Groups["key"].Value)))
                .Select(match => Describe(file, match, "Button has no approved semantic Style")))
            .ToArray();

        AssertNoViolations(
            violations,
            "Every page Button must explicitly declare one approved semantic role. Do not rely on the implicit default style.");
    }

    [Fact]
    public void Pages_do_not_override_shared_button_visual_contracts()
    {
        var violations = ProductionXamlFiles()
            .Where(file => !file.RelativePath.StartsWith("Themes/", StringComparison.Ordinal))
            .SelectMany(file => ButtonElement.Matches(file.Content).Cast<Match>()
                .SelectMany(match => PageLocalButtonVisual.Matches(match.Groups["attributes"].Value)
                    .Select(visual => Describe(file, match, visual.Value.TrimEnd('=')))))
            .ToArray();

        AssertNoViolations(
            violations,
            "Button size, padding, typography and colours belong to shared semantic styles, not page XAML.");
    }

    [Fact]
    public void Pages_do_not_define_button_control_templates()
    {
        var violations = ProductionXamlFiles()
            .Where(file => !file.RelativePath.StartsWith("Themes/", StringComparison.Ordinal))
            .SelectMany(file => ButtonTemplate.Matches(file.Content).Select(match => Describe(file, match, match.Value)))
            .ToArray();

        AssertNoViolations(
            violations,
            "Button templates belong in Themes/Controls.xaml. Pages must consume a shared semantic Button style instead of copying button chrome.");
    }

    [Fact]
    public void Text_and_content_symbol_glyphs_are_an_explicit_migration_inventory()
    {
        var violations = ProductionXamlFiles()
            .SelectMany(file => TextOrContentAttribute.Matches(file.Content).Cast<Match>()
                .Where(match => ContainsSymbolGlyph(match.Groups["value"].Value))
                .Where(match => !ApprovedSymbolUses.Contains(new SymbolUse(
                    file.RelativePath,
                    match.Groups["property"].Value,
                    match.Groups["value"].Value)))
                .Select(match => Describe(file, match, match.Groups["value"].Value)))
            .ToArray();

        AssertNoViolations(
            violations,
            "Text or Content contains a symbol glyph that is not in the temporary migration inventory. Prefer FluentIcons.Wpf; otherwise document the precise existing use in ApprovedSymbolUses.");
    }

    private static bool ContainsSymbolGlyph(string value) => value.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) is UnicodeCategory.MathSymbol
        or UnicodeCategory.CurrencySymbol
        or UnicodeCategory.ModifierSymbol
        or UnicodeCategory.OtherSymbol);

    private static IEnumerable<XamlFile> ProductionXamlFiles()
    {
        var applicationDirectory = Path.Combine(RepositoryRoot(), "src", "FengSync");
        return Directory.EnumerateFiles(applicationDirectory, "*.xaml", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new XamlFile(
                Path.GetRelativePath(applicationDirectory, path).Replace('\\', '/'),
                File.ReadAllText(path)));
    }

    private static string RepositoryRoot()
    {
        foreach (var startDirectory in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "FengSync", "FengSync.csproj")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Feng Sync repository root from the test execution directory.");
    }

    private static string Describe(XamlFile file, Match match, string value) => $"{file.RelativePath}:{LineNumber(file.Content, match.Index)}: {value}";

    private static int LineNumber(string content, int index) => 1 + content.AsSpan(0, index).Count('\n');

    private static void AssertNoViolations(IReadOnlyCollection<string> violations, string guidance) => Assert.True(
        violations.Count == 0,
        $"{guidance}{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");

    private sealed record XamlFile(string RelativePath, string Content);

    private sealed record SymbolUse(string RelativePath, string Property, string Value);
}
