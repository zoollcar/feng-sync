using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    /// <summary>How long a drag gesture waits for a not-yet-cached file before falling back to reveal.</summary>
    private static readonly TimeSpan DragHydrationTimeout = TimeSpan.FromSeconds(2);

    private readonly ObservableCollection<CloudEndpointAccount> _accounts = [];
    private readonly ObservableCollection<MountInfo> _mounts = [];
    private readonly RcloneMountService _mountService = AppServices.Current.MountService;
    private readonly CloudFileManagerService _files = new();
    private readonly ObservableCollection<CloudFileEntry> _entries = [];
    /// <summary>
    /// Shared so uploads and downloads keep running after this window closes; falls back to a private
    /// queue only when no shell installed its services (a preview or a test).
    /// </summary>
    private readonly CloudTransferQueue _transfers = AppServices.TryCurrent?.CloudTransfers ?? new CloudTransferQueue();
    private Point _dragStart;
    private bool _dragArmed;
    private string _currentPath = "";
    private bool _busy;
    private bool _mountBusy;

    public RemoteEndpointManagerWindow()
    {
        InitializeComponent();
        EndpointList.ItemsSource = _accounts;
        MountsList.ItemsSource = _mounts;
        FileList.ItemsSource = _entries;
        _transfers.PropertyChanged += Transfers_PropertyChanged;
        Closed += (_, _) => _transfers.PropertyChanged -= Transfers_PropertyChanged;
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
        var dialog = new MountPickerDialog(account.Name, provider, _mounts.Select(m => m.MountPoint).ToList(), account.RootPath, account.Prefix) { Owner = this };
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
            PathText.Text = account.PrefixIsEnforced
                ? $"{account.Name}:/{_currentPath}    （前缀 /{account.Prefix}，由 rclone 强制，无法越界）"
                : $"{account.Name}:/{_currentPath}";
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
        await EnqueueUploadsAsync(CloudDropPlanner.Expand(dialog.FileNames, _currentPath));
    }

    private async void UploadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { StatusText.Text = "请先选择云盘。"; return; }
        var dialog = new OpenFolderDialog { Title = "选择要上传的文件夹" };
        if (dialog.ShowDialog(this) != true) return;
        await EnqueueUploadsAsync(CloudDropPlanner.Expand([dialog.FolderName], _currentPath));
    }

    /// <summary>
    /// Queues a batch of uploads, asking once about same-name conflicts so a dropped tree cannot produce
    /// a dialog per file. Overwriting stays an explicit user choice, matching the single-file flow.
    /// </summary>
    private async Task EnqueueUploadsAsync(IReadOnlyList<CloudDropPlanner.UploadItem> items)
    {
        if (Selected is not CloudEndpointAccount account) { StatusText.Text = "请先选择云盘。"; return; }
        if (items.Count == 0) { StatusText.Text = "没有可上传的文件。"; return; }

        // One listing per destination directory: a dropped tree would otherwise list a directory per file.
        var listings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var directory in items.Select(item => CloudTransferQueue.ParentOf(item.RemotePath)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in await _files.ListAsync(account.Name, directory)) if (!entry.IsDirectory) names.Add(entry.Name);
                listings[directory] = names;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法读取目标目录：" + RcloneUiError.Describe(ex, "cloud-upload-conflict");
            return;
        }

        var conflicts = items.Where(item => listings[CloudTransferQueue.ParentOf(item.RemotePath)].Contains(CloudTransferTask.NameOf(item.RemotePath))).ToList();
        var accepted = items;
        if (conflicts.Count > 0)
        {
            var answer = MessageBox.Show($"{conflicts.Count} 个同名文件已存在于目标目录。\n\n是：全部覆盖；否：全部跳过；取消：取消本次上传。", "文件已存在", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Cancel) { StatusText.Text = "已取消上传。"; return; }
            if (answer == MessageBoxResult.No) accepted = items.Where(item => !conflicts.Contains(item)).ToList();
        }
        if (accepted.Count == 0) { StatusText.Text = "已跳过全部同名文件。"; return; }

        var uri = EndpointUri(account);
        foreach (var item in accepted)
            _transfers.Enqueue(CloudTransferDirection.Upload, uri, ToEndpointRelative(account, item.RemotePath), item.LocalPath, new FileInfo(item.LocalPath).Length);

        StatusText.Text = $"已加入传输队列：{accepted.Count} 个文件。";
        ShowTransferWindow();
        await BrowseAsync();
    }

    private Task DownloadAsync(CloudFileEntry entry)
    {
        if (Selected is not CloudEndpointAccount account) return Task.CompletedTask;
        var dialog = new SaveFileDialog { FileName = entry.Name, Title = "下载文件" };
        if (dialog.ShowDialog(this) != true) return Task.CompletedTask;
        _transfers.Enqueue(CloudTransferDirection.Download, EndpointUri(account), ToEndpointRelative(account, entry.Path), dialog.FileName, entry.Size);
        StatusText.Text = "已加入传输队列：" + entry.Name;
        ShowTransferWindow();
        return Task.CompletedTask;
    }

    private async Task OpenAsync(CloudFileEntry entry)
    {
        if (Selected is not CloudEndpointAccount account) return;
        var folder = Path.Combine(Path.GetTempPath(), "FengSync", "cloud-open", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(folder, entry.Name);
        var task = _transfers.Enqueue(CloudTransferDirection.Download, EndpointUri(account), ToEndpointRelative(account, entry.Path), local, entry.Size);
        ShowTransferWindow();
        StatusText.Text = "正在下载以打开：" + entry.Name;
        if (!await _transfers.WaitForAsync(task))
        {
            StatusText.Text = "打开失败：" + (task.Error ?? "传输未完成。");
            return;
        }
        Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        StatusText.Text = "已打开：" + entry.Name;
    }

    private void OpenTransfers_Click(object sender, RoutedEventArgs e) => ShowTransferWindow();

    /// <summary>
    /// Reuses the single transfer window rather than owning a new one: an owned window is closed by WPF
    /// when its owner closes, which would hide progress and cancellation while transfers keep running.
    /// </summary>
    private void ShowTransferWindow()
    {
        var existing = Application.Current?.Windows.OfType<CloudTransferWindow>().FirstOrDefault();
        if (existing is null)
        {
            new CloudTransferWindow(_transfers).Show();
            return;
        }
        if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
        existing.Activate();
    }

    private void Transfers_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CloudTransferQueue.OverallPercentage)) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => TransferProgress.Value = _transfers.OverallPercentage); return; }
        TransferProgress.Value = _transfers.OverallPercentage;
    }

    // ---- Drag in: upload whatever Explorer hands us, preserving dropped folder structure ----

    private void FileBrowser_DragOver(object sender, DragEventArgs e)
    {
        var acceptable = Selected is not null && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!acceptable) { DropHint.Visibility = Visibility.Collapsed; return; }
        DropHintText.Text = $"上传到 {Selected!.Name}:/{_currentPath}";
        DropHint.Visibility = Visibility.Visible;
    }

    private void FileBrowser_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private async void FileBrowser_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (Selected is null) { StatusText.Text = "请先选择云盘。"; return; }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0) return;
        e.Handled = true;
        var items = CloudDropPlanner.Expand(dropped, _currentPath);
        if (items.Count == 0) { StatusText.Text = "拖入的内容中没有可上传的文件。"; return; }
        await EnqueueUploadsAsync(items);
    }

    // ---- Drag out: hand Explorer a real file so the drop lands in the folder the user chose ----

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(FileList);
        _dragArmed = true;
    }

    private async void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        // Only a gesture that began on a row drags a file; a scrollbar or header drag must stay native.
        if (ItemsControl.ContainerFromElement(FileList, e.OriginalSource as DependencyObject) is not ListViewItem) return;
        if (FileList.SelectedItem is not CloudFileEntry entry) return;
        var position = e.GetPosition(FileList);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        await BeginDragOutAsync(entry);
    }

    private async Task BeginDragOutAsync(CloudFileEntry entry)
    {
        if (Selected is not CloudEndpointAccount account) return;
        if (entry.IsDirectory)
        {
            StatusText.Text = "暂不支持直接拖出整个文件夹，请先进入文件夹再拖出其中的文件。";
            return;
        }

        var relative = ToEndpointRelative(account, entry.Path);
        if (relative.Length == 0) return;
        var local = CloudDragCache.LocalPathFor(account.Name, relative);

        CloudTransferTask? task = null;
        if (!CloudDragCache.IsUsable(local, entry.Size))
        {
            task = _transfers.Enqueue(CloudTransferDirection.Download, EndpointUri(account), relative, local, entry.Size);
            ShowTransferWindow();
            // Explorer copies the file at drop time, so the bytes must exist first. A small file often
            // finishes inside the same gesture, which lets the drop land in the target folder for real;
            // a large one falls through to the reveal path instead of stalling the drag.
            await Task.WhenAny(_transfers.WaitForAsync(task), Task.Delay(DragHydrationTimeout));
        }

        if (CloudDragCache.IsUsable(local, entry.Size) && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            StatusText.Text = "正在拖出：" + entry.Name;
            DragDrop.DoDragDrop(FileList, new DataObject(DataFormats.FileDrop, new[] { local }), DragDropEffects.Copy);
            StatusText.Text = "已拖出：" + entry.Name;
            return;
        }

        if (task is not null) RevealWhenReady(task, local);
    }

    /// <summary>
    /// The fallback the user asked for: when the download cannot finish inside the drag gesture, finish it
    /// in the background and then put the user in front of the file so a second drag lands in their folder.
    /// </summary>
    private void RevealWhenReady(CloudTransferTask task, string local)
    {
        _ = RevealAsync(task, local);

        async Task RevealAsync(CloudTransferTask pending, string path)
        {
            if (!await _transfers.WaitForAsync(pending) || !File.Exists(path)) return;
            await Dispatcher.InvokeAsync(() =>
            {
                StatusText.Text = $"“{Path.GetFileName(path)}”已下载完成，把它从打开的文件夹拖到目标位置即可。";
                RevealInExplorer(path);
            });
        }
    }

    private static void RevealInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Unable to reveal a downloaded cloud file: " + ex); }
    }

    /// <summary>
    /// The browser navigates paths relative to the rclone remote root, while <see cref="EndpointFactory"/>
    /// addresses an endpoint relative to its configured root (the account's default root folder).
    /// Transfers go through endpoints, so the root prefix has to be stripped.
    /// </summary>
    private static string ToEndpointRelative(CloudEndpointAccount account, string remotePath)
    {
        var root = account.RootPath.Trim('/');
        var path = (remotePath ?? "").Trim('/');
        if (root.Length == 0) return path;
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return "";
        return path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? path[(root.Length + 1)..] : path;
    }

    private static string EndpointUri(CloudEndpointAccount account) =>
        CloudEndpointService.BuildUri(CloudEndpointService.KindFromRcloneType(account.Type), account.Name, account.RootPath);
}
