using FengSync.Core;
using FengSync.Core.Capabilities;
using FengSync.Core.Configuration;
using FengSync.Core.Updates;
using FengSync.Core.Execution;
using FengSync.Core.Scanning;
using FengSync.Core.SftpServer;
using FengSync.Services;
using FluentIcon = FluentIcons.Common.Icon;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using System.IO;
using System.Windows.Media;
using System.Windows.Data;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using FengSync.Views;

namespace FengSync;
public partial class MainWindow : Window
{
    private readonly ObservableCollection<ComparisonRow> _rows = [];
    private readonly ObservableCollection<SyncProfile> _profiles = [];
    private readonly ProfileStore _profileStore = new();
    private readonly ApplicationVersionService _versionService = new();
    private UpdateCoordinator? _updates;
    private SyncPlan? _plan;
    private PlanSnapshot? _snapshot;
    private ComparisonSnapshot? _comparison;
    private IEndpoint? _left, _right;
    private RcloneDaemon? _rclone;
    private ApplicationSettings _settings;
    private CancellationTokenSource? _syncCancellation;
    private CancellationTokenSource? _compareCancellation;
    private bool _syncInProgress;
    private bool _compareInProgress;
    private bool _closing;
    private ChangeSummary _lastSummary = ChangeSummary.Empty;
    private ICollectionView? _rowsView;
    private DispatcherTimer? _searchDebounceTimer;
    // The single source of truth for endpoint header and title text — bound items must agree here.
    private string _filterKind = "All";
    private string _searchText = "";

