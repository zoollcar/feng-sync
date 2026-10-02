using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// The standalone transfer window (独立传输窗口). It is deliberately a separate window rather than an
/// inline panel: uploads and downloads keep running while the user browses another directory, and the
/// window is the single place that owns per-task progress, cancellation, and 断点续传.
/// </summary>
public partial class CloudTransferWindow : Window
{
    private readonly CloudTransferQueue _queue;

    public CloudTransferWindow(CloudTransferQueue queue)
    {
        _queue = queue;
        InitializeComponent();
        TaskList.ItemsSource = _queue.Tasks;
        _queue.PropertyChanged += Queue_PropertyChanged;
        // The queue is application-owned, so another window may enqueue while this one is open.
        _queue.Tasks.CollectionChanged += Tasks_CollectionChanged;
        Loaded += async (_, _) => await LoadResumableAsync();
        Closed += (_, _) =>
        {
            _queue.PropertyChanged -= Queue_PropertyChanged;
            _queue.Tasks.CollectionChanged -= Tasks_CollectionChanged;
        };
        UpdateState();
    }

    public CloudTransferQueue Queue => _queue;

    private void Tasks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateState();

    private async Task LoadResumableAsync()
    {
        try { await _queue.LoadResumableAsync(); }
        catch (Exception ex) { HintText.Text = "无法读取上次未完成的传输：" + RcloneUiError.Describe(ex, "cloud-transfer-journal"); }
        UpdateState();
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
        ResumeButton.IsEnabled = _queue.HasResumableTasks;
        CancelAllButton.IsEnabled = _queue.HasActiveTransfers;
        ClearButton.IsEnabled = _queue.HasCompletedTasks;
        HintText.Text = _queue.HasTasks
            ? "取消或失败的传输会保留在列表中，可随时继续。本程序沿用同步任务的暂存与断点机制：能校验已传分片的传输从断点继续，云端传输受 rclone 限制会从文件开头重新开始。"
            : "把本地文件或文件夹拖入文件浏览器即可上传；把云端文件拖到资源管理器即可下载。";
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        var resumed = _queue.ResumeFinished();
        UpdateState();
        if (resumed == 0) HintText.Text = "当前没有可断点续传的任务。";
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

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CloudTransferTask task) return;
        _queue.Cancel(task);
        UpdateState();
    }
}
