using FengSync.Core.Updates;
using FengSync.Localization;
using System.Diagnostics;
using System.Windows.Navigation;
using System.Windows;
namespace FengSync.Views;
public partial class UpdateWindow : Window
{
    private readonly GitHubReleaseInfo _release; private CancellationTokenSource? _downloadCancellation; private bool _downloading;
    public bool SkipRequested { get; private set; }
    public event Func<CancellationToken, Task>? DownloadRequested;
    public UpdateWindow(string current, GitHubReleaseInfo release) { InitializeComponent(); _release = release; CurrentVersion.Text = Tr.Format("Update.CurrentVersion", current); LatestVersion.Text = Tr.Format("Update.LatestVersion", release.Tag, release.Name); ReleaseNotes.Text = release.Body.Length > 4000 ? release.Body[..4000] + "…" : release.Body; Progress.Text = Tr.Format("Update.DownloadSize", (release.DownloadSize / 1024d / 1024d).ToString("N1")); ReleaseLink.NavigateUri = release.HtmlUrl; }
    public void SetDownloadProgress(UpdateDownloadProgress progress)
    {
        var total = progress.TotalBytes is > 0 ? $" / {progress.TotalBytes.Value / 1024d / 1024d:N1} MB" : "";
        Progress.Text = progress.Percentage is { } percentage
            ? Tr.Format("Update.DownloadingPercent", (progress.ReceivedBytes / 1024d / 1024d).ToString("N1"), total, percentage.ToString("N0"))
            : Tr.Format("Update.Downloading", (progress.ReceivedBytes / 1024d / 1024d).ToString("N1"));
        DownloadProgressBar.Visibility = Visibility.Visible; DownloadProgressBar.IsIndeterminate = progress.Percentage is null;
        if (progress.Percentage is { } value) DownloadProgressBar.Value = value;
    }
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading || DownloadRequested is null) return;
        _downloading = true; _downloadCancellation = new(); DownloadButton.IsEnabled = LaterButton.IsEnabled = SkipButton.IsEnabled = false; CancelButton.Content = Tr.F("Update.CancelDownload");
        try { await DownloadRequested(_downloadCancellation.Token); }
        finally { if (IsVisible) { _downloading = false; _downloadCancellation?.Dispose(); _downloadCancellation = null; DownloadButton.IsEnabled = LaterButton.IsEnabled = SkipButton.IsEnabled = true; CancelButton.Content = Tr.F("Update.Cancel"); } }
    }
    private void Later_Click(object sender, RoutedEventArgs e) => Close();
    private void Skip_Click(object sender, RoutedEventArgs e) { SkipRequested = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_downloading) _downloadCancellation?.Cancel(); else Close(); }
    private void ReleaseLink_RequestNavigate(object sender, RequestNavigateEventArgs e) { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; }
    protected override void OnClosed(EventArgs e) { _downloadCancellation?.Cancel(); base.OnClosed(e); }
}
