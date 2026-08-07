using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FengSync.Core;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// Focused modal opened by the ☁ buttons next to each sync-endpoint path box. The side (Left / Right)
/// is decided by the caller, so this window only needs to surface an endpoint + remote path and a
/// single "添加" button. It deliberately hides all management affordances (new / delete / re-login,
/// mount management) — those live in <see cref="RemoteEndpointManagerWindow"/>.
/// </summary>
public partial class RemoteEndpointPickerWindow : Window
{
    private readonly string _side;
    private readonly ObservableCollection<RcloneAccount> _accounts = [];
    private bool _busy;

    /// <summary>The fully-built Feng Sync endpoint URI the caller should write into the path box.</summary>
    public string? ResultUri { get; private set; }

    public RemoteEndpointPickerWindow(string side, string? initialRemotePath = null)
    {
        InitializeComponent();
        _side = string.IsNullOrEmpty(side) ? "Left" : side;
        var sideLabel = _side == "Right" ? "右侧" : "左侧";
        HeaderText.Text = $"添加云端端点 → {sideLabel}";
        SubHeaderText.Text = $"从已保存的端点中选择，再挑选远端目录，将以 {_side} 端点加入 Profile。";
        EndpointList.ItemsSource = _accounts;
        if (!string.IsNullOrEmpty(initialRemotePath)) RemotePathBox.Text = initialRemotePath;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private RcloneAccount? Selected => EndpointList.SelectedItem as RcloneAccount;

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            StatusText.Text = "正在读取云端端点列表…";
            var previous = Selected?.Name;
            var accounts = await CloudEndpointService.LoadAccountsAsync();
            _accounts.Clear();
            foreach (var account in accounts) _accounts.Add(account);
            var restore = previous is null ? 0 : Math.Max(0, _accounts.ToList().FindIndex(x => x.Name == previous));
            if (_accounts.Count > 0) EndpointList.SelectedIndex = restore;
            StatusText.Text = _accounts.Count == 0 ? "尚无云端端点；请通过工具 → 远程端点管理 创建一个。" : $"共 {_accounts.Count} 个云端端点。";
        }
        catch (Exception ex) { StatusText.Text = "无法读取云端端点：" + RcloneUiError.Describe(ex, "remote-picker-list"); }
        finally { _busy = false; UpdateResolved(); }
    }

    private void EndpointList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateResolved();
    private void RemotePathBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateResolved();

    private void UpdateResolved()
    {
        if (Selected is not RcloneAccount account) { ResolvedUriText.Text = ""; AddButton.IsEnabled = false; return; }
        var kind = CloudEndpointService.KindFromRcloneType(account.Type);
        ResolvedUriText.Text = "将添加：" + CloudEndpointService.BuildUri(kind, account.Name, RemotePathBox.Text);
        AddButton.IsEnabled = true;
    }

    private async void BrowseRemote_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not RcloneAccount account) { StatusText.Text = "请先选择一个端点。"; return; }
        if (_busy) return;
        _busy = true;
        AddButton.IsEnabled = false;
        StatusText.Text = "正在连接云端并读取根目录…";
        var loading = new Window
        {
            Title = "云端目录",
            Owner = this,
            Content = new TextBlock { Text = "正在连接云端并读取根目录…", Margin = new Thickness(22), MinWidth = 280, TextWrapping = TextWrapping.Wrap },
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        loading.Show();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        try
        {
            var picked = await PickRemoteDirectoryAsync(account.Name, RemotePathBox.Text);
            RemotePathBox.Text = picked;
            UpdateResolved();
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法浏览远端目录：" + RcloneUiError.Describe(ex, "remote-picker-browse");
        }
        finally { _busy = false; loading.Close(); UpdateResolved(); }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not RcloneAccount account) { StatusText.Text = "请先选择一个端点。"; return; }
        var kind = CloudEndpointService.KindFromRcloneType(account.Type);
        ResultUri = CloudEndpointService.BuildUri(kind, account.Name, RemotePathBox.Text);
        DialogResult = true;
    }

    // --- Remote directory picker: a modal TreeView with lazy expansion (same approach as MainWindow). ---
    private async Task<string> PickRemoteDirectoryAsync(string remote, string currentPath)
    {
        var client = await App.CurrentApp.RcloneHost.GetClientAsync();
        var filesystem = remote.EndsWith(':') ? remote : remote + ":";
        var directories = await client.ListDirectoriesAsync(filesystem, "", false);
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
                    var children = await client.ListDirectoriesAsync(filesystem, path, false);
                    foreach (var child in children.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => RemoteDirectoryTree.RelativeToListingRoot(x, path)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        var childName = child.Contains('/') ? child[(child.LastIndexOf('/') + 1)..] : child;
                        var childPath = string.IsNullOrEmpty(path) ? child : path.TrimEnd('/') + "/" + child;
                        item.Items.Add(Create(childName, childPath));
                    }
                }
                catch (Exception ex) { item.Items.Add(new TreeViewItem { Header = "无法读取子目录：" + RcloneUiError.Describe(ex, "remote-picker-expand"), IsEnabled = false }); }
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
