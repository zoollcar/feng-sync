using System.Diagnostics;

namespace FengSync.Core.Updates;

/// <summary>
/// Performs the client-side half of a portable update.  Keeping this outside the
/// WPF dialog makes the download, validation and hand-off independently testable.
/// The standalone updater remains responsible for replacing the installation.
/// </summary>
public sealed class UpdateInstallWorkflow
{
    private readonly HttpClient _http;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Action<string, string> _copyFile;

    public UpdateInstallWorkflow(HttpClient http, Func<ProcessStartInfo, Process?>? startProcess = null, Action<string, string>? copyFile = null)
    {
        _http = http;
        _startProcess = startProcess ?? Process.Start;
        _copyFile = copyFile ?? ((source, destination) => File.Copy(source, destination, true));
    }

    public async Task<UpdateInstallHandoff> DownloadValidateAndLaunchAsync(
        GitHubReleaseInfo release, string executable, string baseDirectory, string currentVersion,
        IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!InstallationSafety.TryValidate(executable, Path.Combine(Path.GetTempPath(), "FengSync", "updates", "placeholder"), baseDirectory, out var installation, out var safety))
            throw new InvalidOperationException(safety ?? "安装目录验证失败。");

        var package = await new UpdatePackageDownloader(_http).DownloadAsync(release.DownloadUrl, release.Sha256Url, progress, cancellationToken);
        try
        {
            var staging = await new UpdatePackageExtractor().ExtractAndValidateAsync(package.ZipPath, package.TaskDirectory, release.Tag, cancellationToken);
            var updater = Path.Combine(staging, "FengSync.Updater.exe");
            var detached = Path.Combine(package.TaskDirectory, "FengSync.Updater.exe");
            var detachedTemporary = detached + ".tmp";
            _copyFile(updater, detachedTemporary);
            // Flush the completed copy before atomically publishing its name. A
            // crash can leave only the recognizable .tmp file, never a partial
            // executable at the path passed to Process.Start.
            using (var copied = new FileStream(detachedTemporary, FileMode.Open, FileAccess.Write, FileShare.Read))
                copied.Flush(flushToDisk: true);
            File.Move(detachedTemporary, detached, overwrite: true);

            var oldManifest = Path.Combine(installation, "release-manifest.json");
            var start = new ProcessStartInfo(detached) { UseShellExecute = false, WorkingDirectory = package.TaskDirectory };
            foreach (var argument in new[]
            {
                "--wait-pid", Environment.ProcessId.ToString(), "--staging", staging, "--installation", installation,
                "--executable", Path.GetFullPath(executable), "--old-manifest", File.Exists(oldManifest) ? oldManifest : "",
                "--new-manifest", Path.Combine(staging, "release-manifest.json"), "--from-version", currentVersion,
                "--to-version", release.Tag.TrimStart('v')
            }) start.ArgumentList.Add(argument);
            if (_startProcess(start) is null) throw new InvalidOperationException("无法启动更新器。");
            return new(package.TaskDirectory, staging, installation, detached);
        }
        catch
        {
            // No failed hand-off artifact is reusable: remove the detached copy,
            // its temporary file, the extracted staging tree and downloaded ZIP.
            try { Directory.Delete(package.TaskDirectory, recursive: true); } catch { }
            throw;
        }
    }
}

public sealed record UpdateInstallHandoff(string TaskDirectory, string StagingDirectory, string InstallationDirectory, string DetachedUpdaterPath);
