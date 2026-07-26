using System.Diagnostics;
using System.IO;
using System.Net.Http;
using FengSync.Core.Configuration;
using FengSync.Core.Updates;
using FengSync.Views;
using System.Windows;

namespace FengSync.Services;

public sealed class UpdateCoordinator
{
    private readonly ApplicationVersionService _versions; private readonly GitHubReleaseClient _client; private readonly Func<ApplicationSettings> _getSettings; private readonly Func<ApplicationSettings, Task> _save; private readonly Func<Task>? _exitForUpdate;
    private bool _autoCheckDeferred;
    public UpdateCoordinator(ApplicationVersionService versions, GitHubReleaseClient client, Func<ApplicationSettings> getSettings, Func<ApplicationSettings, Task> save, Func<Task>? exitForUpdate = null) { _versions = versions; _client = client; _getSettings = getSettings; _save = save; _exitForUpdate = exitForUpdate; }
    public async Task CheckAsync(Window owner, bool manual, bool busy = false)
    {
        if (Environment.GetEnvironmentVariable("FENGSYNC_DISABLE_UPDATE_CHECK") == "1") return;
        var settings = _getSettings();
        if (!manual && !settings.AutoCheckForUpdates) return;
        if (!manual && busy) { _autoCheckDeferred = true; return; }
        if (!manual && settings.LastUpdateCheckUtc is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24)) return;
        var result = await _client.CheckAsync(_versions.CurrentVersion, settings.LatestReleaseEtag);
        if (result.Status is UpdateCheckStatus.UpdateAvailable or UpdateCheckStatus.Latest or UpdateCheckStatus.NotModified)
            await _save(settings with { LastUpdateCheckUtc = DateTimeOffset.UtcNow, LatestReleaseEtag = result.Etag ?? settings.LatestReleaseEtag });
        if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Release is { } release)
        {
            if (!manual && string.Equals(settings.SkippedUpdateVersion, release.Tag, StringComparison.OrdinalIgnoreCase)) return;
            var dialog = new UpdateWindow(_versions.DisplayVersion, release) { Owner = owner };
            dialog.DownloadRequested += token => DownloadAndInstallAsync(owner, release, busy: false, dialog, token);
            dialog.ShowDialog();
            if (dialog.SkipRequested) await _save(_getSettings() with { SkippedUpdateVersion = release.Tag });
            return;
        }
        if (!manual) { if (result.Status is not (UpdateCheckStatus.Latest or UpdateCheckStatus.NotModified)) Trace.WriteLine("更新检查失败：" + result.Error); return; }
        if (result.Status is UpdateCheckStatus.Latest or UpdateCheckStatus.NotModified) MessageBox.Show($"当前已是最新版本 {_versions.DisplayVersion}。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show((result.Error ?? "检查更新失败。") + "\nhttps://github.com/zoollcar/feng-sync/releases", "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    /// <summary>Called by the main window after a sync/compare completes so an automatic check is not lost.</summary>
    public Task CheckDeferredAsync(Window owner, bool busy)
    {
        if (!_autoCheckDeferred || busy) return Task.CompletedTask;
        _autoCheckDeferred = false;
        return CheckAsync(owner, manual: false, busy: false);
    }
    private async Task DownloadAndInstallAsync(Window owner, GitHubReleaseInfo release, bool busy, UpdateWindow dialog, CancellationToken cancellationToken)
    {
        if (busy) { MessageBox.Show("同步或比较进行中，不能安装更新。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) { MessageBox.Show("无法确定实际程序路径。\n请从 Release 页面手动下载。", "无法自动更新", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        string installation = ""; string? safety = "安装目录验证失败。";
        if (!InstallationSafety.TryValidate(executable, Path.Combine(Path.GetTempPath(), "FengSync", "updates", "placeholder"), out installation, out safety)) { MessageBox.Show((safety ?? "安装目录验证失败。") + "\n请从 Release 页面手动下载。", "无法自动更新", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            var workflow = new UpdateInstallWorkflow(new HttpClient());
            var handoff = await workflow.DownloadValidateAndLaunchAsync(release, executable, AppContext.BaseDirectory, _versions.CurrentVersion.ToString(), new Progress<UpdateDownloadProgress>(dialog.SetDownloadProgress), cancellationToken);
            dialog.SetDownloadProgress(new UpdateDownloadProgress(release.DownloadSize, release.DownloadSize));
            owner.Title = "FengSync - 正在退出并安装更新…";
            if (_exitForUpdate is not null) await _exitForUpdate();
        }
        catch (OperationCanceledException) { dialog.SetDownloadProgress(new UpdateDownloadProgress(0, null)); }
        catch (Exception ex) { MessageBox.Show(ex.Message + "\n请从 Release 页面手动下载。", "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
