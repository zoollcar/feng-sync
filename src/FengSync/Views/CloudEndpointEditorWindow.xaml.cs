using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FengSync.Core;
using FengSync.Core.Rclone.Configuration;
using FengSync.Services;
using Kind = FengSync.Services.CloudEndpointService.ProviderKind;

namespace FengSync.Views;

/// <summary>
/// Creates a cloud endpoint, or edits one that already exists.
/// <para>
/// The two modes share one form on purpose: the fields, validation and save paths must not drift apart.
/// Edit mode is opened with a <see cref="CloudEndpointEditTarget"/> read from rclone's own configuration,
/// so the form starts on step 2 already populated. Two rules are specific to editing and are not obvious:
/// the endpoint keeps its name and backend (a rename would strand every profile that references it, and
/// switching the backend is a different endpoint, not an edit), and the SFTP password box starts empty
/// because rclone stores it obscured — leaving it empty re-sends the stored value untouched.
/// </para>
/// </summary>
public partial class CloudEndpointEditorWindow : Window
{
    private readonly S3EndpointDraft _s3Draft = new();
    private readonly CloudEndpointEditTarget? _edit;
    private IReadOnlyList<RcloneS3Provider> _providers = [];
    private IReadOnlyList<string> _probeDirectories = [];
    private bool _busy, _secretSync, _initialized;
    private string? _configuredRemote;
    private Kind _configuredKind;

    public string? SavedRemoteName { get; private set; }
    public Kind SavedKind { get; private set; }
    public string? SavedRoot { get; private set; }
    public string? SavedUri => SavedRemoteName is null ? null : CloudEndpointService.BuildUri(SavedKind, SavedRemoteName, SavedRoot);

    public CloudEndpointEditorWindow() : this(null) { }

