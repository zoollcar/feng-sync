using FengSync.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Media;
using System.Diagnostics;

namespace FengSync;
public partial class MainWindow : Window
{
    private readonly ObservableCollection<ComparisonRow> _rows = []; private readonly ObservableCollection<SyncProfile> _profiles = []; private readonly ProfileStore _profileStore = new(); private SyncPlan? _plan; private IEndpoint? _left, _right; private RcloneDaemon? _rclone; private AppSettings _settings;
    private SyncMode SelectedMode => (SyncMode)Math.Max(0, SyncModeBox?.SelectedIndex ?? 0);
    public MainWindow() { InitializeComponent(); Comparison.ItemsSource = _rows; ProfileList.ItemsSource = _profiles; _settings = LoadSettings(); UpdateSettingsText(); Status.Text = "选择左右端点后点击“比较”。"; Loaded += async (_, _) => await LoadProfilesAsync(); }
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        try { await BuildPlanAsync(); }
        catch (Exception ex) { SyncButton.IsEnabled = false; Status.Text = ex.Message; }
    }
    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _left is null || _right is null) return;
        ProgressWindow? progressDialog = null;
        try { var operations = _rows.Select(x => x.Operation).ToList(); var current = new SyncPlan(operations); if (!current.CanExecute) { Status.Text = "请先选择操作并裁决所有冲突。"; return; } SyncButton.IsEnabled = false; var total = operations.Count(x => x.Selected && x.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft); Status.Text = $"正在以 {_settings.MaxConcurrentCopies} 路并发同步…"; progressDialog = new ProgressWindow(total, !_settings.ShowCompleted) { Owner = this }; progressDialog.Show(); await new EndpointExecutor().ExecuteAsync(current, _left, _right, new Progress<string>(p => progressDialog.Report(p)), versioning: _settings.Versioning); if (SelectedMode == SyncMode.TwoWay && _left is LocalEndpoint localLeft && _right is LocalEndpoint localRight) await new BaselineStore().CommitAsync(localLeft, localRight); Status.Text = "同步完成。"; progressDialog.Complete(true, "所有选中的操作已完成。"); }
        catch (Exception ex) { Status.Text = "同步失败：" + ex.Message; progressDialog?.Complete(false, ex.Message); }
        finally { RefreshSummary(); }
    }
    private void KeepLeft_Click(object sender, RoutedEventArgs e) => ResolveSelected(true); private void KeepRight_Click(object sender, RoutedEventArgs e) => ResolveSelected(false);
    private void ResolveSelected(bool left) { if (Comparison.SelectedItem is not ComparisonRow row || !row.Operation.IsConflict) { Status.Text = "请选择一个未裁决的冲突行。"; return; } try { row.Operation.Resolve(left); row.Refresh(); Comparison.Items.Refresh(); RefreshSummary(); } catch (Exception ex) { Status.Text = ex.Message; } }
    private void RefreshSummary() { var selected = _rows.Count(x => x.Selected); Summary.Text = $"左侧 {_rows.Count(x => x.Left is not null)} 项  ·  右侧 {_rows.Count(x => x.Right is not null)} 项  ·  { _rows.Count } 个差异/提示"; SelectedLabel.Text = selected.ToString(); SyncButton.IsEnabled = _plan is not null && new SyncPlan(_rows.Select(x => x.Operation).ToList()).CanExecute; }
    private void BrowseLeft_Click(object s, RoutedEventArgs e) => Browse(LeftPath); private void BrowseRight_Click(object s, RoutedEventArgs e) => Browse(RightPath);
    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        (LeftPath.Text, RightPath.Text) = (RightPath.Text, LeftPath.Text);
        if (_plan is not null) await RecompareAsync();
    }
    private static void Browse(System.Windows.Controls.TextBox target) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog() == true) target.Text = dialog.FolderName; }
    private void Comparison_CellEditEnding(object s, System.Windows.Controls.DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(RefreshSummary);
    private void UpdateSettingsText() { if (ConcurrencyLabel is not null) ConcurrencyLabel.Text = _settings.MaxConcurrentCopies + " 路"; }
    private void SyncMode_Changed(object s, SelectionChangedEventArgs e) { if (SyncModeCaption is not null) SyncModeCaption.Text = ModeTitle(); if (_plan is not null) Compare_Click(this, new RoutedEventArgs()); }
    private string ModeTitle() => SelectedMode switch { SyncMode.Mirror => "镜像 →", SyncMode.Update => "更新 →", SyncMode.Custom => "自定义 →", _ => "双向 ↔" };
    private void SaveSettings_Click(object s, RoutedEventArgs e) { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings)); Status.Text = "设置已保存。"; }
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FengSync", "FengSync.local.json");
    private static AppSettings LoadSettings() { try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new() : new(); } catch { return new(); } }
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
    private void LoadProfile_Click(object s, RoutedEventArgs e) { if (ProfileList.SelectedItem is SyncProfile profile) ApplyProfile(profile); else Status.Text = "请先选择一个配置档案。"; }
    private async void SaveProfile_Click(object s, RoutedEventArgs e)
    {
        var old = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create("未命名配置", "", "");
        var current = old with { LeftPath = LeftPath.Text, RightPath = RightPath.Text, Mode = SelectedMode, MaxConcurrentCopies = _settings.MaxConcurrentCopies, VerifyCopies = _settings.VerifyCopies, Filter = _settings.Filter, Versioning = _settings.Versioning };
        var index = _profiles.IndexOf(old); if (index >= 0) _profiles[index] = current; else _profiles.Add(current); await _profileStore.SaveAsync(_profiles); ProfileList.SelectedItem = current; Status.Text = "配置档案已保存（凭据不会写入档案）。";
    }
    private async void RunProfile_Click(object s, RoutedEventArgs e)
    {
        var profiles = ProfileList.SelectedItems.Cast<SyncProfile>().ToList();
        if (profiles.Count == 0) { Status.Text = "请先选择一个或多个 Profile。"; return; }
        if (profiles.Any(profile => string.IsNullOrWhiteSpace(profile.LeftPath) || string.IsNullOrWhiteSpace(profile.RightPath))) { Status.Text = "批处理中的每个 Profile 都必须有两个端点。"; return; }
        try { Status.Text = $"正在并发执行 {profiles.Count} 个 Profile…"; var results = await Task.WhenAll(profiles.Select(RunBatchProfileAsync)); Status.Text = $"批处理完成：{profiles.Count} 个 Profile，计划 {results.Sum(x => x.Planned)} 项，执行 {results.Sum(x => x.Executed)} 项。"; }
        catch (Exception ex) { Status.Text = "批处理失败：" + ex.Message; }
    }
    private void ProfileList_SelectionChanged(object s, SelectionChangedEventArgs e) { if (ProfileList.SelectedItem is SyncProfile profile) { _settings.LastSelectedProfileId = profile.Id; ApplyProfile(profile); } }
    private void ApplyProfile(SyncProfile profile)
    {
        LeftPath.Text = profile.LeftPath; RightPath.Text = profile.RightPath; SyncModeBox.SelectedIndex = (int)profile.Mode; _settings.MaxConcurrentCopies = profile.MaxConcurrentCopies; _settings.VerifyCopies = profile.VerifyCopies; _settings.Filter = profile.Filter ?? SyncFilter.Empty; _settings.Versioning = profile.Versioning ?? new(); UpdateSettingsText(); Status.Text = $"已载入配置：{profile.Name}";
    }
    private async Task BuildPlanAsync()
    {
        (_left, _right) = await CreateEndpointsAsync(LeftPath.Text, RightPath.Text); var scans = await Task.WhenAll(_left.ScanAsync(), _right.ScanAsync()); var left = scans[0].ToDictionary(x => x.Path); var right = scans[1].ToDictionary(x => x.Path); var baseline = SelectedMode == SyncMode.TwoWay && _left is LocalEndpoint localLeft && _right is LocalEndpoint localRight ? await new BaselineStore().LoadAsync(localLeft, localRight) : null; _plan = new ModePlanner().Build(SelectedMode, left.Values, right.Values, baseline, _settings.Filter); _rows.Clear(); foreach (var op in _plan.Operations) _rows.Add(new(op, left.GetValueOrDefault(op.Path), right.GetValueOrDefault(op.Path))); RefreshSummary(); Status.Text = $"{ModeTitle()} 比较完成。勾选要执行的差异，并裁决所有冲突。";
    }
    private async Task<(IEndpoint Left, IEndpoint Right)> CreateEndpointsAsync(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) throw new InvalidOperationException("请先填写两个端点。");
        await DisposeRcloneAsync();
        var needsRemote = IsCloud(left) || IsCloud(right);
        if (needsRemote) _rclone = await RcloneDaemon.StartAsync("rclone", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rclone", "rclone.conf"));
        return (CreateEndpoint(left), CreateEndpoint(right));
    }
    private async Task<ProfileRunResult> RunBatchProfileAsync(SyncProfile profile)
    {
        if (!IsCloud(profile.LeftPath) && !IsCloud(profile.RightPath)) return await new ProfileRunner().RunAsync(profile);
        await using var daemon = await RcloneDaemon.StartAsync("rclone", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rclone", "rclone.conf"));
        var left = CreateEndpoint(profile.LeftPath, daemon); var right = CreateEndpoint(profile.RightPath, daemon);
        var scans = await Task.WhenAll(left.ScanAsync(), right.ScanAsync());
        var plan = new ModePlanner().Build(profile.Mode, scans[0], scans[1], null, profile.Filter);
        if (!plan.CanExecute && plan.Operations.Any()) throw new InvalidOperationException($"{profile.Name} 遇到未裁决冲突。");
        var selected = plan.Operations.Count(x => x.Selected);
        if (selected > 0) await new EndpointExecutor().ExecuteAsync(plan, left, right, versioning: profile.Versioning);
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
        var remote = new TextBox { Text = "myremote", Margin = new Thickness(0, 4, 0, 8) }; var root = new TextBox { Margin = new Thickness(0, 4, 0, 12) };
        var configure = new Button { Content = "新建 / 管理 rclone 连接…", Margin = new Thickness(0, 0, 0, 12) }; configure.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("cmd.exe", "/k rclone config") { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show("无法启动 rclone config：" + ex.Message, "Feng Sync"); } };
        var ok = new Button { Content = "添加到同步端点", IsDefault = true, MinWidth = 120 }; var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 70, Margin = new Thickness(8, 0, 0, 0) }; var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18), Width = 360 }; panel.Children.Add(new TextBlock { Text = "连接云端端点", FontSize = 18, FontWeight = FontWeights.Bold }); panel.Children.Add(new TextBlock { Text = "先通过 rclone 完成授权；然后填写 rclone remote 名称和同步根目录。凭据不会保存到 Profile。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) }); panel.Children.Add(new TextBlock { Text = "服务" }); panel.Children.Add(type); panel.Children.Add(new TextBlock { Text = "rclone remote 名称" }); panel.Children.Add(remote); panel.Children.Add(new TextBlock { Text = "远程根目录（可留空）" }); panel.Children.Add(root); panel.Children.Add(configure); panel.Children.Add(buttons);
        var dialog = new Window { Title = "添加云端端点", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize };
        ok.Click += (_, _) => { if (string.IsNullOrWhiteSpace(remote.Text)) { MessageBox.Show("请填写 rclone remote 名称。", "Feng Sync"); return; } target.Text = (type.SelectedIndex == 0 ? "gdrive://" : "sftp://") + remote.Text.Trim() + (string.IsNullOrWhiteSpace(root.Text) ? "" : "/" + root.Text.Trim().TrimStart('/')); dialog.DialogResult = true; };
        dialog.ShowDialog();
    }
    private void Options_Click(object s, RoutedEventArgs e)
    {
        var concurrency = new Slider { Minimum = 1, Maximum = 8, Value = _settings.MaxConcurrentCopies, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 220 }; var verify = new CheckBox { Content = "复制后验证文件大小", IsChecked = _settings.VerifyCopies, Margin = new Thickness(0, 8, 0, 0) }; var completed = new CheckBox { Content = "同步完成后保留进度窗口", IsChecked = _settings.ShowCompleted, Margin = new Thickness(0, 4, 0, 14) }; var save = new Button { Content = "保存", IsDefault = true, MinWidth = 85 }; var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 85, Margin = new Thickness(8, 0, 0, 0) }; var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(save); buttons.Children.Add(cancel); var panel = new StackPanel { Margin = new Thickness(18), Width = 320 }; panel.Children.Add(new TextBlock { Text = "程序选项", FontSize = 18, FontWeight = FontWeights.Bold }); panel.Children.Add(new TextBlock { Text = "最大并发传输数", Margin = new Thickness(0, 14, 0, 0) }); panel.Children.Add(concurrency); panel.Children.Add(verify); panel.Children.Add(completed); panel.Children.Add(buttons); var dialog = new Window { Title = "选项", Owner = this, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize }; save.Click += (_, _) => { _settings.MaxConcurrentCopies = (int)Math.Round(concurrency.Value); _settings.VerifyCopies = verify.IsChecked == true; _settings.ShowCompleted = completed.IsChecked == true; SaveSettings_Click(this, new RoutedEventArgs()); UpdateSettingsText(); dialog.DialogResult = true; }; dialog.ShowDialog();
    }
    private async void OpenProfileFile_Click(object s, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "Feng Sync files (*.fengsync.json;*.fengsync.batch.json)|*.fengsync.json;*.fengsync.batch.json|JSON files (*.json)|*.json" }; if (dialog.ShowDialog() != true) return; var raw = await File.ReadAllTextAsync(dialog.FileName); BatchJob? batch = null; try { if (JsonDocument.Parse(raw).RootElement.ValueKind == JsonValueKind.Object) batch = JsonSerializer.Deserialize<BatchJob>(raw); } catch (JsonException) { } var loaded = batch?.Profiles?.Count > 0 ? batch.Profiles : await new ProfileStore(dialog.FileName).LoadAsync(); if (loaded.Count == 0) { Status.Text = "该文件没有可打开的 Profile。"; return; } foreach (var profile in loaded.Where(x => _profiles.All(existing => existing.Id != x.Id))) _profiles.Add(profile); ProfileList.SelectedItem = loaded[0]; await PersistProfilesAsync(); Status.Text = batch is null ? $"已打开 {loaded.Count} 个 Profile。" : $"已打开批处理作业“{batch.Name}”（{loaded.Count} 个 Profile）。"; }
    private async void ExportProfile_Click(object s, RoutedEventArgs e) { await SaveProfileToListAsync(); var profile = ProfileList.SelectedItem as SyncProfile; if (profile is null) return; var dialog = new SaveFileDialog { Filter = "Feng Sync Profile (*.fengsync.json)|*.fengsync.json", FileName = profile.Name + ".fengsync.json" }; if (dialog.ShowDialog() != true) return; await new ProfileStore(dialog.FileName).SaveAsync([profile]); Status.Text = "Profile 已保存为文件。"; }
    private async void SaveBatchJob_Click(object s, RoutedEventArgs e) { await SaveProfileToListAsync(); var profiles = ProfileList.SelectedItems.Cast<SyncProfile>().ToList(); if (profiles.Count == 0 && ProfileList.SelectedItem is SyncProfile profile) profiles.Add(profile); if (profiles.Count == 0) return; var dialog = new SaveFileDialog { Filter = "Feng Sync Batch Job (*.fengsync.batch.json)|*.fengsync.batch.json", FileName = "batch.fengsync.batch.json" }; if (dialog.ShowDialog() != true) return; var job = new BatchJob(Path.GetFileNameWithoutExtension(dialog.FileName), profiles); await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(job)); Status.Text = $"批处理作业已保存（{profiles.Count} 个 Profile）。"; }
    private async void ShowLog_Click(object s, RoutedEventArgs e)
    {
        var jobs = await new TaskJournalStore().LoadIncompleteAsync();
        var text = jobs.Count == 0 ? "没有未完成的同步作业日志。" : string.Join(Environment.NewLine + Environment.NewLine, jobs.Select(job => $"作业 {job.JobId}\n开始：{job.CreatedUtc:yyyy-MM-dd HH:mm:ss}\n" + string.Join(Environment.NewLine, job.Items.Select(item => $"{item.State,-10} {item.Kind,-24} {item.Path}"))));
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(14) };
        new Window { Title = "同步日志", Owner = this, Content = box, Width = 680, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
    private async Task SaveProfileToListAsync() { var old = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create("未命名配置", "", ""); var current = old with { LeftPath = LeftPath.Text, RightPath = RightPath.Text, Mode = SelectedMode, MaxConcurrentCopies = _settings.MaxConcurrentCopies, VerifyCopies = _settings.VerifyCopies, Filter = _settings.Filter, Versioning = _settings.Versioning }; var index = _profiles.IndexOf(old); if (index >= 0) _profiles[index] = current; else _profiles.Add(current); ProfileList.SelectedItem = current; await PersistProfilesAsync(); }
    private Task PersistProfilesAsync() => _profileStore.SaveAsync(_profiles);
    private void Exit_Click(object s, RoutedEventArgs e) => Close();
    private void About_Click(object s, RoutedEventArgs e) => MessageBox.Show("Feng Sync\n本地、SFTP 与 Google Drive 的文件比较和同步。", "关于 Feng Sync");
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { _settings.LastSelectedProfileId = (ProfileList.SelectedItem as SyncProfile)?.Id; Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings)); PersistProfilesAsync().GetAwaiter().GetResult(); base.OnClosing(e); }
    protected override async void OnClosed(EventArgs e) { await DisposeRcloneAsync(); base.OnClosed(e); }
    private sealed record BatchJob(string Name, IReadOnlyList<SyncProfile> Profiles);
    private sealed class AppSettings { public int MaxConcurrentCopies { get; set; } = 3; public bool VerifyCopies { get; set; } = true; public bool ShowCompleted { get; set; } = true; public string? LastSelectedProfileId { get; set; } public SyncFilter? Filter { get; set; } = SyncFilter.Empty; public VersioningPolicy? Versioning { get; set; } = new(); }
}
public sealed class ComparisonRow
{
    public ComparisonRow(SyncOperation operation, EntrySnapshot? left, EntrySnapshot? right) { Operation = operation; Left = left; Right = right; Refresh(); }
    public SyncOperation Operation { get; } public EntrySnapshot? Left { get; } public EntrySnapshot? Right { get; } public bool Selected { get => Operation.Selected; set => Operation.Selected = value; } public string LeftDisplay { get; private set; } = ""; public string RightDisplay { get; private set; } = ""; public string LeftSize { get; private set; } = ""; public string RightSize { get; private set; } = ""; public string ActionDisplay { get; private set; } = ""; public Brush ActionBrush { get; private set; } = Brushes.DimGray; public string Reason => Operation.Reason;
    public void Refresh() { LeftDisplay = Describe(Left); RightDisplay = Describe(Right); LeftSize = Size(Left); RightSize = Size(Right); (ActionDisplay, ActionBrush) = Operation.IsConflict ? ("⚠", Brushes.DarkOrange) : Operation.Kind switch { OperationKind.CopyLeftToRight => ("✚→", Brushes.ForestGreen), OperationKind.CopyRightToLeft => ("←✚", Brushes.ForestGreen), OperationKind.DeleteLeft => ("←✖", Brushes.Firebrick), OperationKind.DeleteRight => ("✖→", Brushes.Firebrick), OperationKind.CreateLeftDirectory => ("←✚", Brushes.ForestGreen), OperationKind.CreateRightDirectory => ("✚→", Brushes.ForestGreen), OperationKind.Blocked => ("⛔", Brushes.Firebrick), _ => ("=", Brushes.DimGray) }; }
    private static string Describe(EntrySnapshot? e) => e is null ? "" : e.Kind == EntryKind.Directory ? "▰ " + e.Path : "▱ " + e.Path;
    private static string Size(EntrySnapshot? e) => e?.Fingerprint is null ? "" : e.Fingerprint.Size.ToString("N0");
}
