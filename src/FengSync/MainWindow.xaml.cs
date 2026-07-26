using FengSync.Core;
using FengSync.Core.Capabilities;
using FengSync.Core.Configuration;
using FengSync.Core.Updates;
using FengSync.Core.Execution;
using FengSync.Core.Scanning;
using FengSync.Core.SftpServer;
using FengSync.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.IO;
using System.Windows.Media;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FengSync.Views;

namespace FengSync;
public partial class MainWindow : Window
{
    private readonly ObservableCollection<ComparisonRow> _rows = []; private readonly ObservableCollection<SyncProfile> _profiles = []; private readonly ProfileStore _profileStore = new(); private readonly ApplicationVersionService _versionService = new(); private UpdateCoordinator? _updates; private SyncPlan? _plan; private PlanSnapshot? _snapshot; private ComparisonSnapshot? _comparison; private IEndpoint? _left, _right; private RcloneDaemon? _rclone; private ApplicationSettings _settings; private CancellationTokenSource? _syncCancellation; private CancellationTokenSource? _compareCancellation; private bool _syncInProgress; private bool _compareInProgress; private bool _closing;
    private SyncMode SelectedMode => (SyncMode)Math.Max(0, SyncModeBox?.SelectedIndex ?? 0);
    public MainWindow() { InitializeComponent(); Comparison.ItemsSource = _rows; ProfileList.ItemsSource = _profiles; Comparison.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler((_, _) => Dispatcher.BeginInvoke(RefreshSummary))); Comparison.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler((_, _) => Dispatcher.BeginInvoke(RefreshSummary))); _settings = new(); _updates = new UpdateCoordinator(_versionService, new GitHubReleaseClient(), () => _settings, ApplyApplicationSettingsAsync, ExitForUpdateAsync); UpdateSettingsText(); RefreshProfileSelection(); UpdateComparisonEmptyState(); Status.Text = "正在加载设置…"; Loaded += async (_, _) => await InitializeAsync(); }
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
        _syncInProgress = true;
        UpdateActionButtons();
        try
        {
            var effective = CurrentSettings; Comparison.CommitEdit(DataGridEditingUnit.Cell, true); Comparison.CommitEdit(DataGridEditingUnit.Row, true);
            var operations = _rows.Select(x => x.Operation).ToList(); var current = new SyncPlan(operations);
            if (!current.CanExecute || _snapshot is null) { Status.Text = "请先选择操作并裁决所有冲突，然后重新比较。"; return; }
            var profile = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create("临时", LeftPath.Text, RightPath.Text);
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
            if (safety.HasBlockingIssues && !thresholdOverride) { var message = string.Join(" ", safety.Issues.Select(x => x.Message)); Status.Text = message; progressDialog.Complete(false, message); return; }
            if (SyncConfirmationPolicy.RequiresConfirmation(risk) || thresholdOverride)
            {
                progressDialog.ShowInitialization("4 / 5", "等待确认高风险同步操作…");
                var confirmation = new SyncConfirmationWindow(risk, safety, profile.Name, risk.TransferBytes) { Owner = this };
                if (confirmation.ShowDialog() != true) { Status.Text = "已取消高风险同步确认。"; progressDialog.Complete(new SyncRunResult(Guid.NewGuid(), []), "已取消高风险同步确认。", cancelled: true); return; }
            }
            progressDialog.ShowInitialization("5 / 5", "正在建立双向同步基线…");
            Status.Text = $"正在以 {effective.MaxConcurrentCopies} 路并发同步…";
            var baselineRepository = new BaselineRepository(); var transaction = await baselineRepository.BeginAsync(_left, _right, _syncCancellation.Token);
            progressDialog.BeginTransfers(effective.MaxConcurrentCopies);
            var run = await new SyncExecutorV2().ExecuteAsync(_snapshot, _left, _right, new Progress<TransferProgress>(p => progressDialog.Report(p)), _syncCancellation.Token, effective.VerifyCopies, effective.Versioning, journals: new TaskJournalStore(), maxConcurrentCopies: effective.MaxConcurrentCopies);
            transaction = run.Operations.Where(x => x.Stage == TransferStage.Committed).Aggregate(transaction, (current, item) => current.RecordCommitted(item.Path));
            // A failed copy no longer prevents independent deletes from running.
            // Persist exactly the committed subset so those deletes are not planned
            // again, while failed and skipped paths retain their previous baseline.
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
                await AppendRunHistoryAsync(profile, operations, run, RunOutcome.PartialSuccess, "同步存在失败操作。");
                Status.Text = $"同步部分失败：{run.FailedOperations} 个操作失败；基线未变更。";
                progressDialog.Complete(run, $"{run.FailedOperations} 个操作失败。可查看错误详情、保存日志，或重试可重试失败项。");
                return;
            }
            await AppendRunHistoryAsync(profile, operations, run, RunOutcome.Succeeded, null);
            Status.Text = "同步完成。"; progressDialog.Complete(run, "所有选中的操作已完成；双向基线已安全提交。");
        }
        catch (OperationCanceledException) { Status.Text = "同步已取消。"; progressDialog?.Complete(new SyncRunResult(Guid.NewGuid(), []), "同步已取消。", cancelled: true); }
        catch (Exception ex) { Status.Text = "同步失败：" + ex.Message; progressDialog?.Complete(false, ex.Message); }
        finally { _syncCancellation?.Dispose(); _syncCancellation = null; _syncInProgress = false; RefreshSummary(); UpdateActionButtons(); _ = _updates?.CheckDeferredAsync(this, _syncInProgress || _compareInProgress); }
    }
    private static Task AppendRunHistoryAsync(SyncProfile profile, IReadOnlyCollection<SyncOperation> operations, SyncRunResult run, RunOutcome outcome, string? detail)
        => new RunHistoryRepository().AppendAsync(new RunHistoryEntry(profile.Id, outcome, DateTimeOffset.UtcNow, operations.Count(x => x.Selected), run.SucceededOperations, run.FailedOperations, run.Operations.Sum(x => x.BytesTransferred), detail, run.RunId));
    private void KeepLeft_Click(object sender, RoutedEventArgs e) => ResolveSelected(true); private void KeepRight_Click(object sender, RoutedEventArgs e) => ResolveSelected(false);
    private void ResolveSelected(bool left)
    {
        // Toolbar coverage is deliberately a whole-plan operation: it must have the same
        // result as selecting every comparison row and choosing the corresponding action
        // from the context menu. Do not use DataGrid.SelectedItems here: a stale current-row
        // selection would otherwise silently restrict the toolbar action to one row.
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
    // Action-cell context-menu handlers. The menu items reuse the same mutation logic as the top-bar
    // KeepLeft/KeepRight buttons so right-click and toolbar produce identical results.
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
    // ContextMenu lives in a separate Popup visual tree. Since WPF 4.0 the menu's DataContext is inherited
    // from its PlacementTarget (the Border in the cell template, whose DataContext is the row), so the
    // simpler lookup usually works; PlacementTarget is a defensive fallback for edge cases.
    private static ComparisonRow? GetRowFromMenu(object sender)
    {
        if (sender is MenuItem { DataContext: ComparisonRow direct }) return direct;
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement target } } && target.DataContext is ComparisonRow viaTarget) return viaTarget;
        return null;
    }
    private void RefreshSummary() { var selected = _rows.Count(x => x.Selected); var bytes = _rows.Where(x => x.Selected && x.Operation.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft).Sum(x => (x.Operation.Kind == OperationKind.CopyLeftToRight ? x.Left : x.Right)?.Fingerprint?.Size ?? 0); Summary.Text = $"左侧 {_rows.Count(x => x.Left is not null)} 项  ·  右侧 {_rows.Count(x => x.Right is not null)} 项  ·  { _rows.Count } 个差异/提示"; TransferSizeLabel.Text = $"已选待传输：{FormatBytes(bytes)}"; SelectedLabel.Text = selected.ToString(); UpdateComparisonEmptyState(); UpdateActionButtons(); }
    private void UpdateActionButtons()
    {
        if (CompareButton is null || SyncButton is null) return;
        var profile = ProfileList?.SelectedItem as SyncProfile ?? SyncProfile.Create("临时", LeftPath?.Text ?? "", RightPath?.Text ?? "");
        var canRun = new FeatureCapabilityService().Evaluate(profile with { LeftPath = LeftPath?.Text ?? "", RightPath = RightPath?.Text ?? "", Mode = SelectedMode }).CanRun;
        CompareButton.IsEnabled = canRun && !_closing && !_compareInProgress && !_syncInProgress;
        SyncButton.IsEnabled = canRun && !_compareInProgress && !_syncInProgress && _plan is not null && new SyncPlan(_rows.Select(x => x.Operation).ToList()).CanExecute;
    }
    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KB" : bytes < 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024:N1} MB" : $"{bytes / 1024d / 1024 / 1024:N2} GB";
    private void BrowseLeft_Click(object s, RoutedEventArgs e) => Browse(LeftPath); private void BrowseRight_Click(object s, RoutedEventArgs e) => Browse(RightPath);
    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        (LeftPath.Text, RightPath.Text) = (RightPath.Text, LeftPath.Text);
        if (_plan is not null) await RecompareAsync();
    }
    private static void Browse(System.Windows.Controls.TextBox target) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog() == true) target.Text = dialog.FolderName; }
    private void Comparison_CurrentCellChanged(object s, EventArgs e) => Dispatcher.BeginInvoke(RefreshSummary);
    private void UpdateComparisonEmptyState()
    {
        if (ComparisonEmptyState is null) return;
        ComparisonEmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ComparisonEmptyStateText is not null) ComparisonEmptyStateText.Text = _plan is null ? "选择左右端点后开始比较" : "没有需要同步的差异";
    }
    private void RefreshProfileSelection()
    {
        if (ProfileSelectionLabel is not null) ProfileSelectionLabel.Text = $"已选择 {ProfileList?.SelectedItems.Count ?? 0} 个 Profile";
        if (EditProfileButton is not null) EditProfileButton.IsEnabled = (ProfileList?.SelectedItems.Count ?? 0) <= 1;
    }
    private void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _settings = _settings with { MainWindowSidebarWidth = ClampSidebarWidth(SidebarColumn.ActualWidth) };
    }
    private static double ClampSidebarWidth(double value) => double.IsNaN(value) || double.IsInfinity(value) ? 260 : Math.Clamp(value, 220, 380);
    private EffectiveProfileSettings CurrentSettings => EffectiveProfileSettings.Resolve(ProfileList?.SelectedItem as SyncProfile ?? SyncProfile.Create("默认", "", ""), _settings);
    private void UpdateSettingsText() { if (ConcurrencyLabel is not null) ConcurrencyLabel.Text = CurrentSettings.MaxConcurrentCopies + " 路"; }
    private void SyncMode_Changed(object s, SelectionChangedEventArgs e) { if (SyncModeCaption is not null) SyncModeCaption.Text = ModeTitle(); if (_plan is not null) Compare_Click(this, new RoutedEventArgs()); }
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
        if (_profiles.Count == 0) _profiles.Add(SyncProfile.Create("未命名配置", "", ""));
        var saved = _profiles.ToList().FindIndex(x => x.Id == _settings.LastSelectedProfileId); ProfileList.SelectedIndex = saved >= 0 ? saved : 0;
    }
    private async void NewProfile_Click(object s, RoutedEventArgs e)
    {
        var profile = SyncProfile.Create("未命名配置 " + (_profiles.Count + 1), "", ""); _profiles.Add(profile); ProfileList.SelectedItem = profile; await PersistProfilesAsync(); Status.Text = "已新建配置档案。";
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
        // The path and mode fields on the main window are editable. Opening the
        // settings gear must edit what the user currently sees rather than stale
        // values from the last persisted profile.
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
    private void LoadProfile_Click(object s, RoutedEventArgs e) { if (ProfileList.SelectedItem is SyncProfile profile) ApplyProfile(profile); else Status.Text = "请先选择一个配置档案。"; }
    private async void SaveProfile_Click(object s, RoutedEventArgs e)
    {
        var old = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create("未命名配置", "", "");
        var current = old with { LeftPath = LeftPath.Text, RightPath = RightPath.Text, Mode = SelectedMode };
        var dialog = new SaveFileDialog
        {
            Filter = "Feng Sync Profile (*.fengsync.json)|*.fengsync.json|JSON files (*.json)|*.json",
            FileName = current.Name + ".fengsync.json",
            InitialDirectory = Path.Combine(AppDataPaths.Root, "Profiles"),
            AddExtension = true,
            DefaultExt = ".fengsync.json"
        };
        if (dialog.ShowDialog() != true) { Status.Text = "已取消保存配置档案。"; return; }
        var index = _profiles.IndexOf(old); if (index >= 0) _profiles[index] = current; else _profiles.Add(current);
        await _profileStore.SaveAsync(_profiles);
        await new ProfileStore(dialog.FileName).SaveAsync([current]);
        ProfileList.SelectedItem = current;
        Status.Text = $"配置档案已保存到：{dialog.FileName}";
    }
    private async void RunProfile_Click(object s, RoutedEventArgs e)
    {
        var profiles = ProfileList.SelectedItems.Cast<SyncProfile>().ToList();
        if (profiles.Count == 0) { Status.Text = "请先选择一个或多个 Profile。"; return; }
        if (profiles.Any(profile => string.IsNullOrWhiteSpace(profile.LeftPath) || string.IsNullOrWhiteSpace(profile.RightPath))) { Status.Text = "批处理中的每个 Profile 都必须有两个端点。"; return; }
        if (profiles.Count > 1) { new BatchRunWindow(profiles, CurrentSettings.MaxConcurrentCopies, (profile, _) => RunBatchProfileAsync(profile)) { Owner = this }.ShowDialog(); return; }
        try
        {
            var scheduler = new FengSync.Core.Automation.BatchScheduler(CurrentSettings.MaxConcurrentCopies);
            Status.Text = $"正在以最多 {CurrentSettings.MaxConcurrentCopies} 路并发执行 {profiles.Count} 个 Profile…";
            var results = await scheduler.RunAsync(profiles.Select(profile => (Func<CancellationToken, Task<ProfileRunResult>>)(token => RunBatchProfileAsync(profile))));
            var successful = results.Where(x => x.Succeeded).Select(x => x.Value!).ToList();
            var failures = results.Count(x => !x.Succeeded);
            Status.Text = failures == 0
                ? $"批处理完成：{profiles.Count} 个 Profile，计划 {successful.Sum(x => x.Planned)} 项，执行 {successful.Sum(x => x.Executed)} 项。"
                : $"批处理完成：{successful.Count} 成功，{failures} 失败；其余 Profile 未受影响。";
        }
        catch (Exception ex) { Status.Text = "批处理失败：" + ex.Message; }
    }
    private void ProfileList_SelectionChanged(object s, SelectionChangedEventArgs e) { RefreshProfileSelection(); if (ProfileList.SelectedItem is SyncProfile profile) { _settings = _settings with { LastSelectedProfileId = profile.Id }; ApplyProfile(profile); } }
    private void ApplyProfile(SyncProfile profile)
    {
        LeftPath.Text = profile.LeftPath; RightPath.Text = profile.RightPath; SyncModeBox.SelectedIndex = (int)profile.Mode; UpdateSettingsText();
        var compatibility = new FeatureCapabilityService().Evaluate(profile);
        CompareButton.IsEnabled = compatibility.CanRun; RunProfileButton.IsEnabled = compatibility.CanRun;
        Status.Text = compatibility.CanRun ? $"已载入配置：{profile.Name}" : $"Profile 需要修复：{compatibility.Summary}";
    }
    private async Task BuildPlanAsync(CancellationToken cancellationToken)
    {
        var profile = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create("临时", LeftPath.Text, RightPath.Text) with { Mode = SelectedMode }; var compatibility = new FeatureCapabilityService().Evaluate(profile with { LeftPath = LeftPath.Text, RightPath = RightPath.Text, Mode = SelectedMode }); if (!compatibility.CanRun) throw new InvalidOperationException("该 Profile 需要修复：" + compatibility.Summary); var effective = CurrentSettings; (_left, _right) = await CreateEndpointsAsync(LeftPath.Text, RightPath.Text);
        var configurationSafety = _left is LocalEndpoint configLeft && _right is LocalEndpoint configRight ? new SafetyValidator().ValidateConfiguration(configLeft.Root, configRight.Root, effective.Versioning?.ArchiveDirectory) : SafetyValidationResult.Pass;
        if (configurationSafety.HasBlockingIssues) throw new InvalidOperationException(string.Join(" ", configurationSafety.Issues.Select(x => x.Message)));
        // Surface progress phases in the status bar so the user knows the compare is still working while
        // scans or analysis run. CompareButton is also disabled, but only status text change can communicate
        // "still running" without extra dialogs. ScanAsync itself doesn't surface per-file progress (rclone's
        // list is a single RPC; LocalEndpoint enumerates synchronously), so phase-based feedback is the
        // honest minimum — see issue #3.
        Status.Text = $"正在扫描端点：左 {LeftPath.Text}  ·  右 {RightPath.Text}";
        var leftProgress = new Progress<ScanProgress>(x => Status.Text = x.Completed ? $"左侧扫描完成：{x.ItemsScanned} 项。正在等待右侧…" : $"正在扫描左侧：已发现 {x.ItemsScanned} 项{(string.IsNullOrEmpty(x.CurrentPath) ? "" : " · " + x.CurrentPath)}");
        var rightProgress = new Progress<ScanProgress>(x => Status.Text = x.Completed ? $"右侧扫描完成：{x.ItemsScanned} 项。正在分析差异…" : $"正在扫描右侧：已发现 {x.ItemsScanned} 项{(string.IsNullOrEmpty(x.CurrentPath) ? "" : " · " + x.CurrentPath)}");
        var scans = await Task.WhenAll(_left.ScanAsync(leftProgress, cancellationToken), _right.ScanAsync(rightProgress, cancellationToken));
        Status.Text = $"扫描完成：左侧 {scans[0].Count} 项  ·  右侧 {scans[1].Count} 项，正在分析差异…";
        var left = scans[0].ToDictionary(x => x.Path, _left.Capabilities.EffectivePaths.CreateComparer()); var right = scans[1].ToDictionary(x => x.Path, _right.Capabilities.EffectivePaths.CreateComparer()); var baselineRepository = new BaselineRepository(); var baseline = await baselineRepository.LoadAsync(_left, _right); var baselineWarning = baselineRepository.LastLoadWarning;
        // Build the complete diff, then render filtered paths as deselected rows. Filters
        // remain a sync boundary because ignored operations are never selected/executed.
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
        if (planSafety.HasBlockingIssues && !SyncConfirmationPolicy.CanOverrideWithProfileName(planSafety)) throw new InvalidOperationException(string.Join(" ", planSafety.Issues.Select(x => x.Message)));
        var risk = SyncRiskSummary.Create(_plan, left, right);
        SafetySummary.Text = planSafety.HasBlockingIssues ? "安全检查：阻断（删除阈值可在同步确认中一次性放行）" : SyncConfirmationPolicy.RequiresConfirmation(risk) ? $"安全检查：警告 · 覆盖 {risk.Overwrites} 项，删除 {risk.Deletes} 项，传输 {FormatBytes(risk.TransferBytes)}" : "安全检查：通过";
        SafetySummary.Foreground = planSafety.HasBlockingIssues ? Brushes.Firebrick : SyncConfirmationPolicy.RequiresConfirmation(risk) ? Brushes.DarkOrange : Brushes.ForestGreen;
        _comparison = new ComparisonSnapshot
        {
            SnapshotId = Guid.NewGuid(),
            Left = new EndpointSnapshot { Endpoint = _left.Profile, Paths = _left.Capabilities.EffectivePaths, StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow, Entries = scans[0], ByPath = left },
            Right = new EndpointSnapshot { Endpoint = _right.Profile, Paths = _right.Capabilities.EffectivePaths, StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow, Entries = scans[1], ByPath = right },
            Mode = ComparisonMode.TimeAndSize, TimeTolerance = TimeSpan.FromSeconds(effective.TimeToleranceSeconds), Baseline = baseline, Plan = _plan
        };
        _snapshot = PlanSnapshot.FromComparison(_plan, _comparison); _rows.Clear(); foreach (var op in _plan.Operations) _rows.Add(new(op, left.GetValueOrDefault(op.Path), right.GetValueOrDefault(op.Path), ignoredPaths.Contains(op.Path))); RefreshSummary(); Status.Text = baselineWarning ?? $"{ModeTitle()} 比较完成：左侧 {left.Count} 项  ·  右侧 {right.Count} 项  ·  {_plan.Operations.Count} 个差异/提示（其中 {ignoredPaths.Count} 项已忽略）。";
    }
    private async Task<(IEndpoint Left, IEndpoint Right)> CreateEndpointsAsync(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) throw new InvalidOperationException("请先填写两个端点。");
        await DisposeRcloneAsync();
        var needsRemote = IsCloud(left) || IsCloud(right);
        if (needsRemote) _rclone = await RcloneDaemon.StartAsync(BundledRclone.ExecutablePath, BundledRclone.ConfigPath);
        return (CreateEndpoint(left), CreateEndpoint(right));
    }
    private Task<ProfileRunResult> RunBatchProfileAsync(SyncProfile profile)
    {
        // Batch execution must use the same paired-baseline transaction as CLI/scheduler.
        return new ProfileRunner(applicationSettings: _settings).RunAsync(profile);
    }
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
    // Cloud endpoint management now lives in a dedicated modal (issue #1): a clean list + 刷新/新建/重新登录/删除
    // actions, a "浏览远程目录" panel, and a "新建端点" editor. The ☁ buttons beside each endpoint box and the
    // "工具 → 云端端点管理…" menu both open it; the manager itself reports which side the chosen URI fills.
    private void AddCloudEndpoint_Click(object s, RoutedEventArgs e) => OpenCloudEndpointManager();
    private void ManageCloudEndpoints_Click(object s, RoutedEventArgs e) => OpenCloudEndpointManager();
    private void OpenCloudEndpointManager()
    {
        var manager = new CloudEndpointManagerWindow { Owner = this };
        if (manager.ShowDialog() != true || manager.ResultUri is null) return;
        var target = manager.ResultSide == "Right" ? RightPath : LeftPath;
        target.Text = manager.ResultUri;
        Status.Text = $"已将云端端点添加到{(manager.ResultSide == "Right" ? "右" : "左")}侧：{manager.ResultUri}";
    }
    private void Options_Click(object s, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings, ApplyApplicationSettingsAsync, ShowSftpServerSettingsAsync, CleanupExpiredLocalTemporaryFilesAsync) { Owner = this };
        dialog.ShowDialog();
    }

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
    private async void OpenProfileFile_Click(object s, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "Feng Sync files (*.fengsync.json;*.fengsync.batch.json)|*.fengsync.json;*.fengsync.batch.json|JSON files (*.json)|*.json" }; if (dialog.ShowDialog() != true) return; var raw = await File.ReadAllTextAsync(dialog.FileName); BatchJob? batch = null; try { if (JsonDocument.Parse(raw).RootElement.ValueKind == JsonValueKind.Object) batch = JsonSerializer.Deserialize<BatchJob>(raw); } catch (JsonException) { } var loaded = batch?.Profiles?.Count > 0 ? batch.Profiles : await new ProfileStore(dialog.FileName).LoadAsync(); if (loaded.Count == 0) { Status.Text = "该文件没有可打开的 Profile。"; return; } foreach (var profile in loaded.Where(x => _profiles.All(existing => existing.Id != x.Id))) _profiles.Add(profile); ProfileList.SelectedItem = loaded[0]; await PersistProfilesAsync(); Status.Text = batch is null ? $"已打开 {loaded.Count} 个 Profile。" : $"已打开批处理作业“{batch.Name}”（{loaded.Count} 个 Profile）。"; }
    private async void ExportProfile_Click(object s, RoutedEventArgs e) { await SaveProfileToListAsync(); var profile = ProfileList.SelectedItem as SyncProfile; if (profile is null) return; var dialog = new SaveFileDialog { Filter = "Feng Sync Profile (*.fengsync.json)|*.fengsync.json", FileName = profile.Name + ".fengsync.json" }; if (dialog.ShowDialog() != true) return; await new ProfileStore(dialog.FileName).SaveAsync([profile]); Status.Text = "Profile 已保存为文件。"; }
    private async void SaveBatchJob_Click(object s, RoutedEventArgs e) { await SaveProfileToListAsync(); var profiles = ProfileList.SelectedItems.Cast<SyncProfile>().ToList(); if (profiles.Count == 0 && ProfileList.SelectedItem is SyncProfile profile) profiles.Add(profile); if (profiles.Count == 0) return; var dialog = new SaveFileDialog { Filter = "Feng Sync Batch Job (*.fengsync.batch.json)|*.fengsync.batch.json", FileName = "batch.fengsync.batch.json" }; if (dialog.ShowDialog() != true) return; var job = new BatchJob(Path.GetFileNameWithoutExtension(dialog.FileName), profiles); await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(job)); Status.Text = $"批处理作业已保存（{profiles.Count} 个 Profile）。"; }
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
    private void ShowRunHistory_Click(object s, RoutedEventArgs e)
        => new RunHistoryWindow((ProfileList.SelectedItem as SyncProfile)?.Id) { Owner = this }.ShowDialog();
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
        // Closing the only main window is an application exit, never a request to
        // leave an invisible sync process behind.  All cancellable work is stopped
        // before owned daemons and the SFTP child process are torn down.
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
        catch { /* Shutdown must not strand the window if a settings file is unavailable. */ }
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
public sealed class ComparisonRow : INotifyPropertyChanged
{
    public ComparisonRow(SyncOperation operation, EntrySnapshot? left, EntrySnapshot? right, bool isFilterExcluded = false) { Operation = operation; Left = left; Right = right; IsFilterExcluded = isFilterExcluded; Refresh(); }
    public SyncOperation Operation { get; } public EntrySnapshot? Left { get; } public EntrySnapshot? Right { get; } public bool IsFilterExcluded { get; private set; } public bool IsIgnored { get; set; } public bool Selected { get => Operation.Selected; set { if (IsIgnored || IsFilterExcluded || Operation.Selected == value) return; Operation.Selected = value; OnPropertyChanged(); } } public string LeftDisplay { get; private set; } = ""; public string RightDisplay { get; private set; } = ""; public string LeftSize { get; private set; } = ""; public string RightSize { get; private set; } = ""; public string ActionDisplay { get; private set; } = ""; public Brush ActionBrush { get; private set; } = Brushes.DimGray; public string Reason => Operation.Reason;
    /// <summary>Explicit toolbar coverage overrides this comparison's temporary exclusions without editing the persisted Profile.</summary>
    public void EnableForCurrentPlan() { IsIgnored = false; IsFilterExcluded = false; Operation.Selected = true; OnPropertyChanged(nameof(Selected)); }
    public void Refresh() { LeftDisplay = Describe(Left); RightDisplay = Describe(Right); LeftSize = Size(Left); RightSize = Size(Right); (ActionDisplay, ActionBrush) = IsIgnored || IsFilterExcluded ? ("⊘", Brushes.DimGray) : Operation.IsConflict ? ("⚠", Brushes.DarkOrange) : Operation.Kind switch { OperationKind.CopyLeftToRight => ("✚→", Brushes.ForestGreen), OperationKind.CopyRightToLeft => ("←✚", Brushes.ForestGreen), OperationKind.DeleteLeft => ("←✖", Brushes.Firebrick), OperationKind.DeleteRight => ("✖→", Brushes.Firebrick), OperationKind.CreateLeftDirectory => ("←✚", Brushes.ForestGreen), OperationKind.CreateRightDirectory => ("✚→", Brushes.ForestGreen), OperationKind.Blocked => ("⛔", Brushes.Firebrick), _ => ("=", Brushes.DimGray) }; }
    private static string Describe(EntrySnapshot? e) => e is null ? "" : e.Kind == EntryKind.Directory ? "▰ " + e.Path : "▱ " + e.Path;
    private static string Size(EntrySnapshot? e) => e?.Fingerprint is null ? "" : e.Fingerprint.Size.ToString("N0");
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
}