    private SyncMode SelectedMode => (SyncMode)Math.Max(0, SyncModeBox?.SelectedIndex ?? 0);
    public MainWindow()
    {
        InitializeComponent();
        Comparison.ItemsSource = _rows;
        ProfileList.ItemsSource = _profiles;
        Comparison.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler((_, _) => Dispatcher.BeginInvoke(RefreshSummary)));
        Comparison.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler((_, _) => Dispatcher.BeginInvoke(RefreshSummary)));
        _settings = new();
        _updates = new UpdateCoordinator(_versionService, new GitHubReleaseClient(), () => _settings, ApplyApplicationSettingsAsync, ExitForUpdateAsync);
        UpdateSettingsText();
        UpdateHeaderForCurrentProfile();
        UpdateComparisonEmptyState();
        Status.Text = "正在加载设置…";
        Loaded += async (_, _) => await InitializeAsync();
    }

    // ==== Search / filter (debounced, view-only) =========================================
    private void ChangeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = ChangeSearchBox.Text ?? "";
        if (_searchDebounceTimer == null)
        {
            _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDebounceTimer.Tick += (_, _) => { _searchDebounceTimer.Stop(); ApplyRowFilter(); };
        }
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void ChangeFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _filterKind = ChangeFilterBox.SelectedIndex switch { 1 => "Upload", 2 => "Download", 3 => "Delete", 4 => "Conflict", 5 => "Ignored", _ => "All" };
        ApplyRowFilter();
    }

    private void ApplyRowFilter()
    {
        _rowsView ??= CollectionViewSource.GetDefaultView(_rows);
        if (_rowsView is null) return;
        var search = _searchText.Trim();
        var filter = _filterKind;
        _rowsView.Filter = obj =>
        {
            if (obj is not ComparisonRow row) return false;
            if (!string.IsNullOrEmpty(search))
            {
                var matches = (row.Name?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                              || (row.RelativePath?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                              || (row.Operation.Path?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
                if (!matches) return false;
            }
            return filter switch
            {
                "Upload" => row.OperationLabel == "上传",
                "Download" => row.OperationLabel == "下载",
                "Delete" => row.OperationLabel == "删除",
                "Conflict" => row.Operation.IsConflict && !row.IsIgnored && !row.IsFilterExcluded,
                "Ignored" => row.IsIgnored || row.IsFilterExcluded,
                _ => true
            };
        };
    }

    // ==== Top-level actions =================================================================
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_compareInProgress || _syncInProgress) return;
        _compareInProgress = true;
        _compareCancellation = new();
        var token = _compareCancellation.Token;
        UpdateActionButtons();
        try
        {
            Status.Text = $"正在准备比较：{LeftPath.Text} ↔ {RightPath.Text}";
            await BuildPlanAsync(token);
        }
        catch (OperationCanceledException) { Status.Text = "比较已取消。"; }
        catch (Exception ex) { SyncButton.IsEnabled = false; Status.Text = ex.Message; }
        finally
        {
            _compareCancellation?.Dispose();
            _compareCancellation = null;
            _compareInProgress = false;
            _ = _updates?.CheckDeferredAsync(this, _syncInProgress || _compareInProgress);
            UpdateActionButtons();
        }
    }
    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_syncInProgress || _compareInProgress || _plan is null || _left is null || _right is null) return;
        ProgressWindow? progressDialog = null;
        var attemptedProfile = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create("临时", LeftPath.Text, RightPath.Text);
        _syncInProgress = true;
        UpdateActionButtons();
        try
        {
            var effective = CurrentSettings; Comparison.CommitEdit(DataGridEditingUnit.Cell, true); Comparison.CommitEdit(DataGridEditingUnit.Row, true);
            var operations = _rows.Select(x => x.Operation).ToList(); var current = new SyncPlan(operations);
            if (!current.CanExecute || _snapshot is null) { Status.Text = "请先选择操作并裁决所有冲突，然后重新比较。"; return; }
            var profile = attemptedProfile;
            _syncCancellation = new();
            progressDialog = new ProgressWindow(operations, !_settings.ShowCompleted) { Owner = this };
            progressDialog.Show();
            progressDialog.ShowInitialization("1 / 5", "正在准备同步计划…");
            Status.Text = "正在准备同步…";

            progressDialog.ShowInitialization("2 / 5", "正在重新扫描两个端点，确认执行前的最新状态…");
            var scans = await Task.WhenAll(_left.ScanAsync(_syncCancellation.Token), _right.ScanAsync(_syncCancellation.Token));
            var leftEntries = scans[0].ToDictionary(x => x.Path, _left.Capabilities.EffectivePaths.CreateComparer()); var rightEntries = scans[1].ToDictionary(x => x.Path, _right.Capabilities.EffectivePaths.CreateComparer());
            progressDialog.ShowInitialization("3 / 5", "正在进行删除阈值和目标空间安全检查…");
            var safety = new SafetyValidator().ValidatePlan(current, leftEntries.Count, rightEntries.Count, SelectedMode, profile.MaxDeletes, profile.MaxDeleteRatio)
                .Combine(new SafetyValidator().ValidateCapacity(current, leftEntries, rightEntries, _left, _right));
            var risk = SyncRiskSummary.Create(current, leftEntries, rightEntries);
            var thresholdOverride = SyncConfirmationPolicy.CanOverrideWithProfileName(safety);
            if (safety.HasBlockingIssues && !thresholdOverride) { var message = string.Join(" ", safety.Issues.Select(x => x.Message)); Status.Text = message; progressDialog.Complete(false, message); await RecordManualRunAsync(profile, RunOutcome.Failed, message); return; }
            if (SyncConfirmationPolicy.RequiresConfirmation(risk) || thresholdOverride)
            {
                progressDialog.ShowInitialization("4 / 5", "等待确认高风险同步操作…");
                var confirmation = new SyncConfirmationWindow(risk, safety, profile.Name, risk.TransferBytes) { Owner = this };
                if (confirmation.ShowDialog() != true) { Status.Text = "已取消高风险同步确认。"; progressDialog.Complete(new SyncRunResult(Guid.NewGuid(), []), "已取消高风险同步确认。", cancelled: true); await RecordManualRunAsync(profile, RunOutcome.Cancelled, "已取消高风险同步确认。"); return; }
            }
            progressDialog.ShowInitialization("5 / 5", "正在建立双向同步基线…");
            Status.Text = $"正在以 {effective.MaxConcurrentCopies} 路并发同步…";
            var baselineRepository = new BaselineRepository(); var transaction = await baselineRepository.BeginAsync(_left, _right, _syncCancellation.Token);
            progressDialog.BeginTransfers(effective.MaxConcurrentCopies);
            var run = await new SyncExecutorV2().ExecuteAsync(_snapshot, _left, _right, new Progress<TransferProgress>(p => progressDialog.Report(p)), _syncCancellation.Token, effective.VerifyCopies, effective.Versioning, journals: new TaskJournalStore(), maxConcurrentCopies: effective.MaxConcurrentCopies);
            transaction = run.Operations.Where(x => x.Stage == TransferStage.Committed).Aggregate(transaction, (current, item) => current.RecordCommitted(item.Path));
            if (_comparison is not null && run.SucceededOperations > 0)
                await baselineRepository.CommitFromResultsAsync(_left, _right,
                    new BaselineCommitInput(_comparison, run.Operations.ToDictionary(x => x.OperationId), transaction), _syncCancellation.Token);
            transaction = run.Succeeded ? transaction.Complete() : transaction.Rollback(needsRecovery: true);
            await baselineRepository.SaveAsync(transaction, _syncCancellation.Token);
            progressDialog.SetRetry(operations, async retryPlan =>
            {
                var retrySnapshot = await PlanSnapshot.CaptureAsync(retryPlan, _left, _right);
                return await new SyncExecutorV2().ExecuteAsync(retrySnapshot, _left, _right, new Progress<TransferProgress>(p => progressDialog.Report(p)), CancellationToken.None, effective.VerifyCopies, effective.Versioning, journals: new TaskJournalStore(), maxConcurrentCopies: effective.MaxConcurrentCopies);
            });
            if (!run.Succeeded)
            {
                var outcome = run.SucceededOperations > 0 ? RunOutcome.PartialSuccess : RunOutcome.Failed;
                await AppendRunHistoryAsync(profile, operations, run, outcome, "同步存在失败操作。");
                SetLastRun(profile, DateTimeOffset.UtcNow);
                Status.Text = $"同步部分失败：{run.FailedOperations} 个操作失败；基线未变更。";
                LastRunStatus.Text = $"上次运行 · {DateTime.Now:yyyy-MM-dd HH:mm}";
                progressDialog.Complete(run, $"{run.FailedOperations} 个操作失败。可查看错误详情、保存日志，或重试可重试失败项。");
                return;
            }
            await AppendRunHistoryAsync(profile, operations, run, RunOutcome.Succeeded, null);
            SetLastRun(profile, DateTimeOffset.UtcNow);
            Status.Text = "同步完成。";
            LastRunStatus.Text = $"上次运行 · {DateTime.Now:yyyy-MM-dd HH:mm}";
            progressDialog.Complete(run, "所有选中的操作已完成；双向基线已安全提交。");
        }
        catch (OperationCanceledException) when (_syncCancellation?.IsCancellationRequested == true)
        {
            Status.Text = "同步已取消。";
            progressDialog?.Complete(new SyncRunResult(Guid.NewGuid(), []), "同步已取消。", cancelled: true);
            await RecordManualRunAsync(attemptedProfile, RunOutcome.Cancelled, "同步已取消。");
        }
        catch (OperationCanceledException ex)
        {
            var message = "同步失败：操作意外中断。" + ex.Message;
            Status.Text = message;
            progressDialog?.Complete(false, message);
            await RecordManualRunAsync(attemptedProfile, RunOutcome.Failed, message);
        }
        catch (Exception ex) { Status.Text = "同步失败：" + ex.Message; progressDialog?.Complete(false, ex.Message); await RecordManualRunAsync(attemptedProfile, RunOutcome.Failed, ex.Message); }
        finally { _syncCancellation?.Dispose(); _syncCancellation = null; _syncInProgress = false; RefreshSummary(); UpdateActionButtons(); _ = _updates?.CheckDeferredAsync(this, _syncInProgress || _compareInProgress); }
    }
    private static Task AppendRunHistoryAsync(SyncProfile profile, IReadOnlyCollection<SyncOperation> operations, SyncRunResult run, RunOutcome outcome, string? detail)
        => new RunHistoryRepository().AppendAsync(new RunHistoryEntry(profile.Id, outcome, DateTimeOffset.UtcNow, operations.Count(x => x.Selected), run.SucceededOperations, run.FailedOperations, run.Operations.Sum(x => x.BytesTransferred), detail, run.RunId));
    private async Task RecordManualRunAsync(SyncProfile profile, RunOutcome outcome, string? detail)
    {
        var timestamp = DateTimeOffset.UtcNow;
        await new RunHistoryRepository().AppendAsync(new RunHistoryEntry(profile.Id, outcome, timestamp, 0, 0, 0, 0, detail), CancellationToken.None);
        SetLastRun(profile, timestamp);
    }
    private void SetLastRun(SyncProfile profile, DateTimeOffset timestamp)
    {
        var index = _profiles.IndexOf(profile);
        if (index < 0) return;
        var updated = profile with { LastRunUtc = timestamp };
        _profiles[index] = updated;
        if ((ProfileList.SelectedItem as SyncProfile)?.Id == profile.Id) ProfileList.SelectedItem = updated;
    }
    private void KeepLeft_Click(object sender, RoutedEventArgs e) => ResolveSelected(true); private void KeepRight_Click(object sender, RoutedEventArgs e) => ResolveSelected(false);
    private void ResolveSelected(bool left)
    {
        if (_rows.Count == 0) { Status.Text = "暂无可修改覆盖方向的行；请先点击“比较”。"; return; }
        ApplyDirection(_rows, left);
    }
    private void ApplyDirection(IEnumerable<ComparisonRow> rows, bool keepLeft)
    {
        var changed = 0; var errors = new List<string>();
        foreach (var row in rows)
        {
            try { row.Operation.OverrideCopyDirection(keepLeft, row.Left, row.Right); row.Refresh(); changed++; }
            catch (Exception ex) { errors.Add($"{row.Operation.Path}：{ex.Message}"); }
        }
        Comparison.Items.Refresh(); RefreshSummary();
        Status.Text = errors.Count == 0 ? $"已将 {changed} 项设置为{(keepLeft ? "左侧覆盖右侧" : "右侧覆盖左侧")}。" : $"已修改 {changed} 项；{string.Join(" ", errors)}";
    }
    private void ActionMenu_KeepLeft_Click(object sender, RoutedEventArgs e) => ApplyActionFromMenu(sender, true);
    private void ActionMenu_KeepRight_Click(object sender, RoutedEventArgs e) => ApplyActionFromMenu(sender, false);
    private void ActionMenu_Ignore_Click(object sender, RoutedEventArgs e) => SetIgnoredFromMenu(sender, true);
    private void ActionMenu_Enable_Click(object sender, RoutedEventArgs e) => SetIgnoredFromMenu(sender, false);
    private void ApplyActionFromMenu(object sender, bool keepLeft)
    {
        if (GetRowFromMenu(sender) is not ComparisonRow row) { Status.Text = "请选择要修改覆盖方向的行。"; return; }
        var rows = Comparison.SelectedItems.Cast<ComparisonRow>().Contains(row) ? Comparison.SelectedItems.Cast<ComparisonRow>() : [row];
        ApplyDirection(rows, keepLeft);
    }
    private void SetIgnoredFromMenu(object sender, bool ignored)
    {
        if (GetRowFromMenu(sender) is not ComparisonRow row) { Status.Text = "请选择要修改的行。"; return; }
        if (row.IsFilterExcluded) { Status.Text = "该文件被 Profile 过滤规则排除；请在 Profile 中修改长期过滤规则。"; return; }
        if (row.IsIgnored == ignored) { Status.Text = ignored ? "该文件已忽略。" : "该文件已启用。"; return; }
        row.IsIgnored = ignored;
        row.Operation.Selected = !ignored;
        row.Refresh(); Comparison.Items.Refresh(); RefreshSummary();
        Status.Text = ignored ? "已临时忽略该文件；仅影响本次同步。" : "已重新启用该文件。";
    }
    private static ComparisonRow? GetRowFromMenu(object sender)
    {
        if (sender is MenuItem { DataContext: ComparisonRow direct }) return direct;
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement target } } && target.DataContext is ComparisonRow viaTarget) return viaTarget;
        return null;
    }

    // ==== ChangeSummary ============================================================
    private ChangeSummary BuildSummary()
    {
        // Selected only — totals reflect the executable subset, not every difference.
        var copyLeftSelected = _rows.Where(x => x.Selected && x.Operation.Kind is OperationKind.CopyLeftToRight).ToList();
        var copyRightSelected = _rows.Where(x => x.Selected && x.Operation.Kind is OperationKind.CopyRightToLeft).ToList();
        var deleteSelected = _rows.Where(x => x.Selected && x.Operation.Kind is OperationKind.DeleteLeft or OperationKind.DeleteRight).ToList();

        long uploadBytes = copyLeftSelected.Sum(x => x.Left?.Fingerprint?.Size ?? 0);
        long downloadBytes = copyRightSelected.Sum(x => x.Right?.Fingerprint?.Size ?? 0);

        var deleteBytes = deleteSelected.Sum(x => x.Operation.Kind == OperationKind.DeleteLeft ? (x.Left?.Fingerprint?.Size ?? 0) : (x.Right?.Fingerprint?.Size ?? 0));
        var deleteBytesAnyKnown = deleteSelected.Any(x => x.Operation.Kind == OperationKind.DeleteLeft ? x.Left?.Fingerprint?.Size is not null : x.Right?.Fingerprint?.Size is not null);
        long? deleteBucket = deleteSelected.Count == 0 ? 0 : (deleteBytesAnyKnown ? (long?)deleteBytes : null);

        // Conflict counting: ignore rows that the user dismissed; size uses the larger known side, "0 B" when there are no conflicts.
        var conflictRows = _rows.Where(x => x.Operation.IsConflict && !x.IsIgnored && !x.IsFilterExcluded).ToList();
        long conflictBytes = conflictRows.Sum(x => Math.Max(x.Left?.Fingerprint?.Size ?? 0, x.Right?.Fingerprint?.Size ?? 0));
        var conflictBucket = new ChangeBucket(conflictRows.Count, conflictBytes);

        // Total: deduplicated operation count and only upload + download transfer bytes.
        var opsForTotal = _rows.Where(x => x.Selected).Select(x => x.Operation.OperationId).Distinct().Count();
        var totalBucket = new ChangeBucket(opsForTotal, uploadBytes + downloadBytes);

        return new ChangeSummary(
            new(copyLeftSelected.Count, uploadBytes),
            new(copyRightSelected.Count, downloadBytes),
            new(deleteSelected.Count, deleteBucket),
            conflictBucket,
            totalBucket);
    }

    private void RefreshSummary()
    {
        var summary = BuildSummary();
        _lastSummary = summary;
        UploadSummary.Text = $"上传 {summary.Upload.Count} 项";
        UploadSizeSummary.Text = FormatBytes(summary.Upload.Bytes);
        DownloadSummary.Text = $"下载 {summary.Download.Count} 项";
        DownloadSizeSummary.Text = FormatBytes(summary.Download.Bytes);
        DeleteSummary.Text = $"删除 {summary.Delete.Count} 项";
        DeleteSizeSummary.Text = summary.Delete.Bytes is null ? "—" : FormatBytes(summary.Delete.Bytes);
        ConflictSummary.Text = $"冲突 {summary.Conflict.Count} 项";
        ConflictSizeSummary.Text = FormatBytes(summary.Conflict.Bytes);
        TotalSummary.Text = $"{summary.Total.Count} 项";
        TotalSizeSummary.Text = FormatBytes(summary.Total.Bytes);

        SelectedLabel.Text = _rows.Count(x => x.Selected).ToString();
        UpdateComparisonEmptyState();
        UpdateActionButtons();
    }

    private record SafetyState(string Headline, string Description, string Severity)
    {
        public bool IsEmpty => string.IsNullOrEmpty(Headline);
    }
    private SafetyState _safetySummary = new("", "选择左右端点后即可比较。", "Neutral");

    private void UpdateActionButtons()
    {
        if (CompareButton is null || SyncButton is null) return;
        var profile = ProfileList?.SelectedItem as SyncProfile ?? SyncProfile.Create("临时", LeftPath?.Text ?? "", RightPath?.Text ?? "");
        var canRun = new FeatureCapabilityService().Evaluate(profile with { LeftPath = LeftPath?.Text ?? "", RightPath = RightPath?.Text ?? "", Mode = SelectedMode }).CanRun;
        var hasProfile = ProfileList?.SelectedItem is SyncProfile selectedProfile && !string.IsNullOrWhiteSpace(selectedProfile.Name);
        CompareButton.IsEnabled = canRun && !_closing && !_compareInProgress && !_syncInProgress && hasProfile;
        var planUsable = _plan is not null && new SyncPlan(_rows.Select(x => x.Operation).ToList()).CanExecute && !_safetySummary.Headline.Contains("阻断");
        SyncButton.IsEnabled = canRun && !_compareInProgress && !_syncInProgress && planUsable;
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null) return "—";
        var b = bytes.Value;
        return b switch
        {
            < 1024 => $"{b:N0} B",
            < 1024 * 1024 => $"{b / 1024d:N1} KB",
            < 1024L * 1024 * 1024 => $"{b / 1024d / 1024:N1} MB",
            _ => $"{b / 1024d / 1024 / 1024:N2} GB"
        };
    }

    // ==== Endpoint bar handlers ===================================================
    private void BrowseLeft_Click(object s, RoutedEventArgs e) => Browse(LeftPath);
    private void BrowseRight_Click(object s, RoutedEventArgs e) => Browse(RightPath);
    private void Browse(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() == true) ChangeEndpointPath(target, dialog.FolderName);
    }
    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        (LeftPath.Text, RightPath.Text) = (RightPath.Text, LeftPath.Text);
        UpdateEndpointHeaders();
        if (_plan is not null) await RecompareAsync();
    }

    private void ChangeEndpointPath(System.Windows.Controls.TextBox target, string value)
    {
        target.Text = value;
        UpdateEndpointHeaders();
        UpdateActionButtons();
    }
    private void Comparison_CurrentCellChanged(object s, EventArgs e) => Dispatcher.BeginInvoke(RefreshSummary);
    private void UpdateComparisonEmptyState()
    {
        if (ComparisonEmptyState is null) return;
        var visible = _rows.Count == 0;
        ComparisonEmptyState.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (ComparisonEmptyStateText is not null)
            ComparisonEmptyStateText.Text = _plan is null
                ? "请选择两个端点后点击比较"
                : "没有需要同步的差异";
    }
    private void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _settings = _settings with { MainWindowSidebarWidth = ClampSidebarWidth(SidebarColumn.ActualWidth) };
    }
    private static double ClampSidebarWidth(double value) => double.IsNaN(value) || double.IsInfinity(value) ? 248 : Math.Clamp(value, 220, 320);
    private EffectiveProfileSettings CurrentSettings => EffectiveProfileSettings.Resolve(ProfileList?.SelectedItem as SyncProfile ?? SyncProfile.Create("默认", "", ""), _settings);
    private void UpdateSettingsText() { if (ConcurrencyLabel is not null) ConcurrencyLabel.Text = CurrentSettings.MaxConcurrentCopies + " 路"; }
    private void SyncMode_Changed(object s, SelectionChangedEventArgs e) { if (_plan is not null) Compare_Click(this, new RoutedEventArgs()); }
    private string ModeTitle() => SelectedMode switch { SyncMode.Mirror => "镜像 →", SyncMode.Update => "更新 →", SyncMode.Custom => "自定义 →", _ => "双向 ↔" };
    private async void SaveSettings_Click(object s, RoutedEventArgs e) { await new SettingsStore().SaveAsync(_settings); Status.Text = "设置已保存。"; }
    private static string SettingsPath => Path.Combine(AppDataPaths.Root, "FengSync.local.json");
    private async Task InitializeAsync()
    {
        try
        {
            var loaded = await new SettingsStore().LoadAsync();
            _settings = loaded.Settings;
            SidebarColumn.Width = new GridLength(ClampSidebarWidth(_settings.MainWindowSidebarWidth));
            UpdateSettingsText();
            UpdateEndpointHeaders();
            Status.Text = loaded.RecoveredFromCorruption
                ? $"设置文件已损坏，已备份到：{loaded.BackupPath}"
                : loaded.Migrated
                    ? $"程序设置已从 schema v{loaded.MigratedFromSchemaVersion} 迁移；原文件备份在：{loaded.MigrationBackupPath}"
                    : "选择左右端点后点击“比较”。";
        }
        catch (Exception ex) { _settings = new(); Status.Text = "无法加载程序设置：" + ex.Message; }
        await LoadProfilesAsync();
        if (App.CurrentApp.UpdatedFromVersion is { Length: > 0 } from && App.CurrentApp.UpdateTaskDirectory is { Length: > 0 } task)
        {
            Status.Text = $"已从 {from} 更新到 {_versionService.DisplayVersion}";
            try { await File.WriteAllTextAsync(Path.Combine(Path.GetFullPath(task), "success"), "ok"); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Unable to confirm update: " + ex.Message); }
        }
        _ = Dispatcher.InvokeAsync(() => _ = _updates?.CheckAsync(this, manual: false, _syncInProgress || _compareInProgress));
    }
    private Task RecompareAsync() { Compare_Click(this, new RoutedEventArgs()); return Task.CompletedTask; }
    private async Task LoadProfilesAsync()
    {
        _profiles.Clear(); foreach (var item in await _profileStore.LoadAsync()) _profiles.Add(item);
        var history = new RunHistoryRepository();
        for (var index = 0; index < _profiles.Count; index++)
        {
            var profile = _profiles[index];
            var lastRun = (await history.QueryAsync(profile.Id)).FirstOrDefault();
            _profiles[index] = profile with { LastRunUtc = lastRun?.CompletedUtc };
        }
        if (_profiles.Count == 0) _profiles.Add(SyncProfile.Create("未命名配置", "", ""));
        var saved = _profiles.ToList().FindIndex(x => x.Id == _settings.LastSelectedProfileId); ProfileList.SelectedIndex = saved >= 0 ? saved : 0;
    }

    private async void NewProfile_Click(object s, RoutedEventArgs e)
    {
        var profile = SyncProfile.Create("未命名配置 " + (_profiles.Count + 1), "", ""); _profiles.Add(profile); ProfileList.SelectedItem = profile; await PersistProfilesAsync(); Status.Text = "已新建配置档案。";
    }
    private async void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not SyncProfile original) { Status.Text = "请先选择要复制的 Profile。"; return; }
        var copy = original with { Id = Guid.NewGuid().ToString("N"), Name = original.Name + " 副本" };
        _profiles.Add(copy); ProfileList.SelectedItem = copy; await PersistProfilesAsync();
        Status.Text = $"已复制为 “{copy.Name}”。";
    }
    private async void DeleteProfile_Click(object s, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not SyncProfile profile) { Status.Text = "请选择要移除的配置。"; return; }
        if (MessageBox.Show($"从 Feng Sync 的配置列表移除“{profile.Name}”？\n已导出的配置文件不会被删除。", "移除配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _profiles.Remove(profile);
        if (_profiles.Count == 0) _profiles.Add(SyncProfile.Create("未命名配置", "", ""));
        ProfileList.SelectedIndex = 0; await PersistProfilesAsync(); Status.Text = "配置已从本项目移除。";
    }
    private async void EditProfile_Click(object s, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not SyncProfile original) { Status.Text = "请先选择一个 Profile。"; return; }
        var editable = original with { LeftPath = LeftPath.Text, RightPath = RightPath.Text, Mode = SelectedMode };
        var updated = new ProfileDialogService().Edit(this, editable);
        if (updated is null) return;
        var index = _profiles.IndexOf(original);
        if (index >= 0) _profiles[index] = updated; else _profiles.Add(updated);
        ProfileList.SelectedItem = updated;
        await PersistProfilesAsync();
        ApplyProfile(updated);
        Status.Text = "Profile 设置已保存。";
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings, ApplyApplicationSettingsAsync, ShowSftpServerSettingsAsync, CleanupExpiredLocalTemporaryFilesAsync, ProfileList.SelectedItem as SyncProfile) { Owner = this };
        dialog.ShowDialog();
    }

    private async void ExportPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) { Status.Text = "没有可导出的变更。"; return; }
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
            FileName = "feng-sync-plan.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            // Always export the current view (filter applied); never include credentials.
            var visible = Comparison.Items.Cast<ComparisonRow>().ToList();
            if (string.Equals(System.IO.Path.GetExtension(dialog.FileName), ".json", StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonSerializer.Serialize(visible.Select(r => new
                {
                    r.Name,
                    r.RelativePath,
                    Path = r.Operation.Path,
                    Operation = r.OperationLabel,
                    Direction = r.DirectionDisplay,
                    SizeBytes = r.OperationSize,
                    ModifiedUtc = r.ModifiedUtc,
                    Selected = r.Selected
                }));
                await File.WriteAllTextAsync(dialog.FileName, json);
            }
            else
            {
                var lines = new List<string> { "Name,RelativePath,Path,Operation,Direction,Size,Modified,Selected" };
                foreach (var r in visible)
                {
                    lines.Add($"{Escape(r.Name)},{Escape(r.RelativePath)},{Escape(r.Operation.Path)},{Escape(r.OperationLabel)},{Escape(r.DirectionDisplay)},{r.OperationSize?.ToString() ?? ""},{r.ModifiedUtc?.ToString("yyyy-MM-dd HH:mm") ?? ""},{(r.Selected ? "true" : "false")}");
                }
                await File.WriteAllLinesAsync(dialog.FileName, lines);
            }
            Status.Text = $"已导出 {visible.Count} 行变更：{dialog.FileName}";
        }
        catch (Exception ex) { Status.Text = "导出失败：" + ex.Message; }
    }
    private static string Escape(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    private void ProfileList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        UpdateHeaderForCurrentProfile();
        if (ProfileList.SelectedItem is SyncProfile profile) { _settings = _settings with { LastSelectedProfileId = profile.Id }; ApplyProfile(profile); }
    }
    private void UpdateHeaderForCurrentProfile()
    {
        var profile = ProfileList.SelectedItem as SyncProfile;
        CurrentProfileTitle.Text = profile is null ? "未选择同步配置" : profile.Name;
        if (ProfileContextStatus != null) ProfileContextStatus.Text = profile is null ? "Profile 工作区" : $"已载入：{profile.Name}";
        // Show a relative description under the title — defaults to a friendly hint.
        if (LastRunStatus != null && profile is not null)
            LastRunStatus.Text = profile.LastRunUtc is not null
                ? $"上次运行 · {profile.LastRunUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
                : string.IsNullOrEmpty(profile.LeftPath) && string.IsNullOrEmpty(profile.RightPath)
                    ? "请在下方选择两个端点后点击比较"
                    : "双向同步 · 准备比较";
    }
    private void ApplyProfile(SyncProfile profile)
    {
        if (LeftPath != null) LeftPath.Text = profile.LeftPath;
        if (RightPath != null) RightPath.Text = profile.RightPath;
        if (SyncModeBox != null) SyncModeBox.SelectedIndex = (int)profile.Mode;
        UpdateSettingsText();
        UpdateEndpointHeaders();
        UpdateHeaderForCurrentProfile();
        var compatibility = new FeatureCapabilityService().Evaluate(profile);
        UpdateActionButtons();
        Status.Text = compatibility.CanRun ? "准备就绪" : $"Profile 需要修复：{compatibility.Summary}";
    }
    private void UpdateEndpointHeaders()
    {
        var (leftTitle, leftIcon) = DescribeEndpoint(LeftPath?.Text);
        var (rightTitle, rightIcon) = DescribeEndpoint(RightPath?.Text);
        if (LeftEndpointTitle != null) LeftEndpointTitle.Text = leftTitle;
        if (LeftEndpointIcon != null) LeftEndpointIcon.Icon = leftIcon;
        if (LeftEndpointPath != null) LeftEndpointPath.Text = LeftPath?.Text ?? "";
        if (RightEndpointTitle != null) RightEndpointTitle.Text = rightTitle;
        if (RightEndpointIcon != null) RightEndpointIcon.Icon = rightIcon;
        if (RightEndpointPath != null) RightEndpointPath.Text = RightPath?.Text ?? "";
    }
    private static (string Title, FluentIcon Icon) DescribeEndpoint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ("未选择端点", FluentIcon.Folder);
        if (path.StartsWith("gdrive://", StringComparison.OrdinalIgnoreCase)) return ("Google Drive", FluentIcon.Cloud);
        if (path.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase)) return ("SFTP", FluentIcon.Cloud);
        if (path.StartsWith("s3://", StringComparison.OrdinalIgnoreCase)) return ("S3", FluentIcon.Cloud);
        return ("本地文件夹", FluentIcon.Folder);
    }
    private async Task BuildPlanAsync(CancellationToken cancellationToken)
    {
        var profile = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create("临时", LeftPath.Text, RightPath.Text) with { Mode = SelectedMode }; var compatibility = new FeatureCapabilityService().Evaluate(profile with { LeftPath = LeftPath.Text, RightPath = RightPath.Text, Mode = SelectedMode }); if (!compatibility.CanRun) throw new InvalidOperationException("该 Profile 需要修复：" + compatibility.Summary); var effective = CurrentSettings; (_left, _right) = await CreateEndpointsAsync(LeftPath.Text, RightPath.Text);
        var configurationSafety = _left is LocalEndpoint configLeft && _right is LocalEndpoint configRight ? new SafetyValidator().ValidateConfiguration(configLeft.Root, configRight.Root, effective.Versioning?.ArchiveDirectory) : SafetyValidationResult.Pass;
        if (configurationSafety.HasBlockingIssues) throw new InvalidOperationException(string.Join(" ", configurationSafety.Issues.Select(x => x.Message)));
        Status.Text = $"正在扫描端点：左 {LeftPath.Text}  ·  右 {RightPath.Text}";
        var leftProgress = new Progress<ScanProgress>(x => Status.Text = x.Completed ? $"左侧扫描完成：{x.ItemsScanned} 项。正在等待右侧…" : $"正在扫描左侧：已发现 {x.ItemsScanned} 项{(string.IsNullOrEmpty(x.CurrentPath) ? "" : " · " + x.CurrentPath)}");
        var rightProgress = new Progress<ScanProgress>(x => Status.Text = x.Completed ? $"右侧扫描完成：{x.ItemsScanned} 项。正在分析差异…" : $"正在扫描右侧：已发现 {x.ItemsScanned} 项{(string.IsNullOrEmpty(x.CurrentPath) ? "" : " · " + x.CurrentPath)}");
        var scans = await Task.WhenAll(_left.ScanAsync(leftProgress, cancellationToken), _right.ScanAsync(rightProgress, cancellationToken));
        Status.Text = $"扫描完成：左侧 {scans[0].Count} 项  ·  右侧 {scans[1].Count} 项，正在分析差异…";
        var left = scans[0].ToDictionary(x => x.Path, _left.Capabilities.EffectivePaths.CreateComparer()); var right = scans[1].ToDictionary(x => x.Path, _right.Capabilities.EffectivePaths.CreateComparer()); var baselineRepository = new BaselineRepository(); var baseline = await baselineRepository.LoadAsync(_left, _right); var baselineWarning = baselineRepository.LastLoadWarning;
        _plan = new ModePlanner().Build(SelectedMode, left.Values, right.Values, baseline, SyncFilter.Empty, _left.Capabilities, _right.Capabilities);
        var filter = effective.Filter.CreateEngine();
        var ignoredPaths = new HashSet<string>(_plan.Operations.Where(op =>
        {
            var entry = left.GetValueOrDefault(op.Path) ?? right.GetValueOrDefault(op.Path);
            return !filter.Evaluate(op.Path, new FilterEntryAttributes(entry?.Fingerprint?.Size, entry?.Fingerprint?.ModifiedUtc)).Included;
        }).Select(op => op.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var operation in _plan.Operations.Where(op => ignoredPaths.Contains(op.Path))) operation.Selected = false;
        Status.Text = $"分析完成：{_plan.Operations.Count} 项差异，正在生成同步计划…";
        var planSafety = new SafetyValidator().ValidatePlan(_plan, left.Count, right.Count, SelectedMode, profile.MaxDeletes, profile.MaxDeleteRatio).Combine(new SafetyValidator().ValidateCapacity(_plan, left, right, _left, _right));
        _safetySummary = planSafety.HasBlockingIssues && !SyncConfirmationPolicy.CanOverrideWithProfileName(planSafety)
            ? new SafetyState("安全检查：阻断", "删除阈值被触发或目标空间不足，请在同步确认窗口中处理。", "Danger")
            : SyncConfirmationPolicy.RequiresConfirmation(SyncRiskSummary.Create(_plan, left, right))
                ? new SafetyState("安全检查：警告", "本次同步将覆盖大量文件；建议先确认差异。", "Warning")
                : new SafetyState("安全检查通过", "建议先比较并确认变更，再执行同步。", "Success");
        if (planSafety.HasBlockingIssues && !SyncConfirmationPolicy.CanOverrideWithProfileName(planSafety)) throw new InvalidOperationException(string.Join(" ", planSafety.Issues.Select(x => x.Message)));
        _comparison = new ComparisonSnapshot
        {
            SnapshotId = Guid.NewGuid(),
            Left = new EndpointSnapshot { Endpoint = _left.Profile, Paths = _left.Capabilities.EffectivePaths, StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow, Entries = scans[0], ByPath = left },
            Right = new EndpointSnapshot { Endpoint = _right.Profile, Paths = _right.Capabilities.EffectivePaths, StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow, Entries = scans[1], ByPath = right },
            Mode = ComparisonMode.TimeAndSize, TimeTolerance = TimeSpan.FromSeconds(effective.TimeToleranceSeconds), Baseline = baseline, Plan = _plan
        };
        _snapshot = PlanSnapshot.FromComparison(_plan, _comparison); _rows.Clear(); foreach (var op in _plan.Operations) _rows.Add(new(op, left.GetValueOrDefault(op.Path), right.GetValueOrDefault(op.Path), ignoredPaths.Contains(op.Path)));
        ApplyRowFilter();
        RefreshSummary();
        UpdateHeaderForCurrentProfile();
        var resultText = _lastSummary.Total.Count == 0 ? "没有需要同步的差异" : $"比较完成 · {_lastSummary.Total.Count} 项变更";
        if (CompareResultStatus != null) CompareResultStatus.Text = resultText;
        var status = baselineWarning ?? $"{ModeTitle()} 比较完成：左侧 {left.Count} 项  ·  右侧 {right.Count} 项  ·  {_plan.Operations.Count} 个差异/提示（其中 {ignoredPaths.Count} 项已忽略）。";
        Status.Text = _safetySummary.Severity is "Warning" or "Danger" ? $"{status} · {_safetySummary.Description}" : status;
    }
    private async Task<(IEndpoint Left, IEndpoint Right)> CreateEndpointsAsync(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) throw new InvalidOperationException("请先填写两个端点。");
        await DisposeRcloneAsync();
        var needsRemote = IsCloud(left) || IsCloud(right);
        if (needsRemote) _rclone = await RcloneDaemon.StartAsync(BundledRclone.ExecutablePath, BundledRclone.ConfigPath);
        return (CreateEndpoint(left), CreateEndpoint(right));
    }
    private Task<ProfileRunResult> RunBatchProfileAsync(SyncProfile profile) => new ProfileRunner(applicationSettings: _settings).RunAsync(profile);
    private IEndpoint CreateEndpoint(string value)
    {
        if (!IsCloud(value)) return new LocalEndpoint(value);
        if (_rclone is null) throw new InvalidOperationException("云端连接未启动。");
        var split = value.Split("://", 2, StringSplitOptions.None); var remoteAndRoot = split[1].Split('/', 2); var type = CloudEndpointType(split[0]);
        return new RcloneEndpoint(_rclone.Client, new EndpointProfile(Guid.NewGuid(), type, remoteAndRoot.Length == 2 ? remoteAndRoot[1] : "", remoteAndRoot[0]), new(false, true, type == EndpointType.GoogleDrive, TimeSpan.FromSeconds(1)));
    }
    private static IEndpoint CreateEndpoint(string value, RcloneDaemon? daemon)
    {
        if (!IsCloud(value)) return new LocalEndpoint(value);
        if (daemon is null) throw new InvalidOperationException("云端连接未启动。");
        var split = value.Split("://", 2, StringSplitOptions.None); var remoteAndRoot = split[1].Split('/', 2); var type = CloudEndpointType(split[0]);
        return new RcloneEndpoint(daemon.Client, new EndpointProfile(Guid.NewGuid(), type, remoteAndRoot.Length == 2 ? remoteAndRoot[1] : "", remoteAndRoot[0]), new(false, true, type == EndpointType.GoogleDrive, TimeSpan.FromSeconds(1)));
    }
    private static EndpointType CloudEndpointType(string scheme) => scheme.ToLowerInvariant() switch { "gdrive" => EndpointType.GoogleDrive, "sftp" => EndpointType.Sftp, "s3" => EndpointType.S3, _ => throw new InvalidOperationException("不支持的云端端点协议。") };
    private static bool IsCloud(string value) => value.StartsWith("gdrive://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase);
    private async Task DisposeRcloneAsync() { if (_rclone is not null) { await _rclone.DisposeAsync(); _rclone = null; } }
    private void AddCloudEndpoint_Click(object sender, RoutedEventArgs e) => AddCloudEndpoint((sender as FrameworkElement)?.Tag as string);
    private void AddCloudEndpoint(string? side)
    {
        side ??= "Left";
        var picker = new RemoteEndpointPickerWindow(side) { Owner = this };
        if (picker.ShowDialog() != true || picker.ResultUri is null) return;
        var resolvedSide = side == "Right" ? "右" : "左";
        var pathBox = side == "Right" ? RightPath : LeftPath;
        ChangeEndpointPath(pathBox, picker.ResultUri);
        Status.Text = $"已将云端端点添加到{resolvedSide}侧：{picker.ResultUri}";
    }
    private void ManageRemoteEndpoints_Click(object sender, RoutedEventArgs e) => new RemoteEndpointManagerWindow { Owner = this }.ShowDialog();
    private void OpenBatchRun_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0) { Status.Text = "没有可运行的 Profile。"; return; }
        new BatchRunWindow(_profiles.ToList(), CurrentSettings.MaxConcurrentCopies,
            (profile, cancellationToken) => new ProfileRunner(applicationSettings: _settings).RunAsync(profile, ct: cancellationToken))
        { Owner = this }.ShowDialog();
    }
    private void Options_Click(object s, RoutedEventArgs e) => SettingsButton_Click(s, new RoutedEventArgs());

    private async Task ApplyApplicationSettingsAsync(ApplicationSettings settings)
    {
        await new SettingsStore().SaveAsync(settings);
        _settings = settings;
        UpdateSettingsText();
        Status.Text = "程序设置已应用。";
    }

    private Task ShowSftpServerSettingsAsync()
    {
        new SftpServerSettingsWindow { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }
    private async void OpenProfileFile_Click(object s, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Feng Sync files (*.fengsync.json;*.fengsync.batch.json)|*.fengsync.json;*.fengsync.batch.json|JSON files (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        var raw = await File.ReadAllTextAsync(dialog.FileName);
        BatchJob? batch = null;
        try { if (JsonDocument.Parse(raw).RootElement.ValueKind == JsonValueKind.Object) batch = JsonSerializer.Deserialize<BatchJob>(raw); } catch (JsonException) { }
        var loaded = batch?.Profiles?.Count > 0 ? batch.Profiles : await new ProfileStore(dialog.FileName).LoadAsync();
        if (loaded.Count == 0) { Status.Text = "该文件没有可打开的 Profile。"; return; }
        foreach (var profile in loaded.Where(x => _profiles.All(existing => existing.Id != x.Id))) _profiles.Add(profile);
        ProfileList.SelectedItem = loaded[0];
        await PersistProfilesAsync();
        Status.Text = batch is null ? $"已打开 {loaded.Count} 个 Profile。" : $"已打开批处理作业“{batch.Name}”（{loaded.Count} 个 Profile）。";
    }
    private async void ExportProfile_Click(object s, RoutedEventArgs e)
    {
        await SaveProfileToListAsync();
        var profile = ProfileList.SelectedItem as SyncProfile;
        if (profile is null) return;
        var dialog = new SaveFileDialog { Filter = "Feng Sync Profile (*.fengsync.json)|*.fengsync.json", FileName = profile.Name + ".fengsync.json" };
        if (dialog.ShowDialog() != true) return;
        await new ProfileStore(dialog.FileName).SaveAsync([profile]);
        Status.Text = "Profile 已保存为文件。";
    }
    private async void SaveBatchJob_Click(object s, RoutedEventArgs e)
    {
        await SaveProfileToListAsync();
        var profiles = ProfileList.SelectedItems.Cast<SyncProfile>().ToList();
        if (profiles.Count == 0 && ProfileList.SelectedItem is SyncProfile profile) profiles.Add(profile);
        if (profiles.Count == 0) return;
        var dialog = new SaveFileDialog { Filter = "Feng Sync Batch Job (*.fengsync.batch.json)|*.fengsync.batch.json", FileName = "batch.fengsync.batch.json" };
        if (dialog.ShowDialog() != true) return;
        var job = new BatchJob(Path.GetFileNameWithoutExtension(dialog.FileName), profiles);
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(job));
        Status.Text = $"批处理作业已保存（{profiles.Count} 个 Profile）。";
    }
    private void ManageSchedule_Click(object s, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not SyncProfile profile) { Status.Text = "请先选择一个 Profile。"; return; }
        new ScheduleWizard(profile) { Owner = this }.ShowDialog();
    }
    private async void ShowLog_Click(object s, RoutedEventArgs e)
    {
        var jobs = await new TaskJournalStore().LoadIncompleteAsync();
        var text = jobs.Count == 0 ? "没有未完成的同步作业日志。" : string.Join(Environment.NewLine + Environment.NewLine, jobs.Select(job => $"作业 {job.JobId}\n开始：{job.CreatedUtc:yyyy-MM-dd HH:mm:ss}\n" + string.Join(Environment.NewLine, job.Items.Select(item => $"{item.State,-10} {item.Kind,-24} {item.Path}"))));
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(14) };
        new Window { Title = "同步日志", Owner = this, Content = box, Width = 680, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
    private void ShowRunHistory_Click(object s, RoutedEventArgs e) => new RunHistoryWindow((ProfileList.SelectedItem as SyncProfile)?.Id) { Owner = this }.ShowDialog();
    private async Task SaveProfileToListAsync() { var old = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create("未命名配置", "", ""); var current = old with { LeftPath = LeftPath.Text, RightPath = RightPath.Text, Mode = SelectedMode }; var index = _profiles.IndexOf(old); if (index >= 0) _profiles[index] = current; else _profiles.Add(current); ProfileList.SelectedItem = current; await PersistProfilesAsync(); }
    private Task PersistProfilesAsync() => _profileStore.SaveAsync(_profiles);
    private void Exit_Click(object s, RoutedEventArgs e) => Close();
    private async void CheckUpdates_Click(object s, RoutedEventArgs e)
    {
        if (_updates is not null) await _updates.CheckAsync(this, manual: true, _syncInProgress || _compareInProgress);
    }
    private void About_Click(object s, RoutedEventArgs e)
        => new AboutWindow(_versionService.DisplayVersion, async () => { if (_updates is not null) await _updates.CheckAsync(this, manual: true, _syncInProgress || _compareInProgress); }) { Owner = this }.ShowDialog();
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) { base.OnClosing(e); return; }
        e.Cancel = true;
        _closing = true;
        _syncCancellation?.Cancel();
        _compareCancellation?.Cancel();
        CloseWhenReadyAsync();
    }
    private Task<int> CleanupExpiredLocalTemporaryFilesAsync()
    {
        var roots = _profiles.SelectMany(profile => new[] { profile.LeftPath, profile.RightPath })
            .Where(path => !IsCloud(path));
        return Task.FromResult(TransferTemporaryMaintenance.RemoveExpiredLocalFiles(roots, TimeSpan.FromDays(7), DateTimeOffset.UtcNow));
    }
    private async void CloseWhenReadyAsync()
    {
        try
        {
            _settings = _settings with { LastSelectedProfileId = (ProfileList.SelectedItem as SyncProfile)?.Id };
            await new SettingsStore().SaveAsync(_settings);
            await PersistProfilesAsync();
            await DisposeRcloneAsync();
        }
        catch { }
        finally { await App.CurrentApp.ShutdownAsync(); }
    }
    private async Task ExitForUpdateAsync()
    {
        Status.Text = "正在退出并安装更新…";
        _closing = true;
        _settings = _settings with { LastSelectedProfileId = (ProfileList.SelectedItem as SyncProfile)?.Id };
        await new SettingsStore().SaveAsync(_settings); await PersistProfilesAsync(); await DisposeRcloneAsync(); await App.CurrentApp.ShutdownAsync();
    }
    protected override void OnClosed(EventArgs e) { base.OnClosed(e); }
    private sealed record BatchJob(string Name, IReadOnlyList<SyncProfile> Profiles);
}

