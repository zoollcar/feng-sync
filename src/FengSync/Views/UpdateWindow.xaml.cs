using FengSync.Core.Updates;
using System.Diagnostics;
using System.Windows.Navigation;
using System.Windows;
namespace FengSync.Views;
public partial class UpdateWindow : Window
{
    private readonly GitHubReleaseInfo _release; private CancellationTokenSource? _downloadCancellation; private bool _downloading;
    public bool SkipRequested { get; private set; }
    public event Func<CancellationToken, Task>? DownloadRequested;
    public UpdateWindow(string current, GitHubReleaseInfo release) { InitializeComponent(); _release = release; CurrentVersion.Text = "当前版本：" + current; LatestVersion.Text = $"最新版本：{release.Tag}  {release.Name}"; ReleaseNotes.Text = release.Body.Length > 4000 ? release.Body[..4000] + "…" : release.Body; Progress.Text = $"下载大小：{release.DownloadSize / 1024d / 1024d:N1} MB"; ReleaseLink.NavigateUri = release.HtmlUrl; }
    public void SetDownloadProgress(UpdateDownloadProgress progress)
    {
        var total = progress.TotalBytes is > 0 ? $" / {progress.TotalBytes.Value / 1024d / 1024d:N1} MB" : "";
        Progress.Text = progress.Percentage is { } percentage ? $"正在下载：{progress.ReceivedBytes / 1024d / 1024d:N1} MB{total}（{percentage:N0}%）" : $"正在下载：{progress.ReceivedBytes / 1024d / 1024d:N1} MB";
        DownloadProgressBar.Visibility = Visibility.Visible; DownloadProgressBar.IsIndeterminate = progress.Percentage is null;
        if (progress.Percentage is { } value) DownloadProgressBar.Value = value;
    }
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading || DownloadRequested is null) return;
        _downloading = true; _downloadCancellation = new(); DownloadButton.IsEnabled = LaterButton.IsEnabled = SkipButton.IsEnabled = false; CancelButton.Content = "取消下载";
        try { await DownloadRequested(_downloadCancellation.Token); }
        finally { if (IsVisible) { _downloading = false; _downloadCancellation?.Dispose(); _downloadCancellation = null; DownloadButton.IsEnabled = LaterButton.IsEnabled = SkipButton.IsEnabled = true; CancelButton.Content = "取消"; } }
    }
    private void Later_Click(object sender, RoutedEventArgs e) => Close();
    private void Skip_Click(object sender, RoutedEventArgs e) { SkipRequested = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_downloading) _downloadCancellation?.Cancel(); else Close(); }
    private void ReleaseLink_RequestNavigate(object sender, RequestNavigateEventArgs e) { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; }
    protected override void OnClosed(EventArgs e) { _downloadCancellation?.Cancel(); base.OnClosed(e); }
}
