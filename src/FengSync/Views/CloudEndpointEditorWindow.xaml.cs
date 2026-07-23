using System.Windows;
using System.Windows.Controls;
using FengSync.Core;
using FengSync.Services;
using Kind = FengSync.Services.CloudEndpointService.ProviderKind;

namespace FengSync.Views;

/// <summary>
/// Modal "新建端点" dialog: pick a service (Google Drive / SFTP / S3), fill a provider-specific form,
/// test the connection, preview the remote as a directory tree, and save it as an rclone remote.
/// Mirrors the structure of <see cref="SftpServerSettingsWindow"/> / <see cref="ProfileEditorWindow"/>.
/// </summary>
public partial class CloudEndpointEditorWindow : Window
{
    // Set once a remote has been successfully created so Test/Preview/Save don't recreate (or re-OAuth) it.
    private string? _configuredRemote;
    private Kind _configuredKind;

    /// <summary>The rclone remote name that was created; null if the dialog was cancelled.</summary>
    public string? SavedRemoteName { get; private set; }
    public Kind SavedKind { get; private set; }
    public string? SavedRoot { get; private set; }
    /// <summary>Feng Sync endpoint URI for the saved remote, or null if cancelled.</summary>
    public string? SavedUri => SavedRemoteName is null ? null : CloudEndpointService.BuildUri(SavedKind, SavedRemoteName, SavedRoot);

