using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FengSync.Core;
using FengSync.Core.Mount;
using FengSync.Services;

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
    private readonly ObservableCollection<RcloneAccount> _accounts = [];
    private readonly ObservableCollection<MountInfo> _mounts = [];
    private readonly RcloneMountService _mountService = new();
    private bool _busy;
    private bool _mountBusy;

    public RemoteEndpointManagerWindow()
    {
        InitializeComponent();
        EndpointList.ItemsSource = _accounts;
        MountsList.ItemsSource = _mounts;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private RcloneAccount? Selected => EndpointList.SelectedItem as RcloneAccount;
    private MountInfo? SelectedMount => MountsList.SelectedItem as MountInfo;

    private async Task RefreshAsync()
    {
        try
        {
            StatusText.Text = "正在读取云端端点列表…";
            var previous = Selected?.Name;
            var accounts = await CloudEndpointService.LoadAccountsAsync();
            _accounts.Clear();
            foreach (var account in accounts) _accounts.Add(account);
            var restore = previous is null ? 0 : Math.Max(0, _accounts.ToList().FindIndex(x => x.Name == previous));
            if (_accounts.Count > 0) EndpointList.SelectedIndex = restore;
            StatusText.Text = _accounts.Count == 0 ? "尚无云端端点。点击“新建端点”创建一个。" : $"共 {_accounts.Count} 个云端端点。";
        }
        catch (Exception ex) { StatusText.Text = "无法读取云端端点：" + ex.Message; }
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
        catch (Exception ex) { MountStatus.Text = "扫描挂载失败：" + ex.Message; }
        UpdateMountButtons();
    }

    private void UpdateMountButtons()
    {
        MountSelectedEndpointButton.IsEnabled = Selected is not null && !_mountBusy;
        UnmountMountButton.IsEnabled = SelectedMount is not null && !_mountBusy;
        RefreshMountsButton.IsEnabled = !_mountBusy;
    }

    private void EndpointList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMountButtons();
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
        if (Selected is not RcloneAccount account) { StatusText.Text = "请先选择一个端点。"; return; }
        await RunBusyAsync(ReconnectButton, "正在登录…", "正在重新登录，请在浏览器完成授权…", async () =>
        {
            await CloudEndpointService.ReconnectAsync(account.Name);
            await RefreshAsync();
            StatusText.Text = $"“{account.Name}”已重新登录。";
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not RcloneAccount account) { StatusText.Text = "请先选择要删除的端点。"; return; }
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
        catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(ex.Message, "远程端点", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { button.IsEnabled = true; button.Content = original; _busy = false; }
    }

    private async void RefreshMounts_Click(object sender, RoutedEventArgs e)
    {
        if (_mountBusy) return;
        _mountBusy = true;
        UpdateMountButtons();
        try { await RefreshMountsAsync(); }
        catch (Exception ex) { MountStatus.Text = "刷新挂载失败：" + ex.Message; }
        finally { _mountBusy = false; UpdateMountButtons(); }
    }

    private async void MountSelectedEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not RcloneAccount account) { MountStatus.Text = "请先选择要挂载的端点。"; return; }
        if (_mountBusy) return;
        var provider = CloudEndpointService.DisplayName(CloudEndpointService.KindFromRcloneType(account.Type));
        var dialog = new MountPickerDialog(account.Name, provider, _mounts.Select(m => m.MountPoint).ToList()) { Owner = this };
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
            MountStatus.Text = "挂载失败：" + ex.Message;
            MessageBox.Show(ex.Message, "挂载", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _mountBusy = false; UpdateMountButtons(); }
    }

    private async void UnmountMount_Click(object sender, RoutedEventArgs e)
    {
        var mount = SelectedMount;
        if (mount is null) { MountStatus.Text = "请先选择要取消的挂载。"; return; }
        if (_mountBusy) return;
        if (mount.Origin == MountOrigin.Unreadable)
        {
            MessageBox.Show("该挂载的命令行无法读取，可能由更高权限的进程启动。请用任务管理器手动结束对应 rclone 进程。", "取消挂载", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var prompt = mount.Origin == MountOrigin.External
            ? $"该挂载不是由本应用启动的。\n\n{mount.Display}\n\n仍要停止并卸载吗？"
            : $"确定取消挂载？\n\n{mount.Display}";
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
            MountStatus.Text = "取消挂载失败：" + ex.Message;
            MessageBox.Show(ex.Message, "取消挂载", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _mountBusy = false; UpdateMountButtons(); }
    }
}