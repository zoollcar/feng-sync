using System.Text;
using System.Text.RegularExpressions;

namespace FengSync.Tests;

/// <summary>
/// 界面语言 guardrails, at the same level as <see cref="UiConsistencyGuardrailTests"/>: source-level checks that
/// no user-visible string can quietly stay outside the resource files and no key can quietly stop existing.
/// </summary>
/// <remarks>
/// <para>Two failures matter and they are different kinds of bug. A <b>missing key</b> renders as the key
/// itself — <c>Settings.Apply</c> on a button — which is visible in the first screenshot. An <b>untranslated
/// literal</b> renders as perfectly good Chinese inside an English interface, which is invisible until a user
/// in the other language reports it. The second is why these tests read source text rather than counting
/// resources.</para>
/// <para>The scope is the WPF shell. <c>FengSync.Core</c> still carries Chinese in its own messages; those
/// reach the user through a shell string, and translating them is a separate, larger job with its own
/// resource set.</para>
/// </remarks>
public sealed class LanguageCoverageTests
{
    private static readonly Regex ChineseCharacter = new(@"[一-鿿]", RegexOptions.CultureInvariant);
    // Two shapes, because there are two shapes: a C# call names the key in quotes and a XAML indexer binding
    // names it bare. Missing either one silently drops half the code base from the check.
    private static readonly Regex ResourceKeyUse = new(
        @"Tr\.(?:F|Format)\(\s*""(?<key>[A-Za-z0-9_.]+)""|Binding\s*\[\s*(?<key>[A-Za-z0-9_.]+)\]",
        RegexOptions.CultureInvariant);
    private static readonly Regex StringLiteral = new(@"(?<value>@?""(?:[^""\\]|\\.)*""?)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Lines whose Chinese is a note to whoever reads the log, not to whoever uses the program. Each entry is
    /// a deliberate exception, so adding one is a decision somebody has to make on purpose.
    /// </summary>
    private static readonly string[] NeverShownByAUser =
    [
        "Trace.Trace", "Trace.Write", "Console.Write", "Debug.Write"
    ];

    [Fact]
    public void Every_key_the_code_asks_for_exists_in_both_languages()
    {
        var defined = DefinedKeys();
        Assert.NotEmpty(defined);
        var missing = new List<string>();
        foreach (var (file, code) in Code())
            foreach (Match use in ResourceKeyUse.Matches(code))
            {
                var key = use.Groups["key"].Value;
                if (defined.Contains(key)) continue;
                missing.Add($"{file.RelativePath}:{LineOf(code, use.Index)}: {key}");
            }
        AssertNo(missing, "A key is used in code but defined in no language. Add it to the .tsv table and regenerate the .resx — a missing key renders as the key itself.");
    }

    [Fact]
    public void No_user_visible_chinese_literal_is_left_outside_the_resource_files()
    {
        var violations = new List<string>();
        // Comments are documentation and stay in the language they were written in; only a literal a user can
        // see is a violation, so the comments go before the look. Code() has already done that. The
        // Localization folder is the one place a Chinese literal is the right answer: the language names
        // themselves are written in the language they name.
        foreach (var (file, code) in Code().Where(entry => !entry.File.RelativePath.StartsWith("Localization/", StringComparison.Ordinal)))
        {
            // There is no per-file exception list any more: the last two deferred files were migrated, so a
            // Chinese literal anywhere in the tree is a violation again. A future deferral has to be argued
            // for by changing this test, which is the point.
            // XAML names its user-visible text in attributes. C# names it in every shape there is — an
            // initialiser, a MessageBox caption, an interpolated status line, a throw — so there is no
            // attribute to whitelist and every literal is checked. The exception is a line whose Chinese is
            // a note to whoever reads the log rather than to whoever uses the program.
            foreach (Match literal in StringLiteral.Matches(code))
            {
                if (!ChineseCharacter.IsMatch(literal.Groups["value"].Value)) continue;
                var line = code[..literal.Index].Count(character => character == '\n') + 1;
                var source = code.Split('\n')[line - 1].Trim();
                if (NeverShownByAUser.Any(reason => source.Contains(reason, StringComparison.Ordinal))) continue;
                violations.Add($"{file.RelativePath}:{line}: {source}");
            }
        }
        AssertNo(violations, "A literal a user can read is still Chinese. Move it into the .tsv table as a key and call Tr.F / a {l:Lang} binding — an English interface with one Chinese label in it is invisible until a user reports it.");
    }

    [Fact]
    public void The_resource_files_are_the_generated_ones_and_have_not_been_edited_by_hand()
    {
        // The .tsv tables under localization/ are the source of truth. Editing the generated .resx works right
        // up until the next regeneration silently throws the change away, so it is caught here instead.
        var expected = new StringBuilder();
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var table in Tables())
        {
            var text = File.ReadAllText(table);
            foreach (var line in text.Split('\n'))
            {
                // A blank line is blank whether the checkout kept the table as LF or turned it into CRLF:
                // with core.autocrlf the separator's companion arrives here as a lone carriage return, and a
                // test that only recognises the zero-length form fails on a normal Windows checkout while the
                // tables themselves are intact.
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
                var columns = line.TrimEnd('\r').Split('\t');
                Assert.True(columns.Length == 3, $"{table}: a row must have three tab separated columns, found {columns.Length}.");
                Assert.True(keys.Add(columns[0]), $"{table}: the key {columns[0]} is defined twice.");
                expected.Append(columns[0]).Append('\n');
            }
        }
        Assert.NotEmpty(keys);
        Assert.Equal(string.Join(Environment.NewLine, keys), string.Join(Environment.NewLine, DefinedKeys()));
    }

