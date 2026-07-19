using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FengSync.Core;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// "云端端点管理" — a single place to list, create, re-login and delete cloud endpoints, and to browse a
/// remote directory before adding it to the left or right sync endpoint. Creating an endpoint delegates to
/// <see cref="CloudEndpointEditorWindow"/>. The dialog reports its outcome via <see cref="ResultUri"/> /
/// <see cref="ResultSide"/> which <see cref="MainWindow"/> applies to the left/right path boxes.
/// </summary>
public partial class CloudEndpointManagerWindow : Window
{
    private readonly ObservableCollection<RcloneAccount> _accounts = [];

    /// <summary>Feng Sync endpoint URI chosen by the user, or null if nothing was added.</summary>
    public string? ResultUri { get; private set; }
    /// <summary>"Left" or "Right" — which sync endpoint the URI should be applied to.</summary>
    public string? ResultSide { get; private set; }

    public CloudEndpointManagerWindow()
    {
        InitializeComponent();
        EndpointList.ItemsSource = _accounts;
        EndpointSelector.ItemsSource = _accounts;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private RcloneAccount? Selected => EndpointSelector.SelectedItem as RcloneAccount ?? EndpointList.SelectedItem as RcloneAccount;

    private async Task RefreshAsync()
    {
        try
        {
            var previous = Selected?.Name;
            var accounts = await CloudEndpointService.LoadAccountsAsync();
            _accounts.Clear();
            foreach (var account in accounts) _accounts.Add(account);
            var restore = previous is null ? 0 : Math.Max(0, _accounts.ToList().FindIndex(x => x.Name == previous));
            if (_accounts.Count > 0) { EndpointList.SelectedIndex = restore; EndpointSelector.SelectedIndex = restore; }
            StatusText.Text = _accounts.Count == 0 ? "尚无云端端点。点击“新建端点”创建一个。" : $"共 {_accounts.Count} 个云端端点。";
        }
        catch (Exception ex) { StatusText.Text = "无法读取云端端点：" + ex.Message; }
    }

    private void EndpointList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndpointList.SelectedItem is RcloneAccount account && !ReferenceEquals(EndpointSelector.SelectedItem, account))
            EndpointSelector.SelectedItem = account;
    }

    private void EndpointSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateResolvedUri();

    private void UpdateResolvedUri()
    {
        if (Selected is not RcloneAccount account) { ResolvedUriText.Text = ""; return; }
        var kind = CloudEndpointService.KindFromRcloneType(account.Type);
        ResolvedUriText.Text = "将添加：" + CloudEndpointService.BuildUri(kind, account.Name, RemotePathBox.Text);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        var editor = new CloudEndpointEditorWindow { Owner = this };
        if (editor.ShowDialog() == true && editor.SavedRemoteName is not null)
        {
            await RefreshAsync();
            var index = _accounts.ToList().FindIndex(x => x.Name == editor.SavedRemoteName);
            if (index >= 0) { EndpointList.SelectedIndex = index; EndpointSelector.SelectedIndex = index; }
            RemotePathBox.Text = editor.SavedRoot ?? "";
            UpdateResolvedUri();
            StatusText.Text = $"已创建端点：{editor.SavedRemoteName}。";
        }
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not RcloneAccount account) { StatusText.Text = "请先选择一个端点。"; return; }
        try
        {
            StatusText.Text = "正在重新登录，请在浏览器完成授权…";
            await CloudEndpointService.ReconnectAsync(account.Name);
            await RefreshAsync();
            StatusText.Text = $"“{account.Name}”已重新登录。";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(ex.Message, "重新登录失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not RcloneAccount account) { StatusText.Text = "请先选择要删除的端点。"; return; }
        if (MessageBox.Show($"删除云端端点“{account.Name}”？本地 Profile 不会被删除。", "删除端点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await CloudEndpointService.DeleteAsync(account.Name); await RefreshAsync(); StatusText.Text = $"已删除端点：{account.Name}。"; }
        catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void BrowseRemote_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not RcloneAccount account) { StatusText.Text = "请先选择一个端点。"; return; }
        try
        {
            var picked = await PickRemoteDirectoryAsync(account.Name, RemotePathBox.Text);
            RemotePathBox.Text = picked;
            UpdateResolvedUri();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(ex.Message, "读取远程目录失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void AddLeft_Click(object sender, RoutedEventArgs e) => AddToSide("Left");
    private void AddRight_Click(object sender, RoutedEventArgs e) => AddToSide("Right");

    private void AddToSide(string side)
    {
        if (Selected is not RcloneAccount account) { StatusText.Text = "请先选择一个端点。"; return; }
        var kind = CloudEndpointService.KindFromRcloneType(account.Type);
        if (kind == CloudEndpointService.ProviderKind.S3)
        {
            MessageBox.Show("S3 端点尚未接入左右同步端点，敬请期待。", "Feng Sync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ResultUri = CloudEndpointService.BuildUri(kind, account.Name, RemotePathBox.Text);
        ResultSide = side;
        DialogResult = true;
    }

    // --- Remote directory picker: a modal TreeView with lazy expansion (same approach as MainWindow). ---
    private async Task<string> PickRemoteDirectoryAsync(string remote, string currentPath)
    {
        await using var daemon = await RcloneDaemon.StartAsync(BundledRclone.ExecutablePath, BundledRclone.ConfigPath);
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
            item.Items.Add(new TreeViewItem { Header = "正在加载…", Tag = null });
            return item;
        }

        var rootItem = Create("", ""); rootItem.Items.Clear();
        foreach (var directory in directories.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim('/')).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            rootItem.Items.Add(Create(directory, directory));
        tree.Items.Add(rootItem); rootItem.IsSelected = true; rootItem.IsExpanded = true;

        var use = new Button { Content = "选择此文件夹", IsDefault = true, MinWidth = 110 };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), MinWidth = 70 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) };
        buttons.Children.Add(use); buttons.Children.Add(cancel);
        var layout = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons); layout.Children.Add(tree);
        var picker = new Window { Title = $"选择 {remote}: 中的文件夹", Owner = this, Content = layout, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.CanResize };
        string? selected = currentPath.Trim('/');
        tree.SelectedItemChanged += (_, _) => selected = (tree.SelectedItem as TreeViewItem)?.Tag as string ?? selected;
        use.Click += (_, _) => picker.DialogResult = true;
        return picker.ShowDialog() == true ? selected ?? "" : currentPath;
    }
}
