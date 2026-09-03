using System.Diagnostics;
using System.IO.Compression;
using FengSync.Core.Updates;

namespace FengSync.Tests;

public sealed class ReleaseManifestTests
{
    [Fact]
    public void Validator_requires_product_runtime_version_and_sorted_safe_files()
    {
        var invalid = new ReleaseManifest("Other", "1.0.0-beta", "linux-x64", [new("b.bin", 1, new string('a', 64)), new("a.bin", -1, "bad")]);
        Assert.NotEmpty(ReleaseManifestValidator.Validate(invalid));
    }

    [Fact]
    public void Validator_reports_sorting_error_only_once()
    {
        var files = new[] { "z.bin", "b.bin", "a.bin" }.Select(path => new ReleaseManifestFile(path, 1, new string('a', 64))).ToArray();
        Assert.Single(ReleaseManifestValidator.Validate(new("FengSync", "0.2.0", "win-x64", files)));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    public async Task Release_script_generates_manifest_accepted_by_update_client(string culture)
    {
        var root = Path.Combine(Path.GetTempPath(), "FengSync-tests", Guid.NewGuid().ToString("N"));
        var publish = Path.Combine(root, "publish");
        Directory.CreateDirectory(publish);
        try
        {
            string[] paths = ["a.bin", "Z.bin", "FengSync.exe", "FengSync.Updater.exe", "Assets/rclone/rclone.exe", "de/UIAutomationTypes.resources.dll", "de/System.Xaml.resources.dll", "_data.bin"];
            foreach (var path in paths)
            {
                var fullPath = Path.Combine(publish, path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, path);
            }
            // Run the actual release script, including regeneration over an existing manifest.
            var runner = Path.Combine(root, "generate.ps1");
            await File.WriteAllTextAsync(runner, """
                param($Script, $PublishDirectory, $Culture)
                $ErrorActionPreference = 'Stop'
                [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($Culture)
                & $Script -PublishDirectory $PublishDirectory -Version '0.2.0'
                & $Script -PublishDirectory $PublishDirectory -Version '0.2.0'
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", runner, Path.Combine(FindRepositoryRoot(), "scripts", "New-ReleaseManifest.ps1"), publish, culture })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"{await stdout}\n{await stderr}");
            var manifest = await ReleaseManifest.LoadAsync(Path.Combine(publish, "release-manifest.json"));
            Assert.Equal(paths.OrderBy(path => path, StringComparer.Ordinal), manifest.Files.Select(file => file.Path));
            Assert.Empty(await ReleaseManifestValidator.ValidateFilesAsync(manifest, publish));
            var zip = Path.Combine(root, "package.zip");
            ZipFile.CreateFromDirectory(publish, zip);
            Assert.True(Directory.Exists(await new UpdatePackageExtractor().ExtractAndValidateAsync(zip, root, "v0.2.0")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FengSync.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root containing FengSync.sln was not found.");
    }
}
