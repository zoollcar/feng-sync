using FengSync.Core;
using FengSync.Core.Capabilities;
using FengSync.Core.Configuration;
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
    private readonly ObservableCollection<ComparisonRow> _rows = []; private readonly ObservableCollection<SyncProfile> _profiles = []; private readonly ProfileStore _profileStore = new(); private SyncPlan? _plan; private PlanSnapshot? _snapshot; private IEndpoint? _left, _right; private RcloneDaemon? _rclone; private ApplicationSettings _settings; private CancellationTokenSource? _syncCancellation; private CancellationTokenSource? _compareCancellation; private bool _closing;
    private SyncMode SelectedMode => (SyncMode)Math.Max(0, SyncModeBox?.SelectedIndex ?? 0);
    public MainWindow() { InitializeComponent(); Comparison.ItemsSource = _rows; ProfileList.ItemsSource = _profiles; _settings = new(); UpdateSettingsText(); Status.Text = "正在加载设置…"; Loaded += async (_, _) => await InitializeAsync(); }
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        // Guard against re-entry while a comparison is already running; CompareButton is also disabled below
        // so the user gets immediate visual feedback rather than a silent no-op.
        if (_compareCancellation is not null) return;
        _compareCancellation = new();
        var token = _compareCancellation.Token;
        CompareButton.IsEnabled = false;
        try
        {
            Status.Text = $"正在准备比较：{LeftPath.Text} ↔ {RightPath.Text}";
            await BuildPlanAsync(token);
        }
        catch (OperationCanceledException) { Status.Text = "比较已取消。"; }
        catch (Exception ex) { SyncButton.IsEnabled = false; Status.Text = ex.Message; }
        finally
        {
            CompareButton.IsEnabled = true;
            _compareCancellation?.Dispose();
            _compareCancellation = null;
        }
    }
    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _left is null || _right is null) return;
        ProgressWindow? progressDialog = null;
        try
        {
            var effective = CurrentSettings; Comparison.CommitEdit(DataGridEditingUnit.Cell, true); Comparison.CommitEdit(DataGridEditingUnit.Row, true);
            var operations = _rows.Select(x => x.Operation).ToList(); var current = new SyncPlan(operations);
            if (!current.CanExecute || _snapshot is null) { Status.Text = "请先选择操作并裁决所有冲突，然后重新比较。"; return; }
            var profile = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create("临时", LeftPath.Text, RightPath.Text);
            var scans = await Task.WhenAll(_left.ScanAsync(), _right.ScanAsync());
            var leftEntries = scans[0].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase); var rightEntries = scans[1].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var safety = new SafetyValidator().ValidatePlan(current, leftEntries.Count, rightEntries.Count, SelectedMode, profile.MaxDeletes, profile.MaxDeleteRatio)
                .Combine(new SafetyValidator().ValidateCapacity(current, leftEntries, rightEntries, _left, _right));
            var risk = SyncRiskSummary.Create(current, leftEntries, rightEntries);
            var thresholdOverride = SyncConfirmationPolicy.CanOverrideWithProfileName(safety);
            if (safety.HasBlockingIssues && !thresholdOverride) { Status.Text = string.Join(" ", safety.Issues.Select(x => x.Message)); return; }
            if (SyncConfirmationPolicy.RequiresConfirmation(risk) || thresholdOverride)
            {
                var confirmation = new SyncConfirmationWindow(risk, safety, profile.Name, risk.TransferBytes) { Owner = this };
                if (confirmation.ShowDialog() != true) { Status.Text = "已取消高风险同步确认。"; return; }
            }
            SyncButton.IsEnabled = false; _syncCancellation = new(); var total = operations.Count(x => x.Selected && x.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft);
            Status.Text = $"正在以 {effective.MaxConcurrentCopies} 路并发同步…"; progressDialog = new ProgressWindow(total, !_settings.ShowCompleted) { Owner = this }; progressDialog.Show();
            var baselineRepository = new BaselineRepository(); var transaction = SelectedMode == SyncMode.TwoWay ? await baselineRepository.BeginAsync(_left, _right, _syncCancellation.Token) : null;
            var run = await new SyncExecutor().ExecuteAsync(_snapshot, _left, _right, new Progress<TransferProgress>(p => progressDialog.Report(p)), _syncCancellation.Token, effective.VerifyCopies, effective.Versioning, effective.MaxConcurrentCopies, new TaskJournalStore());
            if (transaction is not null)
            {
                transaction = run.Operations.Where(x => x.Stage == TransferStage.Committed).Aggregate(transaction, (current, item) => current.RecordCommitted(item.Path));
                await baselineRepository.SaveAsync(transaction, _syncCancellation.Token);
                await baselineRepository.CommitAsync(transaction, _left, _right, run.Succeeded, _syncCancellation.Token);
            }
            progressDialog.SetRetry(operations, async retryPlan =>
            {
                var retrySnapshot = await PlanSnapshot.CaptureAsync(retryPlan, _left, _right);
                return await new SyncExecutor().ExecuteAsync(retrySnapshot, _left, _right, new Progress<TransferProgress>(p => progressDialog.Report(p)), CancellationToken.None, effective.VerifyCopies, effective.Versioning, effective.MaxConcurrentCopies, new TaskJournalStore());
            });
            if (!run.Succeeded)
            {
                Status.Text = $"同步部分失败：{run.FailedOperations} 个操作失败；基线未变更。";
                progressDialog.Complete(run, $"{run.FailedOperations} 个操作失败。可查看错误详情、保存日志，或重试可重试失败项。");
                return;
            }
            Status.Text = "同步完成。"; progressDialog.Complete(run, "所有选中的操作已完成；双向基线已安全提交。");
        }
        catch (OperationCanceledException) { Status.Text = "同步已取消。"; progressDialog?.Complete(new SyncRunResult(Guid.NewGuid(), []), "同步已取消。", cancelled: true); }
        catch (Exception ex) { Status.Text = "同步失败：" + ex.Message; progressDialog?.Complete(false, ex.Message); }
        finally { _syncCancellation?.Dispose(); _syncCancellation = null; RefreshSummary(); }
    }
    private void KeepLeft_Click(object sender, RoutedEventArgs e) => ResolveSelected(true); private void KeepRight_Click(object sender, RoutedEventArgs e) => ResolveSelected(false);
    private void ResolveSelected(bool left) { if (Comparison.SelectedItem is not ComparisonRow row) { Status.Text = "请选择要修改覆盖方向的行。"; return; } try { row.Operation.OverrideCopyDirection(left); row.Refresh(); Comparison.Items.Refresh(); RefreshSummary(); Status.Text = left ? "已设置为左侧覆盖右侧。" : "已设置为右侧覆盖左侧。"; } catch (Exception ex) { Status.Text = ex.Message; } }
    // Action-cell context-menu handlers. The menu items reuse the same mutation logic as the top-bar
    // KeepLeft/KeepRight buttons so right-click and toolbar produce identical results, including the
    // InvalidOperationException path for unresolvable conflicts (e.g. Blocked rows whose KeepLeft/KeepRight
    // are null). The "temporarily ignore" item deselects the row so the operation is skipped at sync time
    // — there is no OperationKind for ignore in the model, and unchecking is the existing escape hatch.
    private void ActionMenu_KeepLeft_Click(object sender, RoutedEventArgs e) => ApplyActionFromMenu(sender, true);
    private void ActionMenu_KeepRight_Click(object sender, RoutedEventArgs e) => ApplyActionFromMenu(sender, false);
    private void ActionMenu_Ignore_Click(object sender, RoutedEventArgs e) => IgnoreFromMenu(sender);
    private void ApplyActionFromMenu(object sender, bool keepLeft)
    {
        if (GetRowFromMenu(sender) is not ComparisonRow row) { Status.Text = "请选择要修改覆盖方向的行。"; return; }
        try { row.Operation.OverrideCopyDirection(keepLeft); row.Refresh(); Comparison.Items.Refresh(); RefreshSummary(); Status.Text = keepLeft ? "已设置为左侧覆盖右侧。" : "已设置为右侧覆盖左侧。"; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void IgnoreFromMenu(object sender)
    {
        if (GetRowFromMenu(sender) is not ComparisonRow row) { Status.Text = "请选择要忽略的行。"; return; }
        if (!row.Selected) { Status.Text = "该行已忽略。"; return; }
        row.Selected = false; row.Refresh(); Comparison.Items.Refresh(); RefreshSummary(); Status.Text = "已临时忽略该行（取消勾选可恢复）。";
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
    private void RefreshSummary() { var selected = _rows.Count(x => x.Selected); var bytes = _rows.Where(x => x.Selected && x.Operation.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft).Sum(x => (x.Operation.Kind == OperationKind.CopyLeftToRight ? x.Left : x.Right)?.Fingerprint?.Size ?? 0); Summary.Text = $"左侧 {_rows.Count(x => x.Left is not null)} 项  ·  右侧 {_rows.Count(x => x.Right is not null)} 项  ·  { _rows.Count } 个差异/提示"; TransferSizeLabel.Text = $"已选待传输：{FormatBytes(bytes)}"; SelectedLabel.Text = selected.ToString(); SyncButton.IsEnabled = _plan is not null && new SyncPlan(_rows.Select(x => x.Operation).ToList()).CanExecute; }
    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KB" : bytes < 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024:N1} MB" : $"{bytes / 1024d / 1024 / 1024:N2} GB";
    private void BrowseLeft_Click(object s, RoutedEventArgs e) => Browse(LeftPath); private void BrowseRight_Click(object s, RoutedEventArgs e) => Browse(RightPath);
    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        (LeftPath.Text, RightPath.Text) = (RightPath.Text, LeftPath.Text);
        if (_plan is not null) await RecompareAsync();
    }
    private static void Browse(System.Windows.Controls.TextBox target) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog() == true) target.Text = dialog.FolderName; }
    private void Comparison_CurrentCellChanged(object s, EventArgs e) => Dispatcher.BeginInvoke(RefreshSummary);
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
            UpdateSettingsText();
            Status.Text = loaded.RecoveredFromCorruption
                ? $"设置文件已损坏，已备份到：{loaded.BackupPath}"
                : loaded.Migrated
                    ? $"程序设置已从 schema v{loaded.MigratedFromSchemaVersion} 迁移；原文件备份在：{loaded.MigrationBackupPath}"
                    : "选择左右端点后点击“比较”。";
        }
        catch (Exception ex) { _settings = new(); Status.Text = "无法加载程序设置：" + ex.Message; }
        await LoadProfilesAsync();
        await ShowRecoveryIfRequiredAsync();
    }
    private async Task ShowRecoveryIfRequiredAsync()
    {
        try
        {
            var coordinator = new RecoveryCoordinator(); var items = await coordinator.FindRecoveryRequiredAsync();
            if (items.Count > 0) new RecoveryWindow(items, coordinator) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { Status.Text = "无法读取恢复记录：" + ex.Message; }
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
        var updated = new ProfileDialogService().Edit(this, original);
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
    private void ProfileList_SelectionChanged(object s, SelectionChangedEventArgs e) { if (ProfileList.SelectedItem is SyncProfile profile) { _settings = _settings with { LastSelectedProfileId = profile.Id }; ApplyProfile(profile); } }
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
        var scans = await Task.WhenAll(_left.ScanAsync(cancellationToken), _right.ScanAsync(cancellationToken));
        Status.Text = $"扫描完成：左侧 {scans[0].Count} 项  ·  右侧 {scans[1].Count} 项，正在分析差异…";
        var left = scans[0].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase); var right = scans[1].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase); var baseline = SelectedMode == SyncMode.TwoWay ? await new BaselineRepository().LoadAsync(_left, _right) : null; _plan = new ModePlanner().Build(SelectedMode, left.Values, right.Values, baseline, effective.Filter);
        Status.Text = $"分析完成：{_plan.Operations.Count} 项差异，正在生成同步计划…";
        var planSafety = new SafetyValidator().ValidatePlan(_plan, left.Count, right.Count, SelectedMode, profile.MaxDeletes, profile.MaxDeleteRatio).Combine(new SafetyValidator().ValidateCapacity(_plan, left, right, _left, _right));
        if (planSafety.HasBlockingIssues && !SyncConfirmationPolicy.CanOverrideWithProfileName(planSafety)) throw new InvalidOperationException(string.Join(" ", planSafety.Issues.Select(x => x.Message)));
        var risk = SyncRiskSummary.Create(_plan, left, right);
        SafetySummary.Text = planSafety.HasBlockingIssues ? "安全检查：阻断（删除阈值可在同步确认中一次性放行）" : SyncConfirmationPolicy.RequiresConfirmation(risk) ? $"安全检查：警告 · 覆盖 {risk.Overwrites} 项，删除 {risk.Deletes} 项，传输 {FormatBytes(risk.TransferBytes)}" : "安全检查：通过";
        SafetySummary.Foreground = planSafety.HasBlockingIssues ? Brushes.Firebrick : SyncConfirmationPolicy.RequiresConfirmation(risk) ? Brushes.DarkOrange : Brushes.ForestGreen;
        _snapshot = await PlanSnapshot.CaptureAsync(_plan, _left, _right, cancellationToken); _rows.Clear(); foreach (var op in _plan.Operations) _rows.Add(new(op, left.GetValueOrDefault(op.Path), right.GetValueOrDefault(op.Path))); RefreshSummary(); Status.Text = $"{ModeTitle()} 比较完成：左侧 {left.Count} 项  ·  右侧 {right.Count} 项  ·  {_plan.Operations.Count} 个差异/提示。勾选要执行的差异，并裁决所有冲突。";
    }
    private async Task<(IEndpoint Left, IEndpoint Right)> CreateEndpointsAsync(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) throw new InvalidOperationException("请先填写两个端点。");
        await DisposeRcloneAsync();
        var needsRemote = IsCloud(left) || IsCloud(right);
        if (needsRemote) _rclone = await RcloneDaemon.StartAsync(BundledRclone.ExecutablePath, BundledRclone.ConfigPath);
        return (CreateEndpoint(left), CreateEndpoint(right));
    }
    private async Task<ProfileRunResult> RunBatchProfileAsync(SyncProfile profile)
    {
        if (!IsCloud(profile.LeftPath) && !IsCloud(profile.RightPath)) return await new ProfileRunner().RunAsync(profile);
        var compatibility = new FeatureCapabilityService().Evaluate(profile);
        if (!compatibility.CanRun) throw new InvalidOperationException($"{profile.Name} 需要修复：{compatibility.Summary}");
        await using var daemon = await RcloneDaemon.StartAsync(BundledRclone.ExecutablePath, BundledRclone.ConfigPath);
        var left = CreateEndpoint(profile.LeftPath, daemon); var right = CreateEndpoint(profile.RightPath, daemon);
        var scans = await Task.WhenAll(left.ScanAsync(), right.ScanAsync());
        var plan = new ModePlanner().Build(profile.Mode, scans[0], scans[1], null, profile.Filter);
        if (!plan.CanExecute && plan.Operations.Any()) throw new InvalidOperationException($"{profile.Name} 遇到未裁决冲突。");
        var selected = plan.Operations.Count(x => x.Selected);
        var effective = EffectiveProfileSettings.Resolve(profile, _settings);
        var safety = new SafetyValidator().ValidatePlan(plan, scans[0].Count, scans[1].Count, profile.Mode, profile.MaxDeletes, profile.MaxDeleteRatio);
        if (safety.HasBlockingIssues) throw new InvalidOperationException(string.Join(" ", safety.Issues.Select(x => x.Message)));
        if (selected > 0)
        {
            var run = await new SyncExecutor().ExecuteAsync(await PlanSnapshot.CaptureAsync(plan, left, right), left, right, verifyCopies: effective.VerifyCopies, versioning: effective.Versioning, maxConcurrentCopies: effective.MaxConcurrentCopies, journals: new TaskJournalStore());
            if (!run.Succeeded) throw new IOException($"{profile.Name} 有 {run.FailedOperations} 个操作失败。");
        }
        return new ProfileRunResult(profile.Id, plan.Operations.Count, selected, DateTimeOffset.UtcNow);
    }
    private IEndpoint CreateEndpoint(string value)
    {
        if (!IsCloud(value)) return new LocalEndpoint(value);
        if (_rclone is null) throw new InvalidOperationException("云端连接未启动。");
        var split = value.Split("://", 2, StringSplitOptions.None); var remoteAndRoot = split[1].Split('/', 2); var type = split[0].Equals("gdrive", StringComparison.OrdinalIgnoreCase) ? EndpointType.GoogleDrive : EndpointType.Sftp;
        return new RcloneEndpoint(_rclone.Client, new EndpointProfile(Guid.NewGuid(), type, remoteAndRoot.Length == 2 ? remoteAndRoot[1] : "", remoteAndRoot[0]), new(false, true, type == EndpointType.GoogleDrive, TimeSpan.FromSeconds(1)));
    }
    private static IEndpoint CreateEndpoint(string value, RcloneDaemon? daemon)
    {
        if (!IsCloud(value)) return new LocalEndpoint(value);
        if (daemon is null) throw new InvalidOperationException("云端连接未启动。");
        var split = value.Split("://", 2, StringSplitOptions.None); var remoteAndRoot = split[1].Split('/', 2); var type = split[0].Equals("gdrive", StringComparison.OrdinalIgnoreCase) ? EndpointType.GoogleDrive : EndpointType.Sftp;
        return new RcloneEndpoint(daemon.Client, new EndpointProfile(Guid.NewGuid(), type, remoteAndRoot.Length == 2 ? remoteAndRoot[1] : "", remoteAndRoot[0]), new(false, true, type == EndpointType.GoogleDrive, TimeSpan.FromSeconds(1)));
    }
    private static bool IsCloud(string value) => value.StartsWith("gdrive://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase);
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
        var dialog = new SettingsWindow(_settings, ApplyApplicationSettingsAsync, ShowSftpServerSettingsAsync) { Owner = this };
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
    private void About_Click(object s, RoutedEventArgs e)
    {
        const string repoUrl = "https://github.com/zoollcar/feng-sync";
        var title = new TextBlock { Text = "Feng Sync", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        var description = new TextBlock { Text = "本地、SFTP 与 Google Drive 的文件比较和同步。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var link = new Hyperlink(new Run(repoUrl)) { NavigateUri = new Uri(repoUrl) };
        link.RequestNavigate += (_, args) =>
        {
            try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("无法打开链接：" + ex.Message, "Feng Sync", MessageBoxButton.OK, MessageBoxImage.Warning); }
            args.Handled = true;
        };
        var repoBlock = new TextBlock { Margin = new Thickness(0, 0, 0, 14) };
        repoBlock.Inlines.Add(new Run("GitHub 仓库："));
        repoBlock.Inlines.Add(link);
        var ok = new Button { Content = "关闭", IsCancel = true, IsDefault = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(18), MinWidth = 360 };
        panel.Children.Add(title);
        panel.Children.Add(description);
        panel.Children.Add(repoBlock);
        panel.Children.Add(ok);
        var dialog = new Window { Title = "关于 Feng Sync", Content = panel, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize };
        ok.Click += (_, _) => dialog.Close();
        dialog.ShowDialog();
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_syncCancellation is not null && MessageBox.Show("同步正在运行。是否取消同步并退出？", "退出 Feng Sync", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _syncCancellation?.Cancel(); _compareCancellation?.Cancel(); _closing = true; CloseWhenReadyAsync();
    }
    private async void CloseWhenReadyAsync()
    {
        try { _settings = _settings with { LastSelectedProfileId = (ProfileList.SelectedItem as SyncProfile)?.Id }; await new SettingsStore().SaveAsync(_settings); await PersistProfilesAsync(); await DisposeRcloneAsync(); }
        catch { /* Shutdown must not strand the window if a settings file is unavailable. */ }
        finally { Close(); }
    }
    protected override void OnClosed(EventArgs e) { base.OnClosed(e); }
    private sealed record BatchJob(string Name, IReadOnlyList<SyncProfile> Profiles);
}
public sealed class ComparisonRow : INotifyPropertyChanged
{
    public ComparisonRow(SyncOperation operation, EntrySnapshot? left, EntrySnapshot? right) { Operation = operation; Left = left; Right = right; Refresh(); }
    public SyncOperation Operation { get; } public EntrySnapshot? Left { get; } public EntrySnapshot? Right { get; } public bool Selected { get => Operation.Selected; set { if (Operation.Selected == value) return; Operation.Selected = value; OnPropertyChanged(); } } public string LeftDisplay { get; private set; } = ""; public string RightDisplay { get; private set; } = ""; public string LeftSize { get; private set; } = ""; public string RightSize { get; private set; } = ""; public string ActionDisplay { get; private set; } = ""; public Brush ActionBrush { get; private set; } = Brushes.DimGray; public string Reason => Operation.Reason;
    public void Refresh() { LeftDisplay = Describe(Left); RightDisplay = Describe(Right); LeftSize = Size(Left); RightSize = Size(Right); (ActionDisplay, ActionBrush) = Operation.IsConflict ? ("⚠", Brushes.DarkOrange) : Operation.Kind switch { OperationKind.CopyLeftToRight => ("✚→", Brushes.ForestGreen), OperationKind.CopyRightToLeft => ("←✚", Brushes.ForestGreen), OperationKind.DeleteLeft => ("←✖", Brushes.Firebrick), OperationKind.DeleteRight => ("✖→", Brushes.Firebrick), OperationKind.CreateLeftDirectory => ("←✚", Brushes.ForestGreen), OperationKind.CreateRightDirectory => ("✚→", Brushes.ForestGreen), OperationKind.Blocked => ("⛔", Brushes.Firebrick), _ => ("=", Brushes.DimGray) }; }
    private static string Describe(EntrySnapshot? e) => e is null ? "" : e.Kind == EntryKind.Directory ? "▰ " + e.Path : "▱ " + e.Path;
    private static string Size(EntrySnapshot? e) => e?.Fingerprint is null ? "" : e.Fingerprint.Size.ToString("N0");
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
}
