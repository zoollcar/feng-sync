using Xunit;

namespace FengSync.UiTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiAcceptanceCollection : ICollectionFixture<UiArtifactWorkspaceFixture>
{
    public const string Name = "UI acceptance workspace";
}

public sealed class UiArtifactWorkspaceFixture
{
    public UiArtifactWorkspaceFixture()
    {
        var repositoryRoot = FindRepositoryRoot();
        var artifactRoot = Path.GetFullPath(Path.Combine(repositoryRoot, ".fengsync-test"));
        var artifactParent = Directory.GetParent(artifactRoot)?.FullName;
        if (!string.Equals(artifactParent, repositoryRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(artifactRoot), ".fengsync-test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to clear an unexpected test-artifact path: {artifactRoot}");
        }

        if (Directory.Exists(artifactRoot))
            Directory.Delete(artifactRoot, recursive: true);
        Directory.CreateDirectory(artifactRoot);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FengSync.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root containing FengSync.sln was not found.");
    }
}