    public CloudEndpointEditorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Service_Changed(this, null!);
    }

    private Kind SelectedKind => ServiceBox.SelectedIndex switch { 1 => Kind.Sftp, 2 => Kind.S3, _ => Kind.GoogleDrive };

    private void Service_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GoogleDrivePanel is null) return; // still initializing
        GoogleDrivePanel.Visibility = SelectedKind == Kind.GoogleDrive ? Visibility.Visible : Visibility.Collapsed;
        SftpPanel.Visibility = SelectedKind == Kind.Sftp ? Visibility.Visible : Visibility.Collapsed;
        S3Panel.Visibility = SelectedKind == Kind.S3 ? Visibility.Visible : Visibility.Collapsed;
        // The provider changed, so any previously created remote no longer matches the visible form.
        _configuredRemote = null;
        PreviewTree.Items.Clear();
    }

    private string DisplayName => SelectedKind switch { Kind.Sftp => SftpNameBox.Text, Kind.S3 => S3NameBox.Text, _ => GDriveNameBox.Text };

    private string RootPath => (SelectedKind switch { Kind.Sftp => SftpRootBox.Text, Kind.S3 => S3RootBox.Text, _ => GDriveRootBox.Text } ?? "").Trim();

    /// <summary>Validates and gathers the rclone <c>config create</c> key/value pairs for the visible provider.</summary>
    private IReadOnlyDictionary<string, string> CollectFields()
    {
        switch (SelectedKind)
        {
            case Kind.GoogleDrive:
                return new Dictionary<string, string>
                {
                    ["client_id"] = GDriveClientIdBox.Text.Trim(),
                    ["client_secret"] = GDriveClientSecretBox.Text.Trim(),
                    ["scope"] = "drive"
                };
            case Kind.Sftp:
                if (string.IsNullOrWhiteSpace(SftpHostBox.Text) || string.IsNullOrWhiteSpace(SftpUserBox.Text))
                    throw new InvalidOperationException("SFTP 必须填写主机和用户名。");
                if (!int.TryParse(SftpPortBox.Text, out var port) || port is < 1 or > 65535)
                    throw new InvalidOperationException("SFTP 端口必须介于 1 和 65535 之间。");
                if (string.IsNullOrWhiteSpace(SftpPasswordBox.Password) && string.IsNullOrWhiteSpace(SftpKeyFileBox.Text))
                    throw new InvalidOperationException("SFTP 必须填写密码或私钥文件。");
                return new Dictionary<string, string>
                {
                    ["host"] = SftpHostBox.Text.Trim(),
                    ["port"] = port.ToString(),
                    ["user"] = SftpUserBox.Text.Trim(),
                    ["pass"] = SftpPasswordBox.Password,
                    ["key_file"] = SftpKeyFileBox.Text.Trim()
                };
            case Kind.S3:
                if (string.IsNullOrWhiteSpace(S3AccessKeyBox.Text) || string.IsNullOrWhiteSpace(S3SecretBox.Password))
                    throw new InvalidOperationException("S3 必须填写 Access Key 与 Secret。");
                return new Dictionary<string, string>
                {
                    ["provider"] = S3ProviderBox.Text.Trim(),
                    ["access_key_id"] = S3AccessKeyBox.Text.Trim(),
                    ["secret_access_key"] = S3SecretBox.Password,
                    ["region"] = S3RegionBox.Text.Trim(),
                    ["endpoint"] = S3EndpointBox.Text.Trim()
                };
            default:
                throw new InvalidOperationException("未知服务类型。");
        }
    }

    /// <summary>Creates the rclone remote once per set of form values; returns the remote name.</summary>
    private async Task<string> EnsureRemoteAsync()
    {
        if (_configuredRemote is not null && _configuredKind == SelectedKind) return _configuredRemote;
        var fields = CollectFields();
        var remote = CloudEndpointService.SanitizeRemoteName(DisplayName);
        await CloudEndpointService.CreateRemoteAsync(SelectedKind, remote, fields);
        _configuredRemote = remote;
        _configuredKind = SelectedKind;
        return remote;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(TestButton, SelectedKind == Kind.GoogleDrive ? "正在等待浏览器授权…" : "正在验证连接…", async () =>
        {
            var remote = await EnsureRemoteAsync();
            await CloudEndpointService.VerifyAsync(remote);
            StatusText.Text = "连接验证成功。";
        });
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(PreviewButton, "正在读取远程目录…", async () =>
        {
            var remote = await EnsureRemoteAsync();
            await LoadPreviewAsync(remote);
            StatusText.Text = "已加载目录树（展开节点可继续浏览）。";
        });
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(SaveButton, "正在保存端点…", async () =>
        {
            var remote = await EnsureRemoteAsync();
            SavedRemoteName = remote;
            SavedKind = SelectedKind;
            SavedRoot = RootPath;
            DialogResult = true;
        });
    }

    /// <summary>Runs an rclone-backed action with a busy label and unified error reporting.</summary>
    private async Task RunBusyAsync(Button button, string busyText, Func<Task> action)
    {
        var original = button.Content;
        try { button.IsEnabled = false; button.Content = busyText; StatusText.Text = busyText; await action(); }
        catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(ex.Message, "Feng Sync", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { button.IsEnabled = true; button.Content = original; }
    }

    // --- Directory-tree preview (lazy expansion mirrors MainWindow's remote picker) ---
    private async Task LoadPreviewAsync(string remote)
    {
        PreviewTree.Items.Clear();
        await using var daemon = await RcloneDaemon.StartAsync(BundledRclone.ExecutablePath, BundledRclone.ConfigPath);
        var filesystem = remote.EndsWith(':') ? remote : remote + ":";
        var directories = await daemon.Client.ListDirectoriesAsync(filesystem, "", false);
        var rootItem = new TreeViewItem { Header = "/（根目录）", Tag = "" };
        foreach (var directory in directories.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim('/')).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            rootItem.Items.Add(CreateNode(filesystem, directory[(directory.LastIndexOf('/') + 1)..], directory));
        PreviewTree.Items.Add(rootItem);
        rootItem.IsExpanded = true;
    }

    private TreeViewItem CreateNode(string filesystem, string name, string path)
    {
        var item = new TreeViewItem { Header = "📁 " + name, Tag = path };
        item.Items.Add(new TreeViewItem { Header = "正在加载…", Tag = null }); // expand affordance, replaced on first open
        item.Expanded += async (_, _) =>
        {
            if (item.Items.Count != 1 || (item.Items[0] as TreeViewItem)?.Tag is not null) return;
            item.Items.Clear();
            // A fresh daemon per expansion keeps this handler self-contained; listings are small and cheap.
            try
            {
                await using var daemon = await RcloneDaemon.StartAsync(BundledRclone.ExecutablePath, BundledRclone.ConfigPath);
                var children = await daemon.Client.ListDirectoriesAsync(filesystem, path, false);
                foreach (var child in children.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => RemoteDirectoryTree.RelativeToListingRoot(x, path)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var childName = child.Contains('/') ? child[(child.LastIndexOf('/') + 1)..] : child;
                    var childPath = string.IsNullOrEmpty(path) ? child : path.TrimEnd('/') + "/" + child;
                    item.Items.Add(CreateNode(filesystem, childName, childPath));
                }
                if (item.Items.Count == 0) item.Items.Add(new TreeViewItem { Header = "（空目录）", IsEnabled = false });
            }
            catch (Exception ex) { item.Items.Add(new TreeViewItem { Header = "无法读取子目录：" + ex.Message, IsEnabled = false }); }
        };
        return item;
    }
}
