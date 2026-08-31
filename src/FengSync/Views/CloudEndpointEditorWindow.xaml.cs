using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FengSync.Core;
using FengSync.Core.Rclone.Configuration;
using FengSync.Services;
using Kind = FengSync.Services.CloudEndpointService.ProviderKind;

namespace FengSync.Views;

public partial class CloudEndpointEditorWindow : Window
{
    private readonly S3EndpointDraft _s3Draft = new();
    private IReadOnlyList<RcloneS3Provider> _providers = [];
    private IReadOnlyList<string> _probeDirectories = [];
    private bool _busy, _secretSync, _initialized;
    private string? _configuredRemote;
    private Kind _configuredKind;

    public string? SavedRemoteName { get; private set; }
    public Kind SavedKind { get; private set; }
    public string? SavedRoot { get; private set; }
    public string? SavedUri => SavedRemoteName is null ? null : CloudEndpointService.BuildUri(SavedKind, SavedRemoteName, SavedRoot);

    public CloudEndpointEditorWindow()
    {
        InitializeComponent();
        _initialized = true;
        S3ProviderBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(S3ProviderText_Changed));
        S3RegionBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(S3RegionText_Changed));
        Loaded += async (_, _) => await LoadProvidersAsync();
    }

    private Kind SelectedKind => ServiceBox.SelectedIndex switch { 1 => Kind.Sftp, 2 => Kind.S3, _ => Kind.GoogleDrive };
    private string ProviderName => S3ProviderBox.Text.Trim();
    private string Secret => S3SecretVisibleBox.Visibility == Visibility.Visible ? S3SecretVisibleBox.Text : S3SecretBox.Password;
    private string RootPath => SelectedKind switch { Kind.Sftp => SftpRootBox.Text.Trim(), Kind.S3 => CurrentS3Values().RootPath, _ => GDriveRootBox.Text.Trim() };

    private async Task LoadProvidersAsync()
    {
        try
        {
            _providers = await CloudEndpointService.LoadS3ProvidersAsync();
            S3ProviderBox.ItemsSource = _providers.Select(provider => provider.Name);
            S3ProviderBox.Text = _providers.FirstOrDefault()?.Name ?? "";
            UpdateProviderHelp();
        }
        catch (Exception ex)
        {
            ProviderDescription.Text = "无法读取 Provider：" + RcloneUiError.Describe(ex, "s3-provider-list");
            NextButton.IsEnabled = SelectedKind != Kind.S3;
        }
    }

    private void Service_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderChoicePanel is null) return;
        ProviderChoicePanel.Visibility = SelectedKind == Kind.S3 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = SelectedKind != Kind.S3 || _providers.Count > 0;
    }

    private void S3Provider_SelectionChanged(object sender, SelectionChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        UpdateProviderHelp();
        if (ProviderName.Equals("Cloudflare", StringComparison.OrdinalIgnoreCase)) S3RegionBox.Text = "auto";
        else if (string.IsNullOrWhiteSpace(S3RegionBox.Text) || S3RegionBox.Text == "auto") S3RegionBox.Text = "us-east-1";
        MarkS3Dirty();
    });
    private void S3ProviderText_Changed(object sender, TextChangedEventArgs e) => Dispatcher.BeginInvoke(() => { UpdateProviderHelp(); MarkS3Dirty(); });
    private void S3RegionText_Changed(object sender, TextChangedEventArgs e) => MarkS3Dirty();

    private void UpdateProviderHelp()
    {
        var provider = _providers.FirstOrDefault(value => value.Name.Equals(ProviderName, StringComparison.Ordinal));
        ProviderDescription.Text = provider?.Description ?? "从列表中选择 rclone 支持的 Provider。";
        S3RegionBox.ItemsSource = provider?.RegionSuggestions;
        RegionHint.Text = provider?.RegionSuggestions.Count > 0 ? "可选择建议值，也可输入自定义 Region。" : "可输入服务商要求的 Region。";
        S3Heading.Text = string.IsNullOrWhiteSpace(ProviderName) ? "S3 连接" : $"{ProviderName} S3 连接";
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedKind == Kind.S3 && !_providers.Any(provider => provider.Name.Equals(ProviderName, StringComparison.Ordinal)))
        { ProviderError.Text = "请选择列表中的 S3 Provider。"; S3ProviderBox.Focus(); return; }
        ProviderError.Text = "";
        ChooseServiceStep.Visibility = Visibility.Collapsed; ConnectionStep.Visibility = Visibility.Visible;
        GoogleDrivePanel.Visibility = SelectedKind == Kind.GoogleDrive ? Visibility.Visible : Visibility.Collapsed;
        SftpPanel.Visibility = SelectedKind == Kind.Sftp ? Visibility.Visible : Visibility.Collapsed;
        S3Panel.Visibility = SelectedKind == Kind.S3 ? Visibility.Visible : Visibility.Collapsed;
        StepCaption.Text = "第 2 步，共 2 步 · 配置连接";
        StepOneIndicator.Background = (Brush)FindResource("BorderSubtleBrush"); StepTwoIndicator.Background = (Brush)FindResource("AccentBrush");
        NextButton.Visibility = Visibility.Collapsed; BackButton.Visibility = TestButton.Visibility = SaveButton.Visibility = Visibility.Visible;
        if (SelectedKind == Kind.S3) MarkS3Dirty();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ChooseServiceStep.Visibility = Visibility.Visible; ConnectionStep.Visibility = Visibility.Collapsed;
        StepCaption.Text = "第 1 步，共 2 步 · 选择服务";
        StepOneIndicator.Background = (Brush)FindResource("AccentBrush"); StepTwoIndicator.Background = (Brush)FindResource("BorderSubtleBrush");
        NextButton.Visibility = Visibility.Visible; BackButton.Visibility = TestButton.Visibility = SaveButton.Visibility = Visibility.Collapsed;
    }

    private S3EndpointValues CurrentS3Values() => new(S3NameBox.Text.Trim(), ProviderName, S3AccessKeyBox.Text.Trim(), Secret,
        S3RegionBox.Text.Trim(), S3EndpointBox.Text.Trim(), S3BucketBox.Text.Trim(), S3SubdirectoryBox.Text.Trim());
    private void MarkS3Dirty() { if (!_initialized) return; _s3Draft.Update(CurrentS3Values()); ProbeResultCard.Visibility = Visibility.Collapsed; StatusText.Text = ""; }
    private void S3Field_Changed(object sender, TextChangedEventArgs e) => MarkS3Dirty();
    private void S3Region_Changed(object sender, SelectionChangedEventArgs e) => Dispatcher.BeginInvoke(MarkS3Dirty);
    private void S3Secret_Changed(object sender, RoutedEventArgs e) { if (!_secretSync) MarkS3Dirty(); }
    private void S3SecretVisible_Changed(object sender, TextChangedEventArgs e) { if (!_secretSync) MarkS3Dirty(); }

    private void ToggleSecret_Click(object sender, RoutedEventArgs e)
    {
        _secretSync = true;
        if (S3SecretVisibleBox.Visibility == Visibility.Visible)
        { S3SecretBox.Password = S3SecretVisibleBox.Text; S3SecretVisibleBox.Visibility = Visibility.Collapsed; S3SecretBox.Visibility = Visibility.Visible; S3SecretBox.Focus(); }
        else
        { S3SecretVisibleBox.Text = S3SecretBox.Password; S3SecretBox.Visibility = Visibility.Collapsed; S3SecretVisibleBox.Visibility = Visibility.Visible; S3SecretVisibleBox.Focus(); }
        _secretSync = false;
    }

    private bool ValidateS3(bool focus)
    {
        MarkS3Dirty();
        var errors = CloudEndpointService.ValidateS3Settings(CurrentS3Values(), _providers.Select(provider => provider.Name).ToList());
        DisplayNameError.Text = Error("displayName"); ProviderError.Text = Error("provider"); AccessKeyError.Text = Error("accessKey"); SecretError.Text = Error("secret");
        EndpointError.Text = Error("endpoint"); BucketError.Text = Error("bucket"); SubdirectoryError.Text = Error("subdirectory");
        if (focus && errors.Count > 0)
        {
            Control control = errors.Keys.First() switch { "displayName" => S3NameBox, "provider" => S3ProviderBox, "accessKey" => S3AccessKeyBox, "secret" => S3SecretBox, "endpoint" => S3EndpointBox, "bucket" => S3BucketBox, _ => S3SubdirectoryBox };
            control.Focus();
        }
        return errors.Count == 0;
        string Error(string key) => errors.TryGetValue(key, out var value) ? value : "";
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedKind == Kind.S3)
        {
            if (!ValidateS3(true)) return;
            await RunBusyAsync(TestButton, "正在读取指定目录…", async () =>
            {
                try
                {
                    var result = await CloudEndpointService.ProbeS3Async(CurrentS3Values()); _probeDirectories = result.Directories; _s3Draft.RecordProbe(true);
                    ProbeResultText.Text = $"读取成功：/{CurrentS3Values().RootPath}。未验证上传权限。"; ProbeResultCard.Visibility = Visibility.Visible; BrowseProbeButton.IsEnabled = true;
                }
                catch { _s3Draft.RecordProbe(false); throw; }
            });
            return;
        }
        await RunBusyAsync(TestButton, SelectedKind == Kind.GoogleDrive ? "正在等待浏览器授权…" : "正在验证连接…", async () => { var remote = await EnsureRemoteAsync(); await CloudEndpointService.VerifyAsync(remote, RootPath); StatusText.Text = "连接验证成功。"; });
    }

    private void BrowseProbe_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListBox { MinWidth = 420, MinHeight = 260, Margin = new Thickness(12) };
        if (_probeDirectories.Count == 0) list.Items.Add("（当前目录没有子目录）"); else foreach (var directory in _probeDirectories) list.Items.Add("📁 " + directory);
        new Window { Title = "测试读取结果", Owner = this, Content = list, Width = 480, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedKind == Kind.S3)
        {
            if (!ValidateS3(true)) return;
            if (!_s3Draft.HasCurrentSuccessfulTest && MessageBox.Show("当前配置尚未通过读取测试。保存后可能无法浏览、上传或同步。\n\n仍要保存吗？", "保存未验证的 S3 端点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var remote = CloudEndpointService.SanitizeRemoteName(S3NameBox.Text);
            var exists = (await CloudEndpointService.LoadAccountsAsync()).Any(account => account.Name.Equals(remote, StringComparison.OrdinalIgnoreCase));
            var replace = false;
            if (exists)
            {
                var answer = MessageBox.Show($"端点“{remote}”已存在。\n\n是：替换现有配置；否：返回修改显示名称。", "端点名称重复", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) { S3NameBox.Focus(); return; } replace = true;
            }
            await RunBusyAsync(SaveButton, "正在保存…", async () => { await CloudEndpointService.SaveS3Async(remote, CurrentS3Values(), replace); SavedRemoteName = remote; SavedKind = Kind.S3; SavedRoot = CurrentS3Values().RootPath; DialogResult = true; });
            return;
        }
        await RunBusyAsync(SaveButton, "正在保存…", async () => { var remote = await EnsureRemoteAsync(); await CloudEndpointService.SaveMetadataAsync(remote, SelectedKind, RootPath); SavedRemoteName = remote; SavedKind = SelectedKind; SavedRoot = RootPath; DialogResult = true; });
    }

    private async Task<string> EnsureRemoteAsync()
    {
        if (_configuredRemote is not null && _configuredKind == SelectedKind) return _configuredRemote;
        var remote = CloudEndpointService.SanitizeRemoteName(SelectedKind == Kind.Sftp ? SftpNameBox.Text : GDriveNameBox.Text);
        await CloudEndpointService.CreateRemoteAsync(SelectedKind, remote, CollectNonS3Fields()); _configuredRemote = remote; _configuredKind = SelectedKind; return remote;
    }

    private IReadOnlyDictionary<string, string> CollectNonS3Fields()
    {
        if (SelectedKind == Kind.GoogleDrive) return new Dictionary<string, string> { ["client_id"] = GDriveClientIdBox.Text.Trim(), ["client_secret"] = GDriveClientSecretBox.Text.Trim(), ["scope"] = "drive" };
        if (string.IsNullOrWhiteSpace(SftpHostBox.Text) || string.IsNullOrWhiteSpace(SftpUserBox.Text)) throw new InvalidOperationException("SFTP 必须填写主机和用户名。");
        if (!int.TryParse(SftpPortBox.Text, out var port) || port is < 1 or > 65535) throw new InvalidOperationException("SFTP 端口必须介于 1 和 65535 之间。");
        if (string.IsNullOrWhiteSpace(SftpPasswordBox.Password) && string.IsNullOrWhiteSpace(SftpKeyFileBox.Text)) throw new InvalidOperationException("SFTP 必须填写密码或私钥文件。");
        return new Dictionary<string, string> { ["host"] = SftpHostBox.Text.Trim(), ["port"] = port.ToString(), ["user"] = SftpUserBox.Text.Trim(), ["pass"] = SftpPasswordBox.Password, ["key_file"] = SftpKeyFileBox.Text.Trim() };
    }

    private async Task RunBusyAsync(Button button, string busyText, Func<Task> action)
    {
        if (_busy) return; _busy = true; var original = button.Content;
        try { BackButton.IsEnabled = TestButton.IsEnabled = SaveButton.IsEnabled = false; button.Content = busyText; StatusText.Text = busyText; await action(); }
        catch (Exception ex) { var message = RcloneUiError.Describe(ex, "cloud-endpoint-editor"); StatusText.Text = message; MessageBox.Show(message, "Feng Sync", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { BackButton.IsEnabled = TestButton.IsEnabled = SaveButton.IsEnabled = true; button.Content = original; _busy = false; }
    }
}
