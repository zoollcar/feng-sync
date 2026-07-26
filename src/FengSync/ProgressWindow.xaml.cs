using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using FengSync.Core;
using Microsoft.Win32;

namespace FengSync;

public partial class ProgressWindow : Window
{
    private readonly ObservableCollection<ProgressOperationRow> _rows = [];
    private readonly Dictionary<Guid, ProgressOperationRow> _rowsById = [];
    private readonly Stopwatch _clock = new();
    private int _total;
    private int _copyTotal;
    private SyncRunResult? _result;
    private IReadOnlyList<SyncOperation> _originalOperations = [];
    private Func<SyncPlan, Task<SyncRunResult>>? _retry;

    public ProgressWindow(IReadOnlyList<SyncOperation> operations, bool autoClose)
    {
        InitializeComponent();
        AutoClose.IsChecked = autoClose;
        OperationResults.ItemsSource = _rows;
        BeginRun(operations);
    }

    private void BeginRun(IReadOnlyList<SyncOperation> operations)
    {
        _rows.Clear();
        _rowsById.Clear();
        foreach (var operation in operations.Where(IsExecutable))
        {
            var row = new ProgressOperationRow(operation);
            _rows.Add(row);
            _rowsById.Add(operation.OperationId, row);
        }
        _total = _rows.Count;
        _copyTotal = _rows.Count(x => x.IsCopy);
        FileProgress.Maximum = Math.Max(1, _total);
        FileProgress.Value = 0;
        BytesGraph.Maximum = Math.Max(1, _copyTotal);
        BytesGraph.Value = 0;
        Counter.Text = $"0 / {_total}";
        BytesText.Text = _copyTotal == 0 ? "本次没有文件传输" : $"已完成 0 / {_copyTotal} 个文件，0 B";
        SpeedText.Text = "等待传输";
        _clock.Restart();
    }

    private static bool IsExecutable(SyncOperation operation) => operation.Selected && !operation.IsConflict;

    public void ShowInitialization(string phase, string detail)
    {
        StateTitle.Text = "正在初始化同步";
        StateDescription.Text = $"{phase} · {detail}";
        CurrentFile.Text = detail;
        FileProgress.IsIndeterminate = true;
        SpeedText.Text = "正在准备";
    }

    public void BeginTransfers(int concurrency)
    {
        FileProgress.IsIndeterminate = false;
        StateTitle.Text = "正在同步";
        CurrentFile.Text = "等待传输任务…";
        UpdateLiveSummary(concurrency);
    }

    public void Report(TransferProgress progress)
    {
        CurrentFile.Text = progress.Path;
        if (!_rowsById.TryGetValue(progress.OperationId, out var row)) return;
        row.Update(progress);
        UpdateVisualCounters(progress.ActiveTransfers);
        UpdateLiveSummary(progress.ActiveTransfers);
    }

    private void UpdateVisualCounters(int activeTransfers)
    {
        var completed = _rows.Count(x => x.IsTerminal);
        var completedCopies = _rows.Count(x => x.IsCopy && x.Stage == TransferStage.Committed);
        var bytes = _rows.Where(x => x.IsCopy && x.Stage == TransferStage.Committed).Sum(x => x.BytesCompleted);
        FileProgress.Value = Math.Min(completed, _total);
        Counter.Text = $"{completed} / {_total}";
        BytesGraph.Value = Math.Min(completedCopies, _copyTotal);
        BytesText.Text = $"已完成 {completedCopies} / {_copyTotal} 个文件，{bytes:N0} B";
        SpeedText.Text = $"{bytes / Math.Max(.1, _clock.Elapsed.TotalSeconds) / 1024 / 1024:N1} MB/秒 · {activeTransfers} 项并发 · 用时 {_clock.Elapsed:mm\\:ss}";
    }

    private void UpdateLiveSummary(int activeTransfers)
    {
        var completed = _rows.Count(x => x.IsTerminal);
        var failed = _rows.Count(x => x.Stage == TransferStage.Failed);
        var executing = _rows.Count(x => x.IsExecuting);
        StateDescription.Text = $"已完成 {completed}，执行中 {Math.Max(executing, activeTransfers)}，失败 {failed}";
    }

