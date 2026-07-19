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
    private readonly ObservableCollection<ComparisonRow> _rows = []; private readonly ObservableCollection<SyncProfile> _profiles = []; private readonly ProfileStore _profileStore = new(); private SyncPlan? _plan; private PlanSnapshot? _snapshot; private IEndpoint? _left, _right; private RcloneDaemon? _rclone; private ApplicationSettings _settings; private CancellationTokenSource? _syncCancellation; private bool _closing;
    private SyncMode SelectedMode => (SyncMode)Math.Max(0, SyncModeBox?.SelectedIndex ?? 0);
    public MainWindow() { InitializeComponent(); Comparison.ItemsSource = _rows; ProfileList.ItemsSource = _profiles; _settings = new(); UpdateSettingsText(); Status.Text = "正在加载设置…"; Loaded += async (_, _) => await InitializeAsync(); }
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        try { await BuildPlanAsync(); }
        catch (Exception ex) { SyncButton.IsEnabled = false; Status.Text = ex.Message; }
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
    private async Task BuildPlanAsync()
    {
        var profile = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create("临时", LeftPath.Text, RightPath.Text) with { Mode = SelectedMode }; var compatibility = new FeatureCapabilityService().Evaluate(profile with { LeftPath = LeftPath.Text, RightPath = RightPath.Text, Mode = SelectedMode }); if (!compatibility.CanRun) throw new InvalidOperationException("该 Profile 需要修复：" + compatibility.Summary); var effective = CurrentSettings; (_left, _right) = await CreateEndpointsAsync(LeftPath.Text, RightPath.Text);
        var configurationSafety = _left is LocalEndpoint configLeft && _right is LocalEndpoint configRight ? new SafetyValidator().ValidateConfiguration(configLeft.Root, configRight.Root, effective.Versioning?.ArchiveDirectory) : SafetyValidationResult.Pass;
        if (configurationSafety.HasBlockingIssues) throw new InvalidOperationException(string.Join(" ", configurationSafety.Issues.Select(x => x.Message)));
        var scans = await Task.WhenAll(_left.ScanAsync(), _right.ScanAsync()); var left = scans[0].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase); var right = scans[1].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase); var baseline = SelectedMode == SyncMode.TwoWay ? await new BaselineRepository().LoadAsync(_left, _right) : null; _plan = new ModePlanner().Build(SelectedMode, left.Values, right.Values, baseline, effective.Filter);
        var planSafety = new SafetyValidator().ValidatePlan(_plan, left.Count, right.Count, SelectedMode, profile.MaxDeletes, profile.MaxDeleteRatio).Combine(new SafetyValidator().ValidateCapacity(_plan, left, right, _left, _right));
        if (planSafety.HasBlockingIssues && !SyncConfirmationPolicy.CanOverrideWithProfileName(planSafety)) throw new InvalidOperationException(string.Join(" ", planSafety.Issues.Select(x => x.Message)));
        var risk = SyncRiskSummary.Create(_plan, left, right);
        SafetySummary.Text = planSafety.HasBlockingIssues ? "安全检查：阻断（删除阈值可在同步确认中一次性放行）" : SyncConfirmationPolicy.RequiresConfirmation(risk) ? $"安全检查：警告 · 覆盖 {risk.Overwrites} 项，删除 {risk.Deletes} 项，传输 {FormatBytes(risk.TransferBytes)}" : "安全检查：通过";
        SafetySummary.Foreground = planSafety.HasBlockingIssues ? Brushes.Firebrick : SyncConfirmationPolicy.RequiresConfirmation(risk) ? Brushes.DarkOrange : Brushes.ForestGreen;
        _snapshot = await PlanSnapshot.CaptureAsync(_plan, _left, _right); _rows.Clear(); foreach (var op in _plan.Operations) _rows.Add(new(op, left.GetValueOrDefault(op.Path), right.GetValueOrDefault(op.Path))); RefreshSummary(); Status.Text = $"{ModeTitle()} 比较完成。勾选要执行的差异，并裁决所有冲突。";
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
    private void AddCloudEndpoint_Click(object s, RoutedEventArgs e)
    {
        var target = (s as FrameworkElement)?.Tag?.ToString() == "Right" ? RightPath : LeftPath;
        var type = new ComboBox { Margin = new Thickness(0, 4, 0, 8), ItemsSource = new[] { "Google Drive", "SFTP" }, SelectedIndex = 0 };
        var accounts = new ListBox { Height = 115, DisplayMemberPath = "Display", Margin = new Thickness(0, 4, 0, 8) };
        var refreshAccounts = new Button { Content = "刷新账号列表", MinWidth = 105 }; var reconnect = new Button { Content = "重新登录", MinWidth = 85, Margin = new Thickness(6, 0, 0, 0) }; var removeAccount = new Button { Content = "清除账号", MinWidth = 85, Margin = new Thickness(6, 0, 0, 0) };
        var name = new TextBox { Text = "云端连接", Margin = new Thickness(0, 4, 0, 8) }; var root = new TextBox { Margin = new Thickness(0, 4, 0, 8) }; var browseRemote = new Button { Content = "浏览远程目录…", MinWidth = 115, Margin = new Thickness(6, 4, 0, 8) };
        var host = new TextBox { Margin = new Thickness(0, 4, 0, 8) }; var port = new TextBox { Text = "22", Margin = new Thickness(0, 4, 0, 8) }; var user = new TextBox { Margin = new Thickness(0, 4, 0, 8) }; var password = new PasswordBox { Margin = new Thickness(0, 4, 0, 12) };
        var sftpFields = new StackPanel(); sftpFields.Children.Add(new TextBlock { Text = "主机" }); sftpFields.Children.Add(host); sftpFields.Children.Add(new TextBlock { Text = "端口" }); sftpFields.Children.Add(port); sftpFields.Children.Add(new TextBlock { Text = "用户名" }); sftpFields.Children.Add(user); sftpFields.Children.Add(new TextBlock { Text = "密码" }); sftpFields.Children.Add(password); sftpFields.Visibility = Visibility.Collapsed;
        type.SelectionChanged += (_, _) => sftpFields.Visibility = type.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        var configure = new Button { Content = "连接并验证", Margin = new Thickness(0, 0, 0, 12) };
        var ok = new Button { Content = "添加到同步端点", IsDefault = true, MinWidth = 120 }; var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 70, Margin = new Thickness(8, 0, 0, 0) }; var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var accountButtons = new StackPanel { Orientation = Orientation.Horizontal }; accountButtons.Children.Add(refreshAccounts); accountButtons.Children.Add(reconnect); accountButtons.Children.Add(removeAccount);
        var rootRow = new DockPanel(); DockPanel.SetDock(browseRemote, Dock.Right); rootRow.Children.Add(browseRemote); rootRow.Children.Add(root);
        var panel = new StackPanel { Margin = new Thickness(18), Width = 360 }; panel.Children.Add(new TextBlock { Text = "连接云端端点", FontSize = 18, FontWeight = FontWeights.Bold }); panel.Children.Add(new TextBlock { Text = "已保存的云端账号（Google Drive 当前由 rclone 不提供邮箱时显示连接 ID）", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 2) }); panel.Children.Add(accounts); panel.Children.Add(accountButtons); panel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 8) }); panel.Children.Add(new TextBlock { Text = "新建：Google Drive 会在默认浏览器完成授权；SFTP 使用下方填写的连接信息。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }); panel.Children.Add(new TextBlock { Text = "服务" }); panel.Children.Add(type); panel.Children.Add(new TextBlock { Text = "显示名称" }); panel.Children.Add(name); panel.Children.Add(sftpFields); panel.Children.Add(new TextBlock { Text = "远程根目录（也可用浏览按钮选择）" }); panel.Children.Add(rootRow); panel.Children.Add(configure); panel.Children.Add(buttons);
        var dialog = new Window { Title = "添加云端端点", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize };
        var remoteId = "fengsync_" + Guid.NewGuid().ToString("N"); var configured = false;
        async Task RefreshAccountsAsync() { accounts.ItemsSource = await LoadCloudAccountsAsync(); }
        refreshAccounts.Click += async (_, _) => await RefreshAccountsAsync();
        reconnect.Click += async (_, _) => { if (accounts.SelectedItem is not CloudAccount account) return; try { await RunRcloneAsync("config", "reconnect", account.Name + ":", "--config", BundledRclone.ConfigPath); await RefreshAccountsAsync(); } catch (Exception ex) { MessageBox.Show(ex.Message, "重新登录失败", MessageBoxButton.OK, MessageBoxImage.Error); } };
        removeAccount.Click += async (_, _) => { if (accounts.SelectedItem is not CloudAccount account || MessageBox.Show($"清除云端账号“{account.Name}”？本地 Profile 不会被删除。", "清除账号", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; try { await RunRcloneAsync("config", "delete", account.Name, "--config", BundledRclone.ConfigPath); await RefreshAccountsAsync(); } catch (Exception ex) { MessageBox.Show(ex.Message, "清除失败", MessageBoxButton.OK, MessageBoxImage.Error); } };
        configure.Click += async (_, _) => { try { configure.IsEnabled = false; configure.Content = type.SelectedIndex == 0 ? "正在等待浏览器授权…" : "正在验证连接…"; await ConfigureCloudAsync(remoteId, type.SelectedIndex == 0, host.Text, port.Text, user.Text, password.Password); configured = true; configure.Content = "连接已验证"; await RefreshAccountsAsync(); } catch (Exception ex) { configure.Content = "连接并验证"; MessageBox.Show(ex.Message, "Feng Sync", MessageBoxButton.OK, MessageBoxImage.Error); } finally { configure.IsEnabled = true; } };
        browseRemote.Click += async (_, _) =>
        {
            var account = accounts.SelectedItem as CloudAccount; var selectedId = account?.Name ?? (configured ? remoteId : null);
            if (string.IsNullOrWhiteSpace(selectedId)) { MessageBox.Show("请先选择已保存账号，或先点击“连接并验证”。", "Feng Sync", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            try { browseRemote.IsEnabled = false; root.Text = await PickRemoteDirectoryAsync(selectedId, root.Text); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "读取远程目录失败", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { browseRemote.IsEnabled = true; }
        };
        ok.Click += (_, _) => { var account = accounts.SelectedItem as CloudAccount; if (account is null && !configured) { MessageBox.Show("请先连接并验证新账号，或从已保存账号列表选择一个。", "Feng Sync"); return; } var selectedId = account?.Name ?? remoteId; var isGoogle = account?.IsGoogleDrive ?? type.SelectedIndex == 0; target.Text = (isGoogle ? "gdrive://" : "sftp://") + selectedId + (string.IsNullOrWhiteSpace(root.Text) ? "" : "/" + root.Text.Trim().TrimStart('/')); dialog.DialogResult = true; };
        dialog.Loaded += async (_, _) => await RefreshAccountsAsync(); dialog.ShowDialog();
    }
    private async Task<string> PickRemoteDirectoryAsync(string remote, string currentPath)
    {
        await using var daemon = await RcloneDaemon.StartAsync(BundledRclone.ExecutablePath, BundledRclone.ConfigPath);
        // RC expects an rclone filesystem specifier ("remote:"), not the raw profile name.
        var filesystem = remote.EndsWith(':') ? remote : remote + ":";
        var directories = await daemon.Client.ListDirectoriesAsync(filesystem, "", false);
        var tree = new TreeView { Margin = new Thickness(12), MinWidth = 420, MinHeight = 360 };
        TreeViewItem Create(string name, string path)
        {
            var item = new TreeViewItem { Header = string.IsNullOrEmpty(path) ? "/（根目录）" : "📁 " + name, Tag = path };
            item.Expanded += async (_, _) =>
            {
                if (item.Items.Count != 1 || (item.Items[0] as TreeViewItem)?.Tag is not null) return;
                item.Items.Clear();
                try
                {
                    var children = await daemon.Client.ListDirectoriesAsync(filesystem, path, false);
                    foreach (var child in children.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => RemoteDirectoryTree.RelativeToListingRoot(x, path)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        var childName = child.Contains('/') ? child[(child.LastIndexOf('/') + 1)..] : child;
                        var childPath = string.IsNullOrEmpty(path) ? child : path.TrimEnd('/') + "/" + child;
                        item.Items.Add(Create(childName, childPath));
                    }
                }
                catch (Exception ex) { item.Items.Add(new TreeViewItem { Header = "无法读取子目录：" + ex.Message, IsEnabled = false }); }
            };
            // The placeholder creates an expand affordance; it is replaced on first expansion.
            item.Items.Add(new TreeViewItem { Header = "正在加载…", Tag = null });
            return item;
        }
        var rootItem = Create("", ""); rootItem.Items.Clear(); foreach (var directory in directories.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim('/')).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) rootItem.Items.Add(Create(directory, directory)); tree.Items.Add(rootItem); rootItem.IsSelected = true; rootItem.IsExpanded = true;
        var use = new Button { Content = "选择此文件夹", IsDefault = true, MinWidth = 110 }; var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), MinWidth = 70 }; var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) }; buttons.Children.Add(use); buttons.Children.Add(cancel);
        var layout = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons); layout.Children.Add(tree);
        var picker = new Window { Title = $"选择 {remote}: 中的文件夹", Owner = this, Content = layout, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.CanResize };
        string? selected = currentPath.Trim('/'); tree.SelectedItemChanged += (_, _) => selected = (tree.SelectedItem as TreeViewItem)?.Tag as string ?? selected;
        use.Click += (_, _) => picker.DialogResult = true;
        return picker.ShowDialog() == true ? selected ?? "" : currentPath;
    }
    private sealed record CloudAccount(string Name, string Type) { public bool IsGoogleDrive => Type.Equals("drive", StringComparison.OrdinalIgnoreCase); public string Display => $"{(IsGoogleDrive ? "Google Drive" : "SFTP")}  ·  {Name}"; }
    private static async Task<IReadOnlyList<CloudAccount>> LoadCloudAccountsAsync()
    {
        if (!File.Exists(BundledRclone.ConfigPath)) return [];
        var json = await RunRcloneAsync("config", "dump", "--config", BundledRclone.ConfigPath);
        return RcloneConfig.ParseDump(json).Select(x => new CloudAccount(x.Name, x.Type)).OrderBy(x => x.Display).ToList();
    }
    private static async Task<string> RunRcloneAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(BundledRclone.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true }; foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动内置 rclone。"); var output = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim()); return output;
    }
    private static async Task ConfigureCloudAsync(string remoteId, bool googleDrive, string host, string port, string user, string password)
    {
        if (!googleDrive && (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user))) throw new InvalidOperationException("SFTP 必须填写主机和用户名。");
        Directory.CreateDirectory(Path.GetDirectoryName(BundledRclone.ConfigPath)!);
        var start = new ProcessStartInfo(BundledRclone.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("config"); start.ArgumentList.Add("create"); start.ArgumentList.Add(remoteId); start.ArgumentList.Add(googleDrive ? "drive" : "sftp"); start.ArgumentList.Add("--config"); start.ArgumentList.Add(BundledRclone.ConfigPath);
        if (!googleDrive) { start.ArgumentList.Add("host"); start.ArgumentList.Add(host); start.ArgumentList.Add("user"); start.ArgumentList.Add(user); start.ArgumentList.Add("port"); start.ArgumentList.Add(string.IsNullOrWhiteSpace(port) ? "22" : port); if (!string.IsNullOrWhiteSpace(password)) { start.ArgumentList.Add("pass"); start.ArgumentList.Add(password); } }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动内置 rclone。"); await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new InvalidOperationException("云端授权失败：" + await process.StandardError.ReadToEndAsync());
        var verify = new ProcessStartInfo(BundledRclone.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }; verify.ArgumentList.Add("lsd"); verify.ArgumentList.Add(remoteId + ":"); verify.ArgumentList.Add("--config"); verify.ArgumentList.Add(BundledRclone.ConfigPath);
        using var check = Process.Start(verify) ?? throw new InvalidOperationException("无法验证云端连接。"); await check.WaitForExitAsync(); if (check.ExitCode != 0) throw new InvalidOperationException("云端连接失败：" + await check.StandardError.ReadToEndAsync());
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
    private void ManageRealtimeMonitor_Click(object s, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not SyncProfile profile) { Status.Text = "请先选择一个 Profile。"; return; }
        new RealtimeMonitorWindow(profile, (item, token) => RunBatchProfileAsync(item)) { Owner = this }.Show();
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
        _syncCancellation?.Cancel(); _closing = true; CloseWhenReadyAsync();
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