    /// <param name="edit">
    /// <see langword="null"/> creates a new endpoint; otherwise the form opens on the connection step with
    /// the endpoint's saved parameters filled in, and saving updates that endpoint in place.
    /// </param>
    public CloudEndpointEditorWindow(CloudEndpointEditTarget? edit)
    {
        _edit = edit;
        InitializeComponent();
        _initialized = true;
        S3ProviderBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(S3ProviderText_Changed));
        S3RegionBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(S3RegionText_Changed));
        Loaded += async (_, _) => await LoadProvidersAsync();
        if (edit is not null) EnterEditMode(edit);
    }

    private Kind SelectedKind => ServiceBox.SelectedIndex switch { 1 => Kind.Sftp, 2 => Kind.S3, _ => Kind.GoogleDrive };
    private string ProviderName => S3ProviderBox.Text.Trim();
    private string Secret => S3SecretVisibleBox.Visibility == Visibility.Visible ? S3SecretVisibleBox.Text : S3SecretBox.Password;
    private string RootPath => SelectedKind switch { Kind.Sftp => SftpRootBox.Text.Trim(), Kind.S3 => CurrentS3Values().Prefix, _ => GDriveRootBox.Text.Trim() };

    /// <summary>
    /// The provider an existing endpoint was saved with. It is stored in Feng Sync's metadata rather than
    /// read back from rclone because the two can differ (Backblaze is rclone's generic <c>Other</c>).
    /// </summary>
    private string EditS3Provider => _edit is null ? "" : _edit.Account.Metadata?.Provider is { Length: > 0 } provider ? provider : _edit.Parameter("provider");

    /// <summary>
    /// The SFTP password to write, and whether it is the obscured value already stored in rclone's config.
    /// Only an edit form can produce the latter, and the flag has to reach the write: obscuring an already
    /// obscured password corrupts it.
    /// </summary>
    private (string Value, bool AlreadyObscured) SftpPassword => _edit is null
        ? (SftpPasswordBox.Password, false)
        : CloudEndpointService.ResolveStoredPassword(SftpPasswordBox.Password, _edit.Parameter("pass"));

    /// <summary>
    /// Google Drive credentials only take effect through rclone's OAuth flow, so saving has to re-authorize
    /// when they changed — but an unchanged client must not force a browser round-trip just to move the
    /// endpoint's root folder.
    /// </summary>
    private bool GoogleDriveCredentialsChanged => _edit is not null && SelectedKind == Kind.GoogleDrive
        && (!string.Equals(GDriveClientIdBox.Text.Trim(), _edit.Parameter("client_id"), StringComparison.Ordinal)
            || !string.Equals(GDriveClientSecretBox.Text.Trim(), _edit.Parameter("client_secret"), StringComparison.Ordinal));

    /// <summary>
    /// Puts the form on the connection step, pre-filled, without the service picker: an endpoint's backend
    /// is part of its identity and cannot be changed by editing it.
    /// </summary>
    private void EnterEditMode(CloudEndpointEditTarget edit)
    {
        Title = "编辑云端端点";
        AutomationProperties.SetName(this, "编辑云端端点");
        HeadingText.Text = "编辑云端端点";
        ServiceBox.SelectedIndex = edit.Kind switch { Kind.Sftp => 1, Kind.S3 => 2, _ => 0 };
        ServiceBox.IsEnabled = false;

        StepIndicator.Visibility = Visibility.Collapsed;
        StepCaption.Text = $"编辑端点 · {CloudEndpointService.DisplayName(edit.Kind)} · 名称与类型不可修改";
        ChooseServiceStep.Visibility = Visibility.Collapsed;
        ConnectionStep.Visibility = Visibility.Visible;
        GoogleDrivePanel.Visibility = edit.Kind == Kind.GoogleDrive ? Visibility.Visible : Visibility.Collapsed;
        SftpPanel.Visibility = edit.Kind == Kind.Sftp ? Visibility.Visible : Visibility.Collapsed;
        S3Panel.Visibility = edit.Kind == Kind.S3 ? Visibility.Visible : Visibility.Collapsed;

        NextButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        TestButton.Visibility = SaveButton.Visibility = Visibility.Visible;

        switch (edit.Kind)
        {
            case Kind.GoogleDrive:
                GDriveNameBox.Text = edit.RemoteName; GDriveNameBox.IsReadOnly = true;
                GDriveClientIdBox.Text = edit.Parameter("client_id");
                GDriveClientSecretBox.Text = edit.Parameter("client_secret");
                GDriveRootBox.Text = edit.Account.RootPath;
                break;
            case Kind.Sftp:
                SftpNameBox.Text = edit.RemoteName; SftpNameBox.IsReadOnly = true;
                SftpHostBox.Text = edit.Parameter("host");
                SftpPortBox.Text = edit.Parameter("port");
                SftpUserBox.Text = edit.Parameter("user");
                SftpKeyFileBox.Text = edit.Parameter("key_file");
                SftpRootBox.Text = edit.Account.RootPath;
                // rclone stores the password obscured, so the box cannot be pre-filled with anything
                // readable. Empty therefore means "keep the stored password", and the hint says so.
                SftpPasswordLabel.Text = "密码（留空表示保持原密码不变）";
                break;
            case Kind.S3:
                S3NameBox.Text = edit.RemoteName; S3NameBox.IsReadOnly = true;
                // Set here as well as after the provider list loads, so the saved provider is on screen
                // from the first frame instead of appearing a moment later.
                S3ProviderBox.Text = EditS3Provider;
                S3AccessKeyBox.Text = edit.Parameter("access_key_id");
                S3SecretBox.Password = edit.Parameter("secret_access_key");
                S3RegionBox.Text = edit.Parameter("region");
                S3EndpointBox.Text = edit.Parameter("endpoint");
                S3BucketBox.Text = edit.Account.Metadata?.Bucket ?? "";
                S3SubdirectoryBox.Text = edit.Account.Metadata?.Subdirectory ?? "";
                MarkS3Dirty();
                break;
        }
    }

    private async Task LoadProvidersAsync()
    {
        try
        {
            _providers = await CloudEndpointService.LoadS3ProvidersAsync();
            S3ProviderBox.ItemsSource = _providers.Select(provider => provider.Name);
            S3ProviderBox.Text = _edit is null ? _providers.FirstOrDefault()?.Name ?? "" : EditS3Provider;
            UpdateProviderHelp();
            // UpdateProviderHelp replaces the region's suggestion list, and an editable ComboBox may drop
            // its text when its ItemsSource changes. An edit form therefore restates the region it loaded
            // instead of trusting the control to have kept it: losing it would silently retarget the
            // endpoint at a different region.
            if (_edit is not null && SelectedKind == Kind.S3) S3RegionBox.Text = _edit.Parameter("region");
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
        ApplyProviderDefaults();
        MarkS3Dirty();
    });

    /// <summary>
    /// Seeds Region with a value that matches the selected provider. Feng Sync presets (Backblaze)
    /// deliberately seed nothing: their region is part of the endpoint the user copies from the
    /// service console, so a prefilled AWS region would be actively misleading.
    /// </summary>
    private void ApplyProviderDefaults()
    {
        // An edit form already holds the endpoint's saved region, and seeding a default here would
        // silently overwrite it. Defaults are a new-endpoint convenience.
        if (_edit is not null) return;
        if (ProviderName.Equals("Cloudflare", StringComparison.OrdinalIgnoreCase)) { S3RegionBox.Text = "auto"; return; }
        if (CloudEndpointService.FindPreset(ProviderName) is not null) { S3RegionBox.Text = ""; return; }
        if (string.IsNullOrWhiteSpace(S3RegionBox.Text) || S3RegionBox.Text == "auto") S3RegionBox.Text = "us-east-1";
    }

    private void S3ProviderText_Changed(object sender, TextChangedEventArgs e) => Dispatcher.BeginInvoke(() => { UpdateProviderHelp(); MarkS3Dirty(); });
    private void S3RegionText_Changed(object sender, TextChangedEventArgs e) => MarkS3Dirty();

    private void UpdateProviderHelp()
    {
        var provider = _providers.FirstOrDefault(value => value.Name.Equals(ProviderName, StringComparison.Ordinal));
        ProviderDescription.Text = provider?.Description ?? "从列表中选择 rclone 支持的 Provider。";
        // Assigning ItemsSource makes an editable ComboBox drop its text, because the previously selected
        // item no longer exists in the new list. That would wipe the region an edit form had just loaded,
        // so the list is only replaced when it actually changes.
        var suggestions = provider?.RegionSuggestions;
        if (!ReferenceEquals(S3RegionBox.ItemsSource, suggestions)) S3RegionBox.ItemsSource = suggestions;
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
        S3RegionBox.Text.Trim(), S3EndpointBox.Text.Trim(), S3BucketBox.Text.Trim(), S3SubdirectoryBox.Text.Trim(),
        _providers.FirstOrDefault(provider => provider.Name.Equals(ProviderName, StringComparison.Ordinal))?.RcloneValue);
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
                    var prefix = CurrentS3Values().Prefix;
                    ProbeResultText.Text = string.IsNullOrEmpty(prefix)
                        ? "读取成功：该 Bucket 根目录。端点将只能访问此 Bucket。未验证上传权限。"
                        : $"读取成功：/{prefix}。保存后该前缀由 rclone 强制，端点只能访问此前缀下的内容。未验证上传权限。";
                    ProbeResultCard.Visibility = Visibility.Visible; BrowseProbeButton.IsEnabled = true;
                }
                catch { _s3Draft.RecordProbe(false); throw; }
            });
            return;
        }
        if (_edit is not null)
        {
            // The endpoint already exists, so testing it only has to reach the configured root. For Google
            // Drive that also avoids a browser round-trip the user did not ask for by opening the editor.
            await RunBusyAsync(TestButton, "正在验证连接…", async () => { await CloudEndpointService.VerifyAsync(_edit.RemoteName, RootPath); StatusText.Text = "连接验证成功。"; });
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
            // An existing endpoint is assumed to have been usable already; the probe warning exists to stop
            // a brand-new configuration from being saved untested.
            if (_edit is null && !_s3Draft.HasCurrentSuccessfulTest && MessageBox.Show("当前配置尚未通过读取测试。保存后可能无法浏览、上传或同步。\n\n仍要保存吗？", "保存未验证的 S3 端点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var remote = _edit?.RemoteName ?? CloudEndpointService.SanitizeRemoteName(S3NameBox.Text);
            var replace = _edit is not null;
            if (!replace)
            {
                var exists = (await CloudEndpointService.LoadAccountsAsync()).Any(account => account.Name.Equals(remote, StringComparison.OrdinalIgnoreCase));
                if (exists)
                {
                    var answer = MessageBox.Show($"端点“{remote}”已存在。\n\n是：替换现有配置；否：返回修改显示名称。", "端点名称重复", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) { S3NameBox.Focus(); return; } replace = true;
                }
            }
            await RunBusyAsync(SaveButton, "正在保存…", async () => { await CloudEndpointService.SaveS3Async(remote, CurrentS3Values(), replace); SavedRemoteName = remote; SavedKind = Kind.S3; 
                // The saved endpoint is an rclone alias whose root already is the prefix, so the profile
                // path that addresses the prefix itself is empty.
                SavedRoot = ""; DialogResult = true; });
            return;
        }
        if (_edit is not null)
        {
            // Only the connection parameters live in rclone; an endpoint's default root lives in Feng
            // Sync's metadata, so moving it never has to touch rclone.conf.
            var remote = _edit.RemoteName;
            var reauthorize = GoogleDriveCredentialsChanged;
            await RunBusyAsync(SaveButton, reauthorize ? "正在等待浏览器授权…" : "正在保存…", async () =>
            {
                if (reauthorize) remote = await EnsureRemoteAsync();
                await CloudEndpointService.SaveMetadataAsync(remote, SelectedKind, RootPath);
                SavedRemoteName = remote; SavedKind = SelectedKind; SavedRoot = RootPath; DialogResult = true;
            });
            return;
        }
        await RunBusyAsync(SaveButton, "正在保存…", async () => { var remote = await EnsureRemoteAsync(); await CloudEndpointService.SaveMetadataAsync(remote, SelectedKind, RootPath); SavedRemoteName = remote; SavedKind = SelectedKind; SavedRoot = RootPath; DialogResult = true; });
    }

    private async Task<string> EnsureRemoteAsync()
    {
        if (_configuredRemote is not null && _configuredKind == SelectedKind) return _configuredRemote;
        // An edited endpoint keeps the name it was listed under; renaming would silently break every
        // profile, CLI job and schedule that addresses it.
        var remote = _edit?.RemoteName ?? CloudEndpointService.SanitizeRemoteName(SelectedKind == Kind.Sftp ? SftpNameBox.Text : GDriveNameBox.Text);
        await CloudEndpointService.CreateRemoteAsync(SelectedKind, remote, CollectNonS3Fields(), SftpPassword.AlreadyObscured);
        _configuredRemote = remote; _configuredKind = SelectedKind; return remote;
    }

    private IReadOnlyDictionary<string, string> CollectNonS3Fields()
    {
        if (SelectedKind == Kind.GoogleDrive) return new Dictionary<string, string> { ["client_id"] = GDriveClientIdBox.Text.Trim(), ["client_secret"] = GDriveClientSecretBox.Text.Trim(), ["scope"] = "drive" };
        if (string.IsNullOrWhiteSpace(SftpHostBox.Text) || string.IsNullOrWhiteSpace(SftpUserBox.Text)) throw new InvalidOperationException("SFTP 必须填写主机和用户名。");
        if (!int.TryParse(SftpPortBox.Text, out var port) || port is < 1 or > 65535) throw new InvalidOperationException("SFTP 端口必须介于 1 和 65535 之间。");
        var password = SftpPassword;
        if (string.IsNullOrWhiteSpace(password.Value) && string.IsNullOrWhiteSpace(SftpKeyFileBox.Text)) throw new InvalidOperationException("SFTP 必须填写密码或私钥文件。");
        return new Dictionary<string, string> { ["host"] = SftpHostBox.Text.Trim(), ["port"] = port.ToString(), ["user"] = SftpUserBox.Text.Trim(), ["pass"] = password.Value, ["key_file"] = SftpKeyFileBox.Text.Trim() };
    }

    private async Task RunBusyAsync(Button button, string busyText, Func<Task> action)
    {
        if (_busy) return; _busy = true; var original = button.Content;
        try { BackButton.IsEnabled = TestButton.IsEnabled = SaveButton.IsEnabled = false; button.Content = busyText; StatusText.Text = busyText; await action(); }
        catch (Exception ex) { var message = RcloneUiError.Describe(ex, "cloud-endpoint-editor"); StatusText.Text = message; MessageBox.Show(message, "Feng Sync", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { BackButton.IsEnabled = TestButton.IsEnabled = SaveButton.IsEnabled = true; button.Content = original; _busy = false; }
    }
}
