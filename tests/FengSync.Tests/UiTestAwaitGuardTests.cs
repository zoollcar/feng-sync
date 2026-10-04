using System.Text.RegularExpressions;

namespace FengSync.Tests;

/// <summary>
/// A window harness that returns <see cref="Task"/> reports its verdict on that task: <c>RunSta</c> runs the
/// scenario on an STA thread, captures what it threw, and rethrows it from the task it hands back. Calling it
/// without <c>await</c> therefore tests nothing — the assertions keep running on a background thread, their
/// failures land in a task nobody observes, and the test reports green. Worse, those threads overlap the next
/// test's, and two of them building a WPF <c>Application</c> aborts the whole run.
/// </summary>
public sealed class UiTestAwaitGuardTests
{
    private static readonly Regex AsyncHarness =
        new(@"static\s+Task\s+(?<name>RunSta|WithWindow)\s*\(", RegexOptions.CultureInvariant);

    private static readonly Regex HarnessCall = new(@"\b(?:RunSta|WithWindow)\s*\(", RegexOptions.CultureInvariant);

    private static readonly Regex Declaration = new(@"static\s+Task\s+\Z", RegexOptions.CultureInvariant);

    [Fact]
    public void Every_call_to_an_async_window_harness_is_awaited()
    {
        var content = WindowTestFiles().ToArray();
        var violations = content
            .SelectMany(file => UnawaitedCalls(file).Select(index =>
                $"{file.RelativePath}:{LineNumber(file.Content, index)}: {LineAt(file.Content, index)}"))
            .ToArray();

        Assert.True(violations.Length == 0,
            "These lines call a harness that returns Task without awaiting it, so the scenario's assertions "
            + "cannot fail the test. Await the call, and declare the test method 'async Task'."
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void The_guard_is_actually_looking_at_the_async_harnesses()
    {
        // Without this the scan could quietly match nothing — a renamed helper would turn the guard above into a
        // test that passes forever, which is the exact failure mode it exists to catch.
        var files = WindowTestFiles().ToArray();
        Assert.True(files.Length >= 4, $"Expected to scan the in-process window tests, found {files.Length} files.");
        Assert.True(files.Any(file => file.AsyncHarnesses > 0),
            "No window test file defines a Task-returning RunSta/WithWindow, so this guard is scanning nothing.");
        Assert.True(files.Count(file => file.AsyncHarnesses > 0) >= 4,
            $"Expected the four in-process window suites to define the async harness, found "
            + $"{files.Count(file => file.AsyncHarnesses > 0)}.");
    }

    private sealed record ScannedFile(string RelativePath, string Content, int AsyncHarnesses);

    /// <summary>The positions of the harness calls whose statement prefix neither awaits nor forwards the task.</summary>
    private static IEnumerable<int> UnawaitedCalls(ScannedFile file)
    {
        if (file.AsyncHarnesses == 0) yield break;
        foreach (Match match in HarnessCall.Matches(file.Content))
        {
            // A comment naming the helper is documentation, not a call.
            var lineStart = file.Content.LastIndexOf('\n', Math.Max(match.Index - 1, 0)) + 1;
            if (file.Content[lineStart..match.Index].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            var prefix = file.Content[StatementStart(file.Content, match.Index)..match.Index];
            // The harness's own signature is a declaration, not a use: the name itself is the match, so only the
            // return type before it identifies it.
            if (Declaration.IsMatch(prefix)) continue;
            if (prefix.Contains("await", StringComparison.Ordinal)
                || prefix.Contains("return", StringComparison.Ordinal)
                || prefix.TrimEnd().EndsWith("=>", StringComparison.Ordinal)) continue;
            yield return match.Index;
        }
    }

    /// <summary>The end of the previous statement or block — the beginning of the code the call sits in.</summary>
    private static int StatementStart(string content, int index)
    {
        var previous = content.LastIndexOfAny([';', '{', '}'], Math.Max(index - 1, 0));
        return previous < 0 ? 0 : previous + 1;
    }

    private static int LineNumber(string content, int index) => 1 + content.AsSpan(0, index).Count('\n');

    private static string LineAt(string content, int index)
    {
        var start = content.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;
        var end = content.IndexOf('\n', index);
        return content[start..(end < 0 ? content.Length : end)].Trim();
    }

    private static IEnumerable<ScannedFile> WindowTestFiles()
    {
        var directory = Path.Combine(RepositoryRoot(), "tests", "FengSync.UiTests");
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (relative: Path.GetRelativePath(directory, path).Replace('\\', '/'),
                content: File.ReadAllText(path)))
            .Select(file => new ScannedFile(file.relative, file.content, AsyncHarness.Matches(file.content).Count));
    }

    private static string RepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "FengSync.sln"))) return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("找不到包含 FengSync.sln 的仓库根目录。");
    }
}
