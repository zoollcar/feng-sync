using System.Diagnostics;
using System.IO;
using System.Windows;
using FengSync.Core;
using Microsoft.Win32;

namespace FengSync;
public partial class ProgressWindow : Window
{
    private readonly int _total; private int _done; private long _bytes; private readonly HashSet<Guid> _completed = []; private readonly Stopwatch _clock = Stopwatch.StartNew();
    private SyncRunResult? _result;
    private IReadOnlyList<SyncOperation> _originalOperations = [];
    private Func<SyncPlan, Task<SyncRunResult>>? _retry;
    public ProgressWindow(int total, bool autoClose) { InitializeComponent(); _total = Math.Max(1, total); AutoClose.IsChecked = autoClose; Counter.Text = $"0 / {total}"; FileProgress.Maximum = _total; }
    public void ShowInitialization(string phase, string detail)
    {
        StateTitle.Text = "正在初始化同步";
        StateDescription.Text = $"{phase} · {detail}";
        Counter.Text = phase;
        CurrentFile.Text = detail;
        FileProgress.IsIndeterminate = true;
        BytesText.Text = "传输尚未开始";
        SpeedText.Text = "正在准备";
    }
    public void BeginTransfers(int concurrency)
    {
        FileProgress.IsIndeterminate = false;
        StateTitle.Text = "正在同步";
        StateDescription.Text = $"初始化完成，正在以 {concurrency} 路并发传输…";
        Counter.Text = $"0 / {_total}";
        CurrentFile.Text = "等待传输任务…";
    }
    public void Report(string path) { _done++; FileProgress.Value = _done; BytesGraph.Value = _done * 100.0 / _total; SpeedGraph.Value = Math.Min(100, _done * 100.0 / Math.Max(1, _clock.Elapsed.TotalSeconds)); Counter.Text = $"{_done} / {_total}"; CurrentFile.Text = path; BytesText.Text = $"已完成 {_done} 个文件"; SpeedText.Text = $"{_done / Math.Max(.1, _clock.Elapsed.TotalSeconds):N1} 文件/秒"; }
    public void Report(TransferProgress progress)
    {
        CurrentFile.Text = progress.Path;
        if (progress.Stage == TransferStage.Failed) { StateDescription.Text = "传输失败：" + (progress.Error ?? progress.Path); return; }
        if (progress.Stage != TransferStage.Committed || !_completed.Add(progress.OperationId)) return;
        _done++; _bytes += progress.BytesCompleted; FileProgress.Value = _done; BytesGraph.Value = _done * 100d / _total;
        Counter.Text = $"{_done} / {_total}"; BytesText.Text = $"已完成 {_done} 个文件，{_bytes:N0} B";
        SpeedGraph.Value = Math.Min(100, _bytes / Math.Max(1, _clock.Elapsed.TotalSeconds) / 1024 / 1024 * 10);
        SpeedText.Text = $"{_bytes / Math.Max(.1, _clock.Elapsed.TotalSeconds) / 1024 / 1024:N1} MB/秒 · {progress.ActiveTransfers} 项并发";
    }
    public void SetRetry(IReadOnlyList<SyncOperation> originalOperations, Func<SyncPlan, Task<SyncRunResult>> retry)
        => (_originalOperations, _retry) = (originalOperations, retry);
    public void Complete(SyncRunResult result, string text, bool cancelled = false)
    {
        FileProgress.IsIndeterminate = false;
        _result = result; OperationResults.ItemsSource = result.Operations;
        var outcome = RunResultPresentation.OutcomeOf(result, cancelled);
        (StateIcon.Text, StateIcon.Foreground, StateTitle.Text) = outcome switch
        {
            RunDisplayOutcome.Succeeded => ("✓", System.Windows.Media.Brushes.ForestGreen, "已成功完成"),
            RunDisplayOutcome.PartialSuccess => ("!", System.Windows.Media.Brushes.DarkOrange, "部分成功"),
            RunDisplayOutcome.Cancelled => ("⊘", System.Windows.Media.Brushes.DarkOrange, "已取消"),
            _ => ("!", System.Windows.Media.Brushes.Firebrick, "同步失败")
        };
        StateDescription.Text = text; CloseButton.IsEnabled = true; SaveLogButton.IsEnabled = true;
        RetryButton.IsEnabled = _retry is not null && RunResultPresentation.BuildRetryPlan(result, _originalOperations).Operations.Count > 0;
        if (outcome == RunDisplayOutcome.Succeeded && AutoClose.IsChecked == true) Close();
    }
    public void Complete(bool success, string text)
    {
        var synthetic = new SyncRunResult(Guid.NewGuid(), []);
        Complete(synthetic with { Operations = success ? [] : [new(Guid.NewGuid(), "", OperationKind.Blocked, TransferStage.Failed, Error: text)] }, text);
    }
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || _retry is null) return;
        var plan = RunResultPresentation.BuildRetryPlan(_result, _originalOperations);
        if (plan.Operations.Count == 0) return;
        RetryButton.IsEnabled = false; StateTitle.Text = "正在重试失败项"; StateDescription.Text = $"正在重试 {plan.Operations.Count} 项可重试失败。";
        try { Complete(await _retry(plan), "失败项重试已完成。"); }
        catch (OperationCanceledException) { Complete(new SyncRunResult(Guid.NewGuid(), []), "失败项重试已取消。", cancelled: true); }
        catch (Exception ex) { Complete(new SyncRunResult(Guid.NewGuid(), [new(Guid.NewGuid(), "", OperationKind.Blocked, TransferStage.Failed, Error: ex.Message)]), "失败项重试失败。"); }
    }
    private async void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null) return;
        var dialog = new SaveFileDialog { Filter = "Text log (*.log)|*.log|Text file (*.txt)|*.txt", FileName = $"fengsync-run-{_result.RunId:N}.log" };
        if (dialog.ShowDialog(this) != true) return;
        await File.WriteAllTextAsync(dialog.FileName, RunResultPresentation.ToLog(_result));
        StateDescription.Text = "运行日志已保存：" + dialog.FileName;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
