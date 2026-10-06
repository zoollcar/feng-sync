using System.Text.RegularExpressions;

namespace FengSync.Tests;

/// <summary>
/// A <c>TextBlock</c> whose text is set in code carries live content: a status, a summary, a version. Giving it a
/// static <c>AutomationProperties.Name</c> replaces that content as the accessible name, so a screen reader
/// announces the label instead of the value and a UI Automation client can no longer read it either. The text
/// itself is the right accessible name, so the element needs an <c>AutomationId</c> (for reachability) and no
/// static name. Binding the name to the element's own <c>Text</c> is the one accepted way to be explicit about it.
/// </summary>
public sealed class LiveTextAccessibilityTests
{
    private static readonly Regex TextBlockElement = new(@"<TextBlock\b(?<attributes>[^>]*)>", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex AutomationName = new(@"AutomationProperties\.Name\s*=\s*""(?<value>[^""]*)""", RegexOptions.CultureInvariant);
    private static readonly Regex AutomationId = new(@"AutomationProperties\.AutomationId\s*=\s*""(?<value>[^""]*)""", RegexOptions.CultureInvariant);
    private static readonly Regex StaticText = new(@"\bText\s*=", RegexOptions.CultureInvariant);
    private static readonly Regex SelfBoundName = new(@"\{\s*Binding\s+Text\b", RegexOptions.CultureInvariant);

    [Fact]
    public void A_text_block_whose_text_comes_from_code_has_no_static_accessible_name()
    {
        var violations = PageFiles()
            .SelectMany(file => TextBlockElement.Matches(file.Content).Cast<Match>()
                .Select(match => (file, match, attributes: match.Groups["attributes"].Value))
                // A literal Text= is static content, and that is exactly when a descriptive name helps.
                .Where(x => !StaticText.IsMatch(x.attributes))
                .Select(x => (x.file, x.match, name: AutomationName.Match(x.attributes)))
                .Where(x => x.name.Success && !SelfBoundName.IsMatch(x.name.Groups["value"].Value))
                .Select(x => $"{x.file.RelativePath}:{LineNumber(x.file.Content, x.match.Index)}: AutomationProperties.Name=\"{x.name.Groups["value"].Value}\""))
            .ToArray();

        Assert.True(violations.Length == 0,
            "These TextBlocks render text assigned in code, so their static AutomationProperties.Name hides the "
            + "content from screen readers and from UI Automation. Drop the name (the text becomes the accessible "
            + "name) or bind it to the element's own Text."
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void A_text_block_whose_text_comes_from_code_is_reachable_by_automation_id()
    {
        var violations = PageFiles()
            .SelectMany(file => TextBlockElement.Matches(file.Content).Cast<Match>()
                .Select(match => (file, match, attributes: match.Groups["attributes"].Value))
                .Where(x => !StaticText.IsMatch(x.attributes))
                .Where(x => AutomationName.IsMatch(x.attributes))
                .Where(x => !AutomationId.IsMatch(x.attributes))
                .Select(x => $"{x.file.RelativePath}:{LineNumber(x.file.Content, x.match.Index)}"))
            .ToArray();

        // Without an id and without a name the element is invisible to the desktop harness, which is how a
        // status readout silently stops being asserted on.
        Assert.True(violations.Length == 0,
            "These TextBlocks render live text but carry neither an AutomationId nor a usable accessible name:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void The_scan_covers_the_shell_and_finds_its_text_blocks()
    {
        // A guardrail that scans nothing passes forever.
        var files = PageFiles().ToArray();
        Assert.True(files.Length >= 10, $"Expected to scan the WPF views, found {files.Length} XAML files.");
        Assert.Contains(files, file => file.RelativePath == "Views/MountPickerDialog.xaml");
        Assert.True(files.Sum(file => TextBlockElement.Matches(file.Content).Count) > 50,
            "Expected the shell to contain a substantial number of TextBlocks.");
    }

    [Fact]
    public void The_cloud_file_list_names_every_row_from_the_item_it_shows()
    {
        // Measured against the real shell: a grid tile's inner text never reaches the automation tree (the row is
        // a DataItem with zero children, named by the item's ToString()), so a row in either view is only
        // announceable and only findable by name when the container carries the name itself.
        var file = PageFiles().FirstOrDefault(entry => entry.RelativePath == "Views/RemoteEndpointManagerWindow.xaml");
        Assert.NotNull(file);
        var list = BrowserListElement.Match(file!.Content);
        Assert.True(list.Success,
            "The cloud file list was not found by that scan. Rename the element and this guard needs updating on "
            + "purpose, not by accident.");
        var rowStyle = SetterOnRowStyle.Match(list.Groups["body"].Value);
        Assert.True(rowStyle.Success,
            "FileList needs an ItemContainerStyle that sets AutomationProperties.Name from a binding, because a "
            + "tile's own text is not in the automation tree and the row would otherwise announce the whole "
            + "CloudFileEntry record.");
        Assert.False(StaticRowName.IsMatch(rowStyle.Groups["value"].Value),
            $"The row name is bound to the item, not written as a literal, and this is a literal: "
            + $"'{rowStyle.Groups["value"].Value}'.");
    }

    private static readonly Regex BrowserListElement = new(
        @"<ListView\b[^>]*x:Name=""FileList""[^>]*>(?<body>[\s\S]*?)</ListView>",
        RegexOptions.CultureInvariant);

    private static readonly Regex SetterOnRowStyle = new(
        @"<Style\b[^>]*TargetType=""ListViewItem""[\s\S]*?Setter\s+Property=""AutomationProperties\.Name""\s+Value=""(?<value>[^""]*)""",
        RegexOptions.CultureInvariant);

    private static readonly Regex StaticRowName = new(@"^(?!\{\s*Binding)", RegexOptions.CultureInvariant);

    private static IEnumerable<XamlFile> PageFiles()
    {
        var applicationDirectory = Path.Combine(RepositoryRoot(), "src", "FengSync");
        return Directory.EnumerateFiles(applicationDirectory, "*.xaml", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new XamlFile(
                Path.GetRelativePath(applicationDirectory, path).Replace('\\', '/'),
                File.ReadAllText(path)));
    }

    private static int LineNumber(string content, int index) => 1 + content.AsSpan(0, index).Count('\n');

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

    private sealed record XamlFile(string RelativePath, string Content);
}