    public void SetRetry(IReadOnlyList<SyncOperation> originalOperations, Func<SyncPlan, Task<SyncRunResult>> retry)
        => (_originalOperations, _retry) = (originalOperations, retry);

    public void Complete(SyncRunResult result, string text, bool cancelled = false)
    {
        FileProgress.IsIndeterminate = false;
        _result = result;
        foreach (var item in result.Operations)
            if (_rowsById.TryGetValue(item.OperationId, out var row)) row.Complete(item);

        foreach (var row in _rows.Where(x => !x.IsTerminal))
            row.ForceTerminal(cancelled ? TransferStage.Cancelled : TransferStage.Failed,
                cancelled ? "同步已取消。" : "因其他操作失败而未执行。");

        UpdateVisualCounters(0);
        var outcome = RunResultPresentation.OutcomeOf(result, cancelled);
        (StateIcon.Text, StateIcon.Foreground, StateTitle.Text) = outcome switch
        {
            RunDisplayOutcome.Succeeded => ("✓", System.Windows.Media.Brushes.ForestGreen, "已成功完成"),
            RunDisplayOutcome.PartialSuccess => ("!", System.Windows.Media.Brushes.DarkOrange, "已结束（部分成功）"),
            RunDisplayOutcome.Cancelled => ("⊘", System.Windows.Media.Brushes.DarkOrange, "已取消"),
            _ => ("!", System.Windows.Media.Brushes.Firebrick, "同步失败")
        };
        StateDescription.Text = text;
        CloseButton.IsEnabled = true;
        SaveLogButton.IsEnabled = true;
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
        RetryButton.IsEnabled = false;
        BeginRun(plan.Operations);
        StateTitle.Text = "正在重试失败项";
        StateDescription.Text = $"正在重试 {plan.Operations.Count} 项可重试失败。";
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

public sealed class ProgressOperationRow : INotifyPropertyChanged
{
    public ProgressOperationRow(SyncOperation operation)
        => (OperationId, Path, Kind, IsCopy) = (operation.OperationId, operation.Path, operation.Kind, operation.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft);

    public Guid OperationId { get; }
    public string Path { get; }
    public OperationKind Kind { get; }
    public bool IsCopy { get; }
    public TransferStage Stage { get; private set; } = TransferStage.Pending;
    public string StageText => Stage switch
    {
        TransferStage.Pending => "等待中", TransferStage.Preparing => "准备中", TransferStage.Transferring => "传输中",
        TransferStage.Verifying => "校验中", TransferStage.Deleting => "删除中", TransferStage.Committed => "已完成",
        TransferStage.Failed => "失败", TransferStage.Cancelled => "已取消", _ => Stage.ToString()
    };
    public string? Error { get; private set; }
    public long BytesCompleted { get; private set; }
    public bool IsTerminal => Stage is TransferStage.Committed or TransferStage.Failed or TransferStage.Cancelled;
    public bool IsExecuting => Stage is TransferStage.Preparing or TransferStage.Transferring or TransferStage.Verifying or TransferStage.Deleting;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(TransferProgress progress)
    {
        Stage = progress.Stage;
        BytesCompleted = Math.Max(BytesCompleted, progress.BytesCompleted);
        if (progress.Stage == TransferStage.Failed) Error = Summarize(progress.Error);
        Notify(nameof(Stage), nameof(StageText), nameof(BytesCompleted), nameof(Error), nameof(IsTerminal), nameof(IsExecuting));
    }

    public void Complete(OperationRunResult result)
    {
        Stage = result.Stage;
        BytesCompleted = Math.Max(BytesCompleted, result.BytesTransferred);
        Error = result.Stage == TransferStage.Failed ? Summarize(result.Error) : null;
        Notify(nameof(Stage), nameof(StageText), nameof(BytesCompleted), nameof(Error), nameof(IsTerminal), nameof(IsExecuting));
    }

    public void ForceTerminal(TransferStage stage, string error)
    {
        Stage = stage;
        Error = error;
        Notify(nameof(Stage), nameof(StageText), nameof(Error), nameof(IsTerminal), nameof(IsExecuting));
    }

    private static string? Summarize(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return error;
        var firstLine = error.Replace("\r", "").Split('\n')[0].Trim();
        return firstLine.Length <= 180 ? firstLine : firstLine[..177] + "…";
    }
    private void Notify(params string[] names) { foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}