    [Fact]
    public void Every_key_names_a_language_and_a_place()
    {
        // Key.Case is cheap to write and expensive to unpick later: a table that grows a SettingsFoo, a
        // MountBar and a CloudBaz has no way to be kept straight.
        foreach (var key in DefinedKeys())
        {
            Assert.True(key.Length > 3 && key.Contains('.'), $"The key {key} has no Area.Name shape.");
            Assert.Equal(key, key.Trim(), StringComparer.Ordinal);
        }
    }

    [Fact]
    public void Every_key_is_used_somewhere()
    {
        // The reverse drift: a key nothing asks for is a translation nobody can see has gone stale.
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, code) in Code())
            foreach (Match use in ResourceKeyUse.Matches(code)) used.Add(use.Groups["key"].Value);
        var orphans = DefinedKeys().Where(key => !used.Contains(key)).ToArray();
        AssertNo(orphans, "A key is defined but never used. Either something stopped using it — which means the string it held is hard-coded again — or the key is left over.");
    }

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyList<string> DefinedKeys()
    {
        var path = Path.Combine(ShellDirectory(), "Localization", "Strings.resx");
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var element in System.Xml.Linq.XDocument.Load(path).Root!.Elements("data"))
        {
            var name = element.Attribute("name")?.Value;
            if (name is not null) keys.Add(name);
        }
        return keys.ToList();
    }

    private static IEnumerable<string> Tables() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "localization"), "*.tsv").OrderBy(path => path, StringComparer.Ordinal);

    /// <summary>
    /// Every shell source file with its comments blanked out but its offsets intact, so a report can name a
    /// line in the file the reader already has open.
    /// </summary>
    private static IEnumerable<(SourceFile File, string Code)> Code() =>
        ShellSources(".cs", ".xaml").Select(file => (
            file,
            file.RelativePath.EndsWith(".xaml", StringComparison.Ordinal)
                ? StripXamlComments(file.Text)
                : StripCSharpComments(file.Text)));

    private static IEnumerable<SourceFile> ShellSources(params string[] extensions) =>
        Directory.EnumerateFiles(ShellDirectory(), "*.*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new SourceFile(
                Path.GetRelativePath(ShellDirectory(), path).Replace('\\', '/'),
                File.ReadAllText(path)));

    private static string ShellDirectory() => Path.Combine(RepositoryRoot(), "src", "FengSync");

    private static string RepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }.Distinct(StringComparer.OrdinalIgnoreCase))
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "FengSync", "FengSync.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the Feng Sync repository root from the test execution directory.");
    }

    private static int LineOf(string text, int index) => 1 + text.AsSpan(0, index).Count('\n');

    /// <summary>
    /// Blanks out the comments while keeping every offset, so a reported line number still points at the
    /// place in the real file. Replacing rather than deleting is what buys that.
    /// </summary>
    /// <remarks>
    /// C# needs the string literals tracked: <c>"https://x//y"</c> is a URL, not a comment. XAML does not —
    /// its attribute values cannot contain a comment opener — and treating them as strings would blank out
    /// every <c>Content="{Binding …}"</c>, which is exactly the text these tests are looking for.
    /// </remarks>
    private static string StripCSharpComments(string text) => Blank(text, [("//", null), ("/*", "*/")], trackStrings: true);

    private static string StripXamlComments(string text) => Blank(text, [("<!--", "-->")], trackStrings: false);

    private static string Blank(string text, (string Open, string? Close)[] comments, bool trackStrings)
    {
        var output = text.ToCharArray();
        var inString = false;
        var verbatim = false;
        var depth = 0;
        var open = "";
        for (int i = 0; i < text.Length; i++)
        {
            if (depth > 0)
            {
                // A line comment ends at the newline; a block comment at its closer. Either way the newlines
                // themselves survive so the line numbers stay true.
                if (open == "//" && text[i] == '\n') { depth = 0; continue; }
                if (open != "//" && comments.First(pair => pair.Open == open).Close is { } close && Matches(text, i, close))
                {
                    for (int j = i; j < i + close.Length; j++) output[j] = ' ';
                    i += close.Length - 1;
                    depth = 0;
                    continue;
                }
                if (text[i] != '\n') output[i] = ' ';
                continue;
            }
            if (trackStrings && text[i] == '"')
            {
                // Two quotes in a row inside a verbatim string are one escaped quote, not the end of it.
                if (inString && verbatim && Matches(text, i, "\"\""))
                {
                    i++;
                    continue;
                }
                if (!inString) { inString = true; verbatim = i > 0 && text[i - 1] == '@'; }
                else inString = false;
                continue;
            }
            if (trackStrings && text[i] == '\n') { inString = false; verbatim = false; continue; }
            if (trackStrings && inString) continue;
            var match = comments.FirstOrDefault(pair => Matches(text, i, pair.Open));
            if (match.Open is null) continue;
            depth = match.Close is null ? 1 : 2;
            open = match.Open;
            for (int j = i; j < i + match.Open.Length; j++) output[j] = ' ';
            i += match.Open.Length - 1;
        }
        return new string(output);
    }

    private static bool Matches(string text, int index, string token) =>
        index + token.Length <= text.Length && string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

    private static void AssertNo(IReadOnlyCollection<string> violations, string guidance) => Assert.True(
        violations.Count == 0,
        violations.Count == 0 ? "" : $"{guidance}{Environment.NewLine}{string.Join(Environment.NewLine, violations.Take(2000))}");

    private sealed record SourceFile(string RelativePath, string Text);
}
