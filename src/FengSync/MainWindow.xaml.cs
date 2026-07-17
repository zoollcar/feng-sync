using FengSync.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Media;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FengSync;
public partial class MainWindow : Window
{
    private readonly ObservableCollection<ComparisonRow> _rows = []; private readonly ObservableCollection<SyncProfile> _profiles = []; private readonly ProfileStore _profileStore = new(); private SyncPlan? _plan; private IEndpoint? _left, _right; private RcloneDaemon? _rclone; private AppSettings _settings; private CancellationTokenSource? _syncCancellation; private bool _closing;
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
        try { Comparison.CommitEdit(DataGridEditingUnit.Cell, true); Comparison.CommitEdit(DataGridEditingUnit.Row, true); var operations = _rows.Select(x => x.Operation).ToList(); var current = new SyncPlan(operations); if (!current.CanExecute) { Status.Text = "请先选择操作并裁决所有冲突。"; return; } SyncButton.IsEnabled = false; _syncCancellation = new(); var total = operations.Count(x => x.Selected && x.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft); Status.Text = $"正在以 {_settings.MaxConcurrentCopies} 路并发同步…"; progressDialog = new ProgressWindow(total, !_settings.ShowCompleted) { Owner = this }; progressDialog.Show(); await new EndpointExecutor().ExecuteAsync(current, _left, _right, new Progress<string>(p => progressDialog.Report(p)), _syncCancellation.Token, _settings.Versioning, _settings.MaxConcurrentCopies); if (SelectedMode == SyncMode.TwoWay && _left is LocalEndpoint localLeft && _right is LocalEndpoint localRight) await new BaselineStore().CommitAsync(localLeft, localRight); Status.Text = "同步完成。"; progressDialog.Complete(true, "所有选中的操作已完成。"); }
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
    private async void DeleteProfile_Click(object s, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not SyncProfile profile) { Status.Text = "请选择要移除的配置。"; return; }
        if (MessageBox.Show($"从 Feng Sync 的配置列表移除“{profile.Name}”？\n已导出的配置文件不会被删除。", "移除配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _profiles.Remove(profile);
        if (_profiles.Count == 0) _profiles.Add(SyncProfile.Create("未命名配置", "", ""));
        ProfileList.SelectedIndex = 0; await PersistProfilesAsync(); Status.Text = "配置已从本项目移除。";
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
        if (needsRemote) _rclone = await RcloneDaemon.StartAsync(BundledRclone.ExecutablePath, BundledRclone.ConfigPath);
        return (CreateEndpoint(left), CreateEndpoint(right));
    }
    private async Task<ProfileRunResult> RunBatchProfileAsync(SyncProfile profile)
    {
        if (!IsCloud(profile.LeftPath) && !IsCloud(profile.RightPath)) return await new ProfileRunner().RunAsync(profile);
        await using var daemon = await RcloneDaemon.StartAsync(BundledRclone.ExecutablePath, BundledRclone.ConfigPath);
        var left = CreateEndpoint(profile.LeftPath, daemon); var right = CreateEndpoint(profile.RightPath, daemon);
        var scans = await Task.WhenAll(left.ScanAsync(), right.ScanAsync());
        var plan = new ModePlanner().Build(profile.Mode, scans[0], scans[1], null, profile.Filter);
        if (!plan.CanExecute && plan.Operations.Any()) throw new InvalidOperationException($"{profile.Name} 遇到未裁决冲突。");
        var selected = plan.Operations.Count(x => x.Selected);
        if (selected > 0) await new EndpointExecutor().ExecuteAsync(plan, left, right, versioning: profile.Versioning, maxConcurrentCopies: profile.MaxConcurrentCopies);
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
        var name = new TextBox { Text = "云端连接", Margin = new Thickness(0, 4, 0, 8) }; var root = new TextBox { Margin = new Thickness(0, 4, 0, 8) };
        var host = new TextBox { Margin = new Thickness(0, 4, 0, 8) }; var port = new TextBox { Text = "22", Margin = new Thickness(0, 4, 0, 8) }; var user = new TextBox { Margin = new Thickness(0, 4, 0, 8) }; var password = new PasswordBox { Margin = new Thickness(0, 4, 0, 12) };
        var sftpFields = new StackPanel(); sftpFields.Children.Add(new TextBlock { Text = "主机" }); sftpFields.Children.Add(host); sftpFields.Children.Add(new TextBlock { Text = "端口" }); sftpFields.Children.Add(port); sftpFields.Children.Add(new TextBlock { Text = "用户名" }); sftpFields.Children.Add(user); sftpFields.Children.Add(new TextBlock { Text = "密码" }); sftpFields.Children.Add(password); sftpFields.Visibility = Visibility.Collapsed;
        type.SelectionChanged += (_, _) => sftpFields.Visibility = type.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        var configure = new Button { Content = "连接并验证", Margin = new Thickness(0, 0, 0, 12) };
        var ok = new Button { Content = "添加到同步端点", IsDefault = true, MinWidth = 120 }; var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 70, Margin = new Thickness(8, 0, 0, 0) }; var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var accountButtons = new StackPanel { Orientation = Orientation.Horizontal }; accountButtons.Children.Add(refreshAccounts); accountButtons.Children.Add(reconnect); accountButtons.Children.Add(removeAccount);
        var panel = new StackPanel { Margin = new Thickness(18), Width = 360 }; panel.Children.Add(new TextBlock { Text = "连接云端端点", FontSize = 18, FontWeight = FontWeights.Bold }); panel.Children.Add(new TextBlock { Text = "已保存的云端账号（Google Drive 当前由 rclone 不提供邮箱时显示连接 ID）", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 2) }); panel.Children.Add(accounts); panel.Children.Add(accountButtons); panel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 8) }); panel.Children.Add(new TextBlock { Text = "新建：Google Drive 会在默认浏览器完成授权；SFTP 使用下方填写的连接信息。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }); panel.Children.Add(new TextBlock { Text = "服务" }); panel.Children.Add(type); panel.Children.Add(new TextBlock { Text = "显示名称" }); panel.Children.Add(name); panel.Children.Add(sftpFields); panel.Children.Add(new TextBlock { Text = "远程根目录（可留空）" }); panel.Children.Add(root); panel.Children.Add(configure); panel.Children.Add(buttons);
        var dialog = new Window { Title = "添加云端端点", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize };
        var remoteId = "fengsync_" + Guid.NewGuid().ToString("N"); var configured = false;
        async Task RefreshAccountsAsync() { accounts.ItemsSource = await LoadCloudAccountsAsync(); }
        refreshAccounts.Click += async (_, _) => await RefreshAccountsAsync();
        reconnect.Click += async (_, _) => { if (accounts.SelectedItem is not CloudAccount account) return; try { await RunRcloneAsync("config", "reconnect", account.Name + ":", "--config", BundledRclone.ConfigPath); await RefreshAccountsAsync(); } catch (Exception ex) { MessageBox.Show(ex.Message, "重新登录失败", MessageBoxButton.OK, MessageBoxImage.Error); } };
        removeAccount.Click += async (_, _) => { if (accounts.SelectedItem is not CloudAccount account || MessageBox.Show($"清除云端账号“{account.Name}”？本地 Profile 不会被删除。", "清除账号", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; try { await RunRcloneAsync("config", "delete", account.Name, "--config", BundledRclone.ConfigPath); await RefreshAccountsAsync(); } catch (Exception ex) { MessageBox.Show(ex.Message, "清除失败", MessageBoxButton.OK, MessageBoxImage.Error); } };
        configure.Click += async (_, _) => { try { configure.IsEnabled = false; configure.Content = type.SelectedIndex == 0 ? "正在等待浏览器授权…" : "正在验证连接…"; await ConfigureCloudAsync(remoteId, type.SelectedIndex == 0, host.Text, port.Text, user.Text, password.Password); configured = true; configure.Content = "连接已验证"; await RefreshAccountsAsync(); } catch (Exception ex) { configure.Content = "连接并验证"; MessageBox.Show(ex.Message, "Feng Sync", MessageBoxButton.OK, MessageBoxImage.Error); } finally { configure.IsEnabled = true; } };
        ok.Click += (_, _) => { var account = accounts.SelectedItem as CloudAccount; if (account is null && !configured) { MessageBox.Show("请先连接并验证新账号，或从已保存账号列表选择一个。", "Feng Sync"); return; } var selectedId = account?.Name ?? remoteId; var isGoogle = account?.IsGoogleDrive ?? type.SelectedIndex == 0; target.Text = (isGoogle ? "gdrive://" : "sftp://") + selectedId + (string.IsNullOrWhiteSpace(root.Text) ? "" : "/" + root.Text.Trim().TrimStart('/')); dialog.DialogResult = true; };
        dialog.Loaded += async (_, _) => await RefreshAccountsAsync(); dialog.ShowDialog();
    }
    private sealed record CloudAccount(string Name, string Type) { public bool IsGoogleDrive => Type.Equals("drive", StringComparison.OrdinalIgnoreCase); public string Display => $"{(IsGoogleDrive ? "Google Drive" : "SFTP")}  ·  {Name}"; }
    private static async Task<IReadOnlyList<CloudAccount>> LoadCloudAccountsAsync()
    {
        if (!File.Exists(BundledRclone.ConfigPath)) return [];
        var json = await RunRcloneAsync("config", "show", "--json", "--config", BundledRclone.ConfigPath); using var doc = JsonDocument.Parse(json); if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
        return doc.RootElement.EnumerateObject().Select(x => new CloudAccount(x.Name, x.Value.TryGetProperty("type", out var type) ? type.GetString() ?? "unknown" : "unknown")).Where(x => x.Type is "drive" or "sftp").OrderBy(x => x.Display).ToList();
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
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_syncCancellation is not null && MessageBox.Show("同步正在运行。是否取消同步并退出？", "退出 Feng Sync", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _syncCancellation?.Cancel(); _closing = true; CloseWhenReadyAsync();
    }
    private async void CloseWhenReadyAsync()
    {
        try { _settings.LastSelectedProfileId = (ProfileList.SelectedItem as SyncProfile)?.Id; Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(_settings)); await PersistProfilesAsync(); await DisposeRcloneAsync(); }
        catch { /* Shutdown must not strand the window if a settings file is unavailable. */ }
        finally { Close(); }
    }
    protected override void OnClosed(EventArgs e) { base.OnClosed(e); }
    private sealed record BatchJob(string Name, IReadOnlyList<SyncProfile> Profiles);
    private sealed class AppSettings { public int MaxConcurrentCopies { get; set; } = 3; public bool VerifyCopies { get; set; } = true; public bool ShowCompleted { get; set; } = true; public string? LastSelectedProfileId { get; set; } public SyncFilter? Filter { get; set; } = SyncFilter.Empty; public VersioningPolicy? Versioning { get; set; } = new(); }
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
