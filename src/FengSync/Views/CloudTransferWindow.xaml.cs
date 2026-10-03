using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FengSync.Core.Configuration;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// The standalone transfer window (独立传输窗口). It is deliberately a separate window rather than an
/// inline panel: uploads and downloads keep running while the user browses another directory, and the
/// window is the single place that owns per-task progress, pausing, cancellation, and 断点续传.
/// </summary>
public partial class CloudTransferWindow : Window
{
    /// <summary>The concurrency values the picker offers; the queue clamps anything else anyway.</summary>
    private static readonly int[] ConcurrencyOptions = [1, 2, 3, 4, 6, 8];

    private readonly CloudTransferQueue _queue;
    private readonly CloudCompletionNotifier _notifier;

    public CloudTransferWindow(CloudTransferQueue queue)
    {
        _queue = queue;
        _notifier = new CloudCompletionNotifier(queue);
        InitializeComponent();
        TaskList.ItemsSource = _queue.Tasks;
        _queue.PropertyChanged += Queue_PropertyChanged;
        _notifier.Notified += Notifier_Notified;
        // The queue is application-owned, so another window may enqueue while this one is open.
        _queue.Tasks.CollectionChanged += Tasks_CollectionChanged;
        Loaded += async (_, _) => await LoadResumableAsync();
        Closed += (_, _) =>
        {
            _queue.PropertyChanged -= Queue_PropertyChanged;
            _queue.Tasks.CollectionChanged -= Tasks_CollectionChanged;
            _notifier.Notified -= Notifier_Notified;
            _notifier.Dispose();
        };
        BuildConcurrencyOptions();
        UpdateState();
    }

    public CloudTransferQueue Queue => _queue;

    /// <summary>
    /// Fills the picker from the queue's own current value, so a concurrency changed elsewhere — the
    /// settings page, or a previous session of this window — is what the box shows rather than the default.
    /// Rebuilt from scratch because the settings load can lower or raise the current value afterwards.
    /// </summary>
    private void BuildConcurrencyOptions()
    {
        ConcurrencyBox.SelectionChanged -= Concurrency_Changed;
        ConcurrencyBox.Items.Clear();
        foreach (var option in ConcurrencyOptions) ConcurrencyBox.Items.Add(option);
        if (!ConcurrencyOptions.Contains(_queue.Concurrency)) ConcurrencyBox.Items.Add(_queue.Concurrency);
        ConcurrencyBox.SelectedItem = _queue.Concurrency;
        ConcurrencyBox.SelectionChanged += Concurrency_Changed;
    }

    private void Tasks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateState();

    private async Task LoadResumableAsync()
    {
        try
        {
            // The window is also reachable from DriveHub, which has no main window to push settings onto the
            // shared queue, so the queue is configured from here too. Assigning the values twice is harmless.
            var settings = (await new SettingsStore().LoadAsync()).Settings;
            _queue.Concurrency = settings.CloudMaxConcurrentTransfers;
            _queue.MaxRetries = settings.CloudRetryCount;
            _notifier.Enabled = settings.CloudNotifyOnCompletion;
            BuildConcurrencyOptions();
            await _queue.LoadResumableAsync();
        }
        catch (Exception ex) { HintText.Text = "无法读取上次未完成的传输：" + RcloneUiError.Describe(ex, "cloud-transfer-journal"); }
        UpdateState();
    }

    private void Notifier_Notified(object? sender, string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Notifier_Notified(sender, message)); return; }
        CompletionText.Text = message;
        CompletionBanner.Visibility = Visibility.Visible;
    }

    private void Queue_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Progress ticks arrive from background threads; the summary and button enablement are UI state.
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateState); return; }
        UpdateState();
    }

    private void UpdateState()
    {
        SummaryText.Text = _queue.SummaryText;
        // A new batch makes the previous batch's "已完成" line stale, so it goes away as soon as work resumes.
        if (_queue.HasActiveTransfers) CompletionBanner.Visibility = Visibility.Collapsed;
        ResumeButton.IsEnabled = _queue.HasResumableTasks;
        PauseAllButton.IsEnabled = _queue.HasActiveTransfers;
        CancelAllButton.IsEnabled = _queue.HasActiveTransfers;
        ClearButton.IsEnabled = _queue.HasCompletedTasks;
        ConcurrencyHint.Text = $"当前 {_queue.Concurrency} 个；调低不会中断正在进行的传输，只是不再补位。"
            + (_queue.MaxRetries > 0 ? $"失败会自动重试 {_queue.MaxRetries} 次。" : "失败不会自动重试。");
        HintText.Text = _queue.HasTasks
            ? "取消、暂停或失败的传输都会保留在列表中，可随时继续或重试。本程序沿用同步任务的暂存与断点机制："
              + "能校验已传分片的传输从断点继续，云端传输受 rclone 限制会从文件开头重新开始。"
            : "把本地文件或文件夹拖入文件浏览器即可上传；把云端文件拖到资源管理器即可下载。";
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        var resumed = _queue.ResumeFinished();
        UpdateState();
        if (resumed == 0) HintText.Text = "当前没有可断点续传的任务。";
    }

    private void PauseAll_Click(object sender, RoutedEventArgs e)
    {
        var paused = _queue.PauseAll();
        UpdateState();
        if (paused == 0) HintText.Text = "当前没有正在进行的传输可以暂停。";
    }

    private void CancelAll_Click(object sender, RoutedEventArgs e)
    {
        _queue.CancelAll();
        UpdateState();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _queue.ClearFinished();
        UpdateState();
    }

    private void PauseTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CloudTransferTask task) return;
        _queue.Pause(task);
        UpdateState();
    }

    private void RestartTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CloudTransferTask task) return;
        _queue.Retry(task);
        UpdateState();
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CloudTransferTask task) return;
        _queue.Cancel(task);
        UpdateState();
    }

    /// <summary>
    /// Applies a new concurrency to the live queue. It is intentionally not persisted here: the window is a
    /// per-session control surface, and the startup default belongs to the settings page where it survives
    /// a restart.
    /// </summary>
    private void Concurrency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ConcurrencyBox.SelectedItem is not int value) return;
        _queue.Concurrency = value;
        UpdateState();
    }
}