/// <summary>One bucket in the right-hand summary panel (e.g. "上传 12 项 / 1.8 GB").</summary>
public sealed record ChangeBucket(int Count, long? Bytes);
/// <summary>Aggregated row counts + transfer sizes derived from the current ComparisonRow collection.
/// <see cref="Total"/> counts unique operations only — the same file change must not be added
/// to both Upload and Download. long? distinguishes a known zero-byte total from "size unknown".</summary>
public sealed record ChangeSummary(ChangeBucket Upload, ChangeBucket Download, ChangeBucket Delete, ChangeBucket Conflict, ChangeBucket Total)
{
    public static ChangeSummary Empty { get; } = new(new(0, 0), new(0, 0), new(0, 0), new(0, 0), new(0, 0));
}

public sealed class ComparisonRow : INotifyPropertyChanged
{
    public ComparisonRow(SyncOperation operation, EntrySnapshot? left, EntrySnapshot? right, bool isFilterExcluded = false) { Operation = operation; Left = left; Right = right; IsFilterExcluded = isFilterExcluded; Refresh(); }
    public SyncOperation Operation { get; }
    public EntrySnapshot? Left { get; }
    public EntrySnapshot? Right { get; }
    public bool IsFilterExcluded { get; private set; }
    public bool IsIgnored { get; set; }
    public bool Selected
    {
        get => Operation.Selected;
        set { if (IsIgnored || IsFilterExcluded || Operation.Selected == value) return; Operation.Selected = value; OnPropertyChanged(); }
    }

