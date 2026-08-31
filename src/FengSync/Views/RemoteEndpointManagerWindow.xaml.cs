using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FengSync.Core;
using FengSync.Core.Mount;
using FengSync.Services;
using Microsoft.Win32;

namespace FengSync.Views;

/// <summary>
/// "远程端点管理" — opens from the Tools menu and consolidates two admin-style concerns:
/// (1) managing the cloud endpoints stored in rclone.conf (refresh / new / re-login / delete), and
/// (2) managing live <c>rclone mount</c> processes on the host (refresh / start a mount for the
/// selected endpoint / unmount an existing one). The browse-and-pick flow used when adding an
/// endpoint to a sync side lives in <see cref="RemoteEndpointPickerWindow"/> instead.
/// </summary>
public partial class RemoteEndpointManagerWindow : Window
{
    private readonly ObservableCollection<CloudEndpointAccount> _accounts = [];
    private readonly ObservableCollection<MountInfo> _mounts = [];
    private readonly RcloneMountService _mountService = App.CurrentApp.MountService;
    private readonly CloudFileManagerService _files = new();
    private readonly ObservableCollection<CloudFileEntry> _entries = [];
    private string _currentPath = "";
    private bool _busy;
    private bool _mountBusy;

    public RemoteEndpointManagerWindow()
    {
        InitializeComponent();
        EndpointList.ItemsSource = _accounts;
        MountsList.ItemsSource = _mounts;
        FileList.ItemsSource = _entries;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private CloudEndpointAccount? Selected => EndpointList.SelectedItem as CloudEndpointAccount;
    private MountInfo? SelectedMount => MountsList.SelectedItem as MountInfo;

    private async Task RefreshAsync()
    {
        try
        {
            StatusText.Text = "正在读取云端端点列表…";
            var previous = Selected?.Name;
            var accounts = await CloudEndpointService.LoadEndpointAccountsAsync();
            _accounts.Clear();
            foreach (var account in accounts) _accounts.Add(account);
            var restore = previous is null ? 0 : Math.Max(0, _accounts.ToList().FindIndex(x => x.Name == previous));
            if (_accounts.Count > 0) EndpointList.SelectedIndex = restore;
            StatusText.Text = _accounts.Count == 0 ? "尚无云端端点。点击“新建端点”创建一个。" : $"共 {_accounts.Count} 个云端端点。";
        }
        catch (Exception ex) { StatusText.Text = "无法读取云端端点：" + RcloneUiError.Describe(ex, "endpoint-manager-list"); }
        await RefreshMountsAsync();
    }

    private async Task RefreshMountsAsync()
    {
        try
        {
            MountStatus.Text = "正在扫描系统全部 rclone 挂载…";
            var mounts = await _mountService.ScanAsync();
            _mounts.Clear();
            foreach (var mount in mounts) _mounts.Add(mount);
            MountStatus.Text = _mounts.Count == 0 ? "当前系统没有 rclone 挂载。" : $"共发现 {_mounts.Count} 个挂载，其中 {_mounts.Count(m => m.Origin == MountOrigin.FengSyncManaged)} 个由本应用启动。";
        }
        catch (Exception ex) { MountStatus.Text = "扫描挂载失败：" + RcloneUiError.Describe(ex, "mount-scan"); }
        UpdateMountButtons();
    }

    private void UpdateMountButtons()
    {
        MountSelectedEndpointButton.IsEnabled = Selected is not null && !_mountBusy;
        UnmountMountButton.IsEnabled = SelectedMount?.CanUnmount == true && !_mountBusy;
        RefreshMountsButton.IsEnabled = !_mountBusy;
    }

    private async void EndpointList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentPath = Selected?.RootPath ?? "";
        UpdateMountButtons();
        await BrowseAsync();
    }
    private void MountsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMountButtons();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await RunBusyAsync(RefreshButton, "正在刷新…", "正在读取云端端点列表…", RefreshAsync);

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(NewButton, "正在新建…", "正在打开新建端点窗口…", async () =>
        {
            var editor = new CloudEndpointEditorWindow { Owner = this };
            if (editor.ShowDialog() != true || editor.SavedRemoteName is null) return;
            await RefreshAsync();
            var index = _accounts.ToList().FindIndex(x => x.Name == editor.SavedRemoteName);
            if (index >= 0) EndpointList.SelectedIndex = index;
            StatusText.Text = $"已创建端点：{editor.SavedRemoteName}。";
        });
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择一个端点。"; return; }
        if (!account.Remote.IsGoogleDrive) { StatusText.Text = "只有 Google Drive 端点需要浏览器重新登录。"; return; }
        await RunBusyAsync(ReconnectButton, "正在登录…", "正在重新登录，请在浏览器完成授权…", async () =>
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            await CloudEndpointService.ReconnectAsync(account.Name, progress);
            await RefreshAsync();
            StatusText.Text = $"“{account.Name}”已重新登录。";
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择要删除的端点。"; return; }
        var relatedMounts = _mounts.Where(m => string.Equals(m.RemoteName, account.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (relatedMounts.Count > 0)
        {
            var detail = string.Join("\n", relatedMounts.Select(m => " · " + m.Display));
            var confirm = MessageBox.Show($"端点“{account.Name}”仍有 {relatedMounts.Count} 个挂载：\n{detail}\n\n继续删除端点？", "删除端点", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }
        else if (MessageBox.Show($"删除云端端点“{account.Name}”？本地 Profile 不会被删除。", "删除端点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunBusyAsync(DeleteButton, "正在删除…", $"正在删除端点“{account.Name}”…", async () => { await CloudEndpointService.DeleteAsync(account.Name); await RefreshAsync(); StatusText.Text = $"已删除端点：{account.Name}。"; });
    }

    private async Task RunBusyAsync(Button button, string busyText, string status, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        var original = button.Content;
        try { button.IsEnabled = false; button.Content = busyText; StatusText.Text = status; await action(); }
        catch (Exception ex)
        {
            var message = RcloneUiError.Describe(ex, "endpoint-manager-action");
            StatusText.Text = message;
            MessageBox.Show(message, "远程端点", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { button.IsEnabled = true; button.Content = original; _busy = false; }
    }

    private async void RefreshMounts_Click(object sender, RoutedEventArgs e)
    {
        if (_mountBusy) return;
        _mountBusy = true;
        UpdateMountButtons();
        try { await RefreshMountsAsync(); }
        catch (Exception ex) { MountStatus.Text = "刷新挂载失败：" + RcloneUiError.Describe(ex, "mount-refresh"); }
        finally { _mountBusy = false; UpdateMountButtons(); }
    }

    private async void MountSelectedEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not CloudEndpointAccount account) { MountStatus.Text = "请先选择要挂载的端点。"; return; }
        if (_mountBusy) return;
        var provider = CloudEndpointService.DisplayName(CloudEndpointService.KindFromRcloneType(account.Type));
        var dialog = new MountPickerDialog(account.Name, provider, _mounts.Select(m => m.MountPoint).ToList(), account.RootPath) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var target = dialog.SelectedTarget!;
        _mountBusy = true;
        UpdateMountButtons();
        MountStatus.Text = $"正在挂载 {account.Name} → {target.MountPoint}…";
        try
        {
            await _mountService.MountAsync(target);
            await RefreshMountsAsync();
            MountStatus.Text = $"已启动挂载：{target.RemoteName} → {target.MountPoint}。";
        }
        catch (Exception ex)
        {
            var message = RcloneUiError.Describe(ex, "mount-create");
            MountStatus.Text = "挂载失败：" + message;
            MessageBox.Show(message, "挂载", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _mountBusy = false; UpdateMountButtons(); }
    }

    private async void UnmountMount_Click(object sender, RoutedEventArgs e)
    {
        var mount = SelectedMount;
        if (mount is null) { MountStatus.Text = "请先选择要取消的挂载。"; return; }
        if (_mountBusy) return;
        if (!mount.CanUnmount)
        {
            MessageBox.Show("该挂载由外部程序管理。Feng Sync 只读显示外部挂载，不会结束其进程或代为卸载。", "取消挂载", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var prompt = $"确定取消挂载？\n\n{mount.Display}";
        if (MessageBox.Show(prompt, "取消挂载", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _mountBusy = true;
        UpdateMountButtons();
        MountStatus.Text = $"正在取消挂载：{mount.MountPoint}…";
        try
        {
            var result = await _mountService.UnmountAsync(mount);
            if (result.AllStopped) { MountStatus.Text = $"已取消挂载：{mount.MountPoint}。"; }
            else
            {
                var detail = string.Join("\n", result.Failures.Select(f => $" · {f.MountPoint}（PID {f.Pid}）：{f.Reason}"));
                MountStatus.Text = "部分挂载未完成：\n" + detail;
                MessageBox.Show("部分挂载未完成：\n" + detail, "取消挂载", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            await RefreshMountsAsync();
        }
        catch (Exception ex)
        {
            var message = RcloneUiError.Describe(ex, "mount-remove");
            MountStatus.Text = "取消挂载失败：" + message;
            MessageBox.Show(message, "取消挂载", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _mountBusy = false; UpdateMountButtons(); }
    }

    private async Task BrowseAsync()
    {
        if (Selected is not CloudEndpointAccount account) { _entries.Clear(); PathText.Text = "请选择一个云盘"; return; }
        try
        {
            StatusText.Text = "正在读取目录…";
            var items = await _files.ListAsync(account.Name, _currentPath);
            _entries.Clear(); foreach (var item in items) _entries.Add(item);
            PathText.Text = $"{account.Name}:/{_currentPath}";
            BackButton.IsEnabled = !_currentPath.Equals(account.RootPath, StringComparison.OrdinalIgnoreCase);
            StatusText.Text = $"共 {_entries.Count} 项。";
        }
        catch (Exception ex) { StatusText.Text = "无法读取目录：" + RcloneUiError.Describe(ex, "cloud-file-list"); }
    }

    private async void BrowseRefresh_Click(object sender, RoutedEventArgs e) => await BrowseAsync();
    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not CloudEndpointAccount account || _currentPath.Equals(account.RootPath, StringComparison.OrdinalIgnoreCase)) return;
        _currentPath = CloudFileManagerService.Parent(_currentPath);
        if (!_currentPath.StartsWith(account.RootPath, StringComparison.OrdinalIgnoreCase)) _currentPath = account.RootPath;
        await BrowseAsync();
    }

    private async void FileList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not CloudFileEntry entry) return;
        if (entry.IsDirectory) { _currentPath = entry.Path; await BrowseAsync(); }
        else await OpenAsync(entry);
    }

    private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (FileList.SelectedItem is not CloudFileEntry entry || entry.IsDirectory) { e.Handled = true; return; }
        var menu = new ContextMenu();
        var download = new MenuItem { Header = "下载…" }; download.Click += async (_, _) => await DownloadAsync(entry); menu.Items.Add(download);
        var open = new MenuItem { Header = "打开" }; open.Click += async (_, _) => await OpenAsync(entry); menu.Items.Add(open);
        FileList.ContextMenu = menu;
    }

    private async void UploadFiles_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { StatusText.Text = "请先选择云盘。"; return; }
        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames) if (!await UploadOneAsync(path)) break;
        await BrowseAsync();
    }

    private async void UploadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { StatusText.Text = "请先选择云盘。"; return; }
        var dialog = new OpenFolderDialog { Title = "选择要上传的文件夹" };
        if (dialog.ShowDialog(this) != true) return;
        var root = dialog.FolderName; var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        var applyAll = false; var overwriteAll = false;
        foreach (var local in files)
        {
            var relative = Path.GetRelativePath(root, local).Replace('\\', '/');
            var parent = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
            var old = _currentPath; _currentPath = CloudFileManagerService.Join(old, parent);
            var proceed = await UploadOneAsync(local, applyAll ? overwriteAll : null);
            _currentPath = old;
            if (!proceed) break;
            // The first conflict dialog may set a session-wide policy through the status tag.
            if (Tag is bool policy) { applyAll = true; overwriteAll = policy; Tag = null; }
        }
        await BrowseAsync();
    }

    private async Task<bool> UploadOneAsync(string localPath, bool? forceOverwrite = null)
    {
        if (Selected is not CloudEndpointAccount account) return false;
        var exists = (await _files.ListAsync(account.Name, _currentPath)).Any(x => !x.IsDirectory && x.Name.Equals(Path.GetFileName(localPath), StringComparison.OrdinalIgnoreCase));
        var overwrite = forceOverwrite ?? false;
        if (exists && forceOverwrite is null)
        {
            var answer = MessageBox.Show($"“{Path.GetFileName(localPath)}”已存在。\n是：覆盖；否：跳过；取消：取消上传。", "文件已存在", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Cancel) return false;
            overwrite = answer == MessageBoxResult.Yes;
            if (!overwrite) return true;
        }
        try
        {
            TransferProgress.Value = 0; StatusText.Text = "正在上传：" + Path.GetFileName(localPath);
            await _files.UploadAsync(account.Name, _currentPath, localPath, new Progress<CloudTransferProgress>(p =>
            { TransferProgress.Value = p.Percentage; StatusText.Text = $"正在上传：{Path.GetFileName(localPath)}  {p.Percentage:N1}%  {p.CompletedBytes / 1024d / 1024d:N1} MB"; }));
            StatusText.Text = (overwrite ? "已覆盖：" : "已上传：") + Path.GetFileName(localPath); return true;
        }
        catch (Exception ex) { StatusText.Text = "上传失败：" + RcloneUiError.Describe(ex, "cloud-file-upload"); return false; }
    }

    private async Task DownloadAsync(CloudFileEntry entry)
    {
        if (Selected is not CloudEndpointAccount account) return;
        var dialog = new SaveFileDialog { FileName = entry.Name, Title = "下载文件" };
        if (dialog.ShowDialog(this) != true) return;
        await DownloadToAsync(account.Name, entry, dialog.FileName);
    }

    private async Task OpenAsync(CloudFileEntry entry)
    {
        if (Selected is not CloudEndpointAccount account) return;
        var folder = Path.Combine(Path.GetTempPath(), "FengSync", "cloud-open", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(folder, entry.Name);
        if (await DownloadToAsync(account.Name, entry, local)) Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
    }

    private async Task<bool> DownloadToAsync(string remote, CloudFileEntry entry, string local)
    {
        try { TransferProgress.Value = 0; await _files.DownloadAsync(remote, entry.Path, local, new Progress<CloudTransferProgress>(p => TransferProgress.Value = p.Percentage)); StatusText.Text = "已下载：" + entry.Name; return true; }
        catch (Exception ex) { StatusText.Text = "下载失败：" + RcloneUiError.Describe(ex, "cloud-file-download"); return false; }
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        var tasks = new ListBox { Margin = new Thickness(16), MinWidth = 520, MinHeight = 260 };
        tasks.Items.Add(new TextBlock
        {
            Text = "没有可断点续传的上传任务。\n\n上传被取消或连接失败后，可恢复任务会显示在这里。",
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8)
        });
        var close = new Button { Content = "关闭", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 16, 16), HorizontalAlignment = HorizontalAlignment.Right };
        var layout = new DockPanel(); DockPanel.SetDock(close, Dock.Bottom); layout.Children.Add(close); layout.Children.Add(tasks);
        var window = new Window
        {
            Title = "断点续传",
            Owner = this,
            Content = layout,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.CanResize
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }
}
