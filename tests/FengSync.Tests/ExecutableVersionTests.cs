using System.Text.RegularExpressions;

namespace FengSync.Tests;

/// <summary>
/// Every executable that ships in a release package must carry a version, and they must all agree.
/// <c>FengSync.exe</c> and <c>FengSync.Cli.exe</c> sit in the same folder, so a stale or missing
/// <c>&lt;Version&gt;</c> shows up as two different product versions in one package: the CLI is copied from its
/// own output directory, which only inherits the release version when the publish happens to pass
/// <c>-p:Version</c>. Declaring it per project makes the version correct for a plain build too.
/// </summary>
public sealed class ExecutableVersionTests
{
    private static readonly Regex OutputType = new(@"<OutputType>(?<value>[^<]+)</OutputType>", RegexOptions.CultureInvariant);
    private static readonly Regex Version = new(@"<Version>(?<value>[^<]+)</Version>", RegexOptions.CultureInvariant);

    [Fact]
    public void Every_executable_project_declares_its_own_version()
    {
        var projects = ExecutableProjects().ToArray();

        // A guard that silently matches nothing would pass forever, so pin the inventory it must cover.
        var discovered = projects.Select(x => x.RelativePath).ToArray();
        foreach (var expected in new[] { "src/FengSync/FengSync.csproj", "src/FengSync.Cli/FengSync.Cli.csproj", "src/FengSync.DriveHub/FengSync.DriveHub.csproj", "src/FengSync.Updater/FengSync.Updater.csproj" })
            Assert.Contains(expected, discovered);

        var missing = projects
            .Where(project => Version.Match(project.Content) is { Success: false })
            .Select(project => project.RelativePath)
            .ToArray();

        Assert.True(missing.Length == 0,
            $"These projects produce an executable but declare no <Version>, so a plain build stamps them 1.0.0:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
    }

    [Fact]
    public void All_executable_projects_declare_the_same_version()
    {
        var versions = ExecutableProjects()
            .Select(project => (project.RelativePath, Version: Version.Match(project.Content)))
            .Where(x => x.Version.Success)
            .Select(x => (x.RelativePath, Value: x.Version.Groups["value"].Value.Trim()))
            .ToArray();

        var distinct = versions.Select(x => x.Value).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(distinct.Length == 1,
            $"Executables ship together and must share one version, found {string.Join(", ", distinct)}:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, versions.Select(x => $"{x.RelativePath} -> {x.Value}"))}");
    }

    [Fact]
    public void Declared_version_is_a_three_part_release_number()
    {
        foreach (var (path, value) in ExecutableProjects()
            .Select(project => (project.RelativePath, Version: Version.Match(project.Content)))
            .Where(x => x.Version.Success)
            .Select(x => (x.RelativePath, Value: x.Version.Groups["value"].Value.Trim())))
        {
            Assert.True(FengSync.Core.Updates.ReleaseVersion.TryParse(value, out _),
                $"{path} declares <Version>{value}</Version>, which the updater cannot parse as a release version.");
        }
    }

    private static IEnumerable<Project> ExecutableProjects()
    {
        var sourceDirectory = Path.Combine(RepositoryRoot(), "src");
        return Directory.EnumerateFiles(sourceDirectory, "*.csproj", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new Project(Path.GetRelativePath(RepositoryRoot(), path).Replace('\\', '/'), File.ReadAllText(path)))
            .Where(project => OutputType.Match(project.Content) is { Success: true } match
                && match.Groups["value"].Value.Trim() is "Exe" or "WinExe");
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

    private sealed record Project(string RelativePath, string Content);
}