    // Display-only projections surfaced by the redesigned comparison grid.
    public string Name { get; private set; } = "";
    public string RelativePath { get; private set; } = "";
    public string DirectionDisplay { get; private set; } = "";
    public OperationKind OperationKindValue => Operation.Kind;
    public OperationKind DisplayKind => IsIgnored || IsFilterExcluded ? OperationKind.Blocked : Operation.Kind;
    public string OperationLabel { get; private set; } = "—";
    public string OperationSeverity { get; private set; } = "Neutral";
    public long? OperationSize { get; private set; }
    public DateTime? ModifiedUtc { get; private set; }

    // Legacy display properties retained so the existing grid remains readable during migration.
    public string LeftDisplay { get; private set; } = "";
    public string RightDisplay { get; private set; } = "";
    public string LeftSize { get; private set; } = "";
    public string RightSize { get; private set; } = "";
    public string ActionDisplay { get; private set; } = "";
    public Brush ActionBrush { get; private set; } = Brushes.DimGray;
    public string Reason => Operation.Reason;
    public string? ConflictToolTip => Operation.IsConflict && !IsIgnored && !IsFilterExcluded
        ? $"冲突原因：{Operation.Reason}{Environment.NewLine}左侧修改时间：{ModifiedTime(Left)}{Environment.NewLine}右侧修改时间：{ModifiedTime(Right)}"
        : null;
    /// <summary>Explicit toolbar coverage overrides this comparison's temporary exclusions without editing the persisted Profile.</summary>
    public void EnableForCurrentPlan() { IsIgnored = false; IsFilterExcluded = false; Operation.Selected = true; OnPropertyChanged(nameof(Selected)); }
    public void Refresh()
    {
        Name = Path.GetFileName(Operation.Path);
        var dir = Path.GetDirectoryName(Operation.Path);
        RelativePath = string.IsNullOrEmpty(dir) ? "/" : "/" + dir.Replace('\\', '/');

        DirectionDisplay = (IsIgnored || IsFilterExcluded) ? "—" : Operation.Kind switch
        {
            OperationKind.CopyLeftToRight or OperationKind.CreateRightDirectory => "左 → 右",
            OperationKind.CopyRightToLeft or OperationKind.CreateLeftDirectory => "右 → 左",
            OperationKind.DeleteLeft => "— → 左",
            OperationKind.DeleteRight => "右 → —",
            _ => "—"
        };

        OperationLabel = IsIgnored ? "已忽略" : IsFilterExcluded ? "已过滤" : Operation.IsConflict ? "冲突" : Operation.Kind switch
        {
            OperationKind.CopyLeftToRight or OperationKind.CreateRightDirectory => "上传",
            OperationKind.CopyRightToLeft or OperationKind.CreateLeftDirectory => "下载",
            OperationKind.DeleteLeft or OperationKind.DeleteRight => "删除",
            OperationKind.Blocked => "阻断",
            _ => "未知"
        };
        OperationSeverity = OperationLabel switch { "上传" => "Success", "下载" => "Info", "删除" => "Danger", "冲突" => "Warning", _ => "Neutral" };

        OperationSize = Operation.Kind switch
        {
            OperationKind.CopyLeftToRight or OperationKind.CreateRightDirectory => Left?.Fingerprint?.Size,
            OperationKind.CopyRightToLeft or OperationKind.CreateLeftDirectory => Right?.Fingerprint?.Size,
            OperationKind.DeleteLeft => Left?.Fingerprint?.Size,
            OperationKind.DeleteRight => Right?.Fingerprint?.Size,
            _ => null
        };
        ModifiedUtc = ((IsIgnored || IsFilterExcluded)
            ? (Left?.Fingerprint?.ModifiedUtc ?? Right?.Fingerprint?.ModifiedUtc)
            : (Operation.Kind switch
            {
                OperationKind.CopyRightToLeft or OperationKind.DeleteRight => Right?.Fingerprint?.ModifiedUtc,
                _ => Left?.Fingerprint?.ModifiedUtc
            }))?.LocalDateTime;

        LeftDisplay = Describe(Left);
        RightDisplay = Describe(Right);
        LeftSize = Size(Left);
        RightSize = Size(Right);
        (ActionDisplay, ActionBrush) = (IsIgnored || IsFilterExcluded) ? ("⊘", Brushes.DimGray) : Operation.IsConflict ? ("⚠", Brushes.DarkOrange) : Operation.Kind switch
        {
            OperationKind.CopyLeftToRight => ("✚→", Brushes.ForestGreen),
            OperationKind.CopyRightToLeft => ("←✚", Brushes.ForestGreen),
            OperationKind.DeleteLeft => ("←✖", Brushes.Firebrick),
            OperationKind.DeleteRight => ("✖→", Brushes.Firebrick),
            OperationKind.CreateLeftDirectory => ("←✚", Brushes.ForestGreen),
            OperationKind.CreateRightDirectory => ("✚→", Brushes.ForestGreen),
            OperationKind.Blocked => ("⛔", Brushes.Firebrick),
            _ => ("=", Brushes.DimGray)
        };
    }
    private static string Describe(EntrySnapshot? e) => e is null ? "" : e.Kind == EntryKind.Directory ? "▰ " + e.Path : "▱ " + e.Path;
    private static string Size(EntrySnapshot? e) => e?.Fingerprint is null ? "" : e.Fingerprint.Size.ToString("N0");
    private static string ModifiedTime(EntrySnapshot? entry) => entry switch
    {
        null => "不存在或已删除",
        { Kind: EntryKind.Directory } => "目录（无修改时间）",
        { Fingerprint: { } fingerprint } => fingerprint.ModifiedUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
        _ => "未知"
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
}
