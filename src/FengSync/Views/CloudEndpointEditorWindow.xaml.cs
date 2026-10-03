using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FengSync.Core;
using FengSync.Core.Rclone.Configuration;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// Creates a cloud endpoint, or edits one that already exists.
/// <para>
/// The two modes share one form on purpose: the fields, validation and save paths must not drift apart.
/// Edit mode is opened with a <see cref="CloudEndpointEditTarget"/> read from rclone's own configuration,
/// so the form starts on step 2 already populated. Two rules are specific to editing and are not obvious:
/// the endpoint keeps its name and backend (a rename would strand every profile that references it, and
/// switching the backend is a different endpoint, not an edit), and a password box starts empty because
/// rclone stores it obscured — leaving it empty re-sends the stored value untouched.
/// </para>
/// <para>
/// The connection form for every provider except S3 is generated from <see cref="CloudProviderCatalog"/>.
/// S3 keeps a hand-written panel because it needs a provider list, a prefix boundary and a probe that no
/// other backend has, and because its AutomationIds are part of the desktop acceptance scenario.
/// </para>
/// </summary>
public partial class CloudEndpointEditorWindow : Window
{
    private readonly S3EndpointDraft _s3Draft = new();
    private readonly CloudEndpointEditTarget? _edit;
    /// <summary>Set when the form was opened to import an endpoint described by an export file.</summary>
    private readonly CloudEndpointDraft? _draft;
    /// <summary>Input controls of the generated form, keyed by rclone config key.</summary>
    private readonly Dictionary<string, Control> _fieldInputs = new(StringComparer.Ordinal);
    /// <summary>The label rendered above each generated input, keyed by the same rclone config key.</summary>
    private readonly Dictionary<string, TextBlock> _fieldLabels = new(StringComparer.Ordinal);
    private IReadOnlyList<RcloneS3Provider> _providers = [];
    private IReadOnlyList<string> _probeDirectories = [];
    private TextBox? _nameBox, _rootBox;
    private bool _busy, _secretSync, _initialized;
    private string? _configuredRemote;
    private EndpointType _configuredKind;

    public string? SavedRemoteName { get; private set; }
    public EndpointType SavedKind { get; private set; }
    public string? SavedRoot { get; private set; }
    public string? SavedUri => SavedRemoteName is null ? null : CloudEndpointService.BuildUri(SavedKind, SavedRemoteName, SavedRoot);

    public CloudEndpointEditorWindow() : this((CloudEndpointEditTarget?)null, null) { }

    /// <param name="edit">
    /// <see langword="null"/> creates a new endpoint; otherwise the form opens on the connection step with
    /// the endpoint's saved parameters filled in, and saving updates that endpoint in place.
    /// </param>
    public CloudEndpointEditorWindow(CloudEndpointEditTarget? edit) : this(edit, null) { }

    /// <summary>
    /// Opens the form for an endpoint an export file described: the service, the name and the root are
    /// pre-filled, and every credential is left blank for the user to type. Saving creates a new endpoint
    /// through the same path a hand-entered one takes.
    /// </summary>
    public CloudEndpointEditorWindow(CloudEndpointDraft draft) : this(null, draft) { }

    private CloudEndpointEditorWindow(CloudEndpointEditTarget? edit, CloudEndpointDraft? draft)
    {
        _edit = edit;
        _draft = draft;
        InitializeComponent();
        // The service list is data, not markup: it comes from the provider catalog so a new provider needs
        // no change here. Each item carries its descriptor, which keeps the selection and the descriptor
        // impossible to disagree.
        foreach (var descriptor in CloudProviderCatalog.Providers)
            ServiceBox.Items.Add(new ComboBoxItem { Content = descriptor.DisplayName, Tag = descriptor });
        ServiceBox.SelectedIndex = 0;
        _initialized = true;
        S3ProviderBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(S3ProviderText_Changed));
        S3RegionBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(S3RegionText_Changed));
        Loaded += async (_, _) => await LoadProvidersAsync();
        if (edit is not null) EnterEditMode(edit);
        else if (draft is not null) EnterDraftMode(draft);
    }

    private CloudProviderDescriptor SelectedProvider =>
        (ServiceBox.SelectedItem as ComboBoxItem)?.Tag as CloudProviderDescriptor
        ?? CloudProviderCatalog.Providers[0];

    private EndpointType SelectedKind => SelectedProvider.Kind;
    private string ProviderName => S3ProviderBox.Text.Trim();
    private string Secret => S3SecretVisibleBox.Visibility == Visibility.Visible ? S3SecretVisibleBox.Text : S3SecretBox.Password;
    private string RootPath => SelectedProvider.IsS3Compatible ? CurrentS3Values().Prefix : _rootBox?.Text.Trim() ?? "";

    /// <summary>
    /// The provider an existing endpoint was saved with. It is stored in Feng Sync's metadata rather than
    /// read back from rclone because the two can differ (Backblaze is rclone's generic <c>Other</c>).
    /// </summary>
    private string EditS3Provider => _edit is null ? "" : _edit.Account.Metadata?.Provider is { Length: > 0 } provider ? provider : _edit.Parameter("provider");

    /// <summary>
    /// Puts the form on the connection step, pre-filled, without the service picker: an endpoint's backend
    /// is part of its identity and cannot be changed by editing it.
    /// </summary>
    private void EnterEditMode(CloudEndpointEditTarget edit)
    {
        Title = "编辑云端端点";
        AutomationProperties.SetName(this, "编辑云端端点");
        HeadingText.Text = "编辑云端端点";

        var descriptor = CloudProviderCatalog.TryFor(edit.Kind);
        ServiceBox.SelectedItem = ServiceBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => ReferenceEquals(item.Tag, descriptor)) ?? ServiceBox.SelectedItem;
        ServiceBox.IsEnabled = false;

        StepIndicator.Visibility = Visibility.Collapsed;
        StepCaption.Text = $"编辑端点 · {CloudEndpointService.DisplayName(edit.Kind)} · 名称与类型不可修改";
        ChooseServiceStep.Visibility = Visibility.Collapsed;
        ConnectionStep.Visibility = Visibility.Visible;
        BuildProviderFields();
        S3Panel.Visibility = SelectedProvider.IsS3Compatible ? Visibility.Visible : Visibility.Collapsed;
        ProviderFieldsPanel.Visibility = SelectedProvider.IsS3Compatible ? Visibility.Collapsed : Visibility.Visible;

        NextButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        TestButton.Visibility = SaveButton.Visibility = Visibility.Visible;

        if (SelectedProvider.IsS3Compatible)
        {
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
            return;
        }

        if (_nameBox is not null) { _nameBox.Text = edit.RemoteName; _nameBox.IsReadOnly = true; }
        if (_rootBox is not null) _rootBox.Text = edit.Account.RootPath;
        // A password field is deliberately left empty. rclone stores it obscured, so the form has no
        // readable value to show; an empty box means "keep the stored one", which is exactly what the
        // write does. Pre-filling it would send the obscured value back as if it were plaintext.
        foreach (var field in SelectedProvider.Fields.Where(field => field.Kind != CloudFormFieldKind.Password))
            SetField(field, edit.Parameter(field.Key));
    }

    /// <summary>
    /// Puts the form on the connection step for an endpoint an export file described: the service is fixed
    /// and the non-secret values are known. The name stays editable — an import may have renamed it to avoid
    /// a collision — and no credential is guessed, so the user has to supply them.
    /// </summary>
    private void EnterDraftMode(CloudEndpointDraft draft)
    {
        Title = "导入云端端点";
        AutomationProperties.SetName(this, "导入云端端点");
        HeadingText.Text = "导入云端端点";

        var descriptor = CloudProviderCatalog.TryFor(draft.Kind);
        ServiceBox.SelectedItem = ServiceBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => ReferenceEquals(item.Tag, descriptor)) ?? ServiceBox.SelectedItem;
        ServiceBox.IsEnabled = false;

        StepIndicator.Visibility = Visibility.Collapsed;
        StepCaption.Text = $"导入端点 · {CloudEndpointService.DisplayName(draft.Kind)} · 请补填凭据后保存";
        ChooseServiceStep.Visibility = Visibility.Collapsed;
        ConnectionStep.Visibility = Visibility.Visible;
        BuildProviderFields();
        S3Panel.Visibility = SelectedProvider.IsS3Compatible ? Visibility.Visible : Visibility.Collapsed;
        ProviderFieldsPanel.Visibility = SelectedProvider.IsS3Compatible ? Visibility.Collapsed : Visibility.Visible;

        NextButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        TestButton.Visibility = SaveButton.Visibility = Visibility.Visible;

        if (SelectedProvider.IsS3Compatible)
        {
            S3NameBox.Text = draft.RemoteName;
            S3ProviderBox.Text = draft.Provider;
            S3BucketBox.Text = draft.Bucket;
            S3SubdirectoryBox.Text = draft.Subdirectory;
            MarkS3Dirty();
            return;
        }

        if (_nameBox is not null) _nameBox.Text = draft.RemoteName;
        if (_rootBox is not null) _rootBox.Text = draft.Subdirectory;
    }

    private async Task LoadProvidersAsync()
    {
        try
        {
            _providers = await CloudEndpointService.LoadS3ProvidersAsync();
            S3ProviderBox.ItemsSource = _providers.Select(provider => provider.Name);
            S3ProviderBox.Text = _draft is not null ? _draft.Provider
                : _edit is not null ? EditS3Provider
                : _providers.FirstOrDefault()?.Name ?? "";
            UpdateProviderHelp();
            // UpdateProviderHelp replaces the region's suggestion list, and an editable ComboBox may drop
            // its text when its ItemsSource changes. An edit form therefore restates the region it loaded
            // instead of trusting the control to have kept it: losing it would silently retarget the
            // endpoint at a different region.
            if (_edit is not null && SelectedProvider.IsS3Compatible) S3RegionBox.Text = _edit.Parameter("region");
        }
        catch (Exception ex)
        {
            ProviderDescription.Text = "无法读取 Provider：" + RcloneUiError.Describe(ex, "s3-provider-list");
            NextButton.IsEnabled = !SelectedProvider.IsS3Compatible;
        }
    }

    private void Service_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderChoicePanel is null) return;
        ProviderChoicePanel.Visibility = SelectedProvider.IsS3Compatible ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = !SelectedProvider.IsS3Compatible || _providers.Count > 0;
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
        // silently overwrite it. An import draft likewise knows nothing about the region, so it is left
        // for the user. Defaults are a new-endpoint convenience.
        if (_edit is not null || _draft is not null) return;
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
        if (SelectedProvider.IsS3Compatible && !_providers.Any(provider => provider.Name.Equals(ProviderName, StringComparison.Ordinal)))
        { ProviderError.Text = "请选择列表中的 S3 Provider。"; S3ProviderBox.Focus(); return; }
        ProviderError.Text = "";
        ChooseServiceStep.Visibility = Visibility.Collapsed; ConnectionStep.Visibility = Visibility.Visible;
        BuildProviderFields();
        S3Panel.Visibility = SelectedProvider.IsS3Compatible ? Visibility.Visible : Visibility.Collapsed;
        ProviderFieldsPanel.Visibility = SelectedProvider.IsS3Compatible ? Visibility.Collapsed : Visibility.Visible;
        StepCaption.Text = "第 2 步，共 2 步 · 配置连接";
        StepOneIndicator.Background = (Brush)FindResource("BorderSubtleBrush"); StepTwoIndicator.Background = (Brush)FindResource("AccentBrush");
        NextButton.Visibility = Visibility.Collapsed; BackButton.Visibility = TestButton.Visibility = SaveButton.Visibility = Visibility.Visible;
        if (SelectedProvider.IsS3Compatible) MarkS3Dirty();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ChooseServiceStep.Visibility = Visibility.Visible; ConnectionStep.Visibility = Visibility.Collapsed;
        StepCaption.Text = "第 1 步，共 2 步 · 选择服务";
        StepOneIndicator.Background = (Brush)FindResource("AccentBrush"); StepTwoIndicator.Background = (Brush)FindResource("BorderSubtleBrush");
        NextButton.Visibility = Visibility.Visible; BackButton.Visibility = TestButton.Visibility = SaveButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Renders the selected provider's connection form. Rebuilt rather than reused because the fields
    /// differ per provider; the values the user typed into the previous provider's form are deliberately
    /// not carried over, since they describe a different service.
    /// </summary>
    private void BuildProviderFields()
    {
        _fieldInputs.Clear();
        _fieldLabels.Clear();
        _nameBox = null;
        _rootBox = null;
        ProviderFieldsPanel.Children.Clear();
        var descriptor = SelectedProvider;
        if (descriptor.IsS3Compatible) return;

        ProviderFieldsPanel.Children.Add(new TextBlock
        {
            Text = $"{descriptor.DisplayName} 连接",
            Style = (Style)FindResource("SectionTitleTextStyle")
        });
        ProviderFieldsPanel.Children.Add(Label("显示名称", 12));
        _nameBox = new TextBox { Text = descriptor.NamePlaceholder, Margin = new Thickness(0, 4, 0, 8) };
        AutomationProperties.SetAutomationId(_nameBox, descriptor.NameAutomationId);
        ProviderFieldsPanel.Children.Add(_nameBox);

        foreach (var field in descriptor.Fields)
        {
            var label = Label(_edit is null ? field.Label : field.EditLabel ?? field.Label, 8);
            _fieldLabels[field.Key] = label;
            ProviderFieldsPanel.Children.Add(label);
            var input = field.Kind == CloudFormFieldKind.Password ? (Control)new PasswordBox() : new TextBox();
            if (field.DefaultValue is { Length: > 0 } defaultValue)
            {
                if (input is PasswordBox password) password.Password = defaultValue; else ((TextBox)input).Text = defaultValue;
            }
            input.Margin = new Thickness(0, 4, 0, field.Hint is { Length: > 0 } ? 2 : 8);
            if (field.AutomationId is { Length: > 0 } automationId) AutomationProperties.SetAutomationId(input, automationId);
            _fieldInputs[field.Key] = input;
            ProviderFieldsPanel.Children.Add(input);
            if (field.Hint is { Length: > 0 } hint)
                ProviderFieldsPanel.Children.Add(new TextBlock
                {
                    Text = hint,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)FindResource("CaptionTextStyle"),
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    Margin = new Thickness(0, 0, 0, 8)
                });
        }

        ProviderFieldsPanel.Children.Add(Label("远程根目录（可选）", 0));
        _rootBox = new TextBox { Text = descriptor.DefaultRoot, Margin = new Thickness(0, 4, 0, 8) };
        AutomationProperties.SetAutomationId(_rootBox, descriptor.RootAutomationId);
        ProviderFieldsPanel.Children.Add(_rootBox);
    }

    private static TextBlock Label(string text, double topMargin) =>
        new() { Text = text, Margin = new Thickness(0, topMargin, 0, 0) };

    /// <summary>
    /// The generated form's control for an rclone config key, its display-name and root boxes, and the
    /// label rendered above a field. Exposed so the in-process window tests can read what the dynamic form
    /// built: the alternative — a named field per provider per control — is exactly the coupling this
    /// replaced, and it is what made adding a provider a XAML change.
    /// </summary>
    internal Control? FieldInput(string key) => _fieldInputs.GetValueOrDefault(key);
    internal TextBox? NameInput => _nameBox;
    internal TextBox? RootInput => _rootBox;
    internal string FieldLabel(string key) => _fieldLabels.TryGetValue(key, out var label) ? label.Text : "";
    /// <summary>The provider the form is currently configured for.</summary>
    internal EndpointType SelectedKindForTest => SelectedKind;

    private string ReadField(CloudFormField field)
    {
        if (!_fieldInputs.TryGetValue(field.Key, out var input)) return "";
        return input is PasswordBox password ? password.Password : input is TextBox text ? text.Text : "";
    }

    private void SetField(CloudFormField field, string value)
    {
        if (!_fieldInputs.TryGetValue(field.Key, out var input)) return;
        if (input is PasswordBox password) password.Password = value; else if (input is TextBox text) text.Text = value;
    }

    /// <summary>
    /// The parameters to write, and whether the password fields among them are the obscured values already
    /// stored in rclone's config. Only an edit form can produce the latter, and the flag has to reach the
    /// write: rclone obscures a password field that is not already obscured, so obscuring it a second time
    /// would corrupt the stored password. A blank password box on an edit form means "keep the stored one",
    /// which is exactly what re-sending the obscured value does.
    /// </summary>
    private (IReadOnlyDictionary<string, string> Values, bool PasswordsAreObscured) CollectProviderFields()
    {
        var descriptor = SelectedProvider;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var passwords = descriptor.Fields.Where(field => field.Kind == CloudFormFieldKind.Password).ToList();
        var obscured = 0;
        foreach (var field in descriptor.Fields)
        {
            var typed = ReadField(field).Trim();
            if (field.Kind == CloudFormFieldKind.Password && typed.Length == 0 && _edit is not null)
            {
                typed = _edit.Parameter(field.Key);
                if (typed.Length > 0) obscured++;
            }
            values[field.Key] = typed;
        }
        Validate(descriptor, values);
        // Every password field has to be obscured for the flag to be safe to send: it applies to the whole
        // write, so one newly typed password among stored ones would corrupt the others.
        return (values, passwords.Count > 0 && obscured == passwords.Count);
    }

    /// <summary>
    /// Rejects a form rclone would accept but fail on later. The rules come from the descriptor — required
    /// fields, a numeric range, and any rule no single field can state — so a new provider inherits the
    /// generic checks and only has to describe what is special about it.
    /// </summary>
    private void Validate(CloudProviderDescriptor descriptor, IReadOnlyDictionary<string, string> values)
    {
        if (_nameBox is not null && string.IsNullOrWhiteSpace(_nameBox.Text))
            throw new InvalidOperationException("请输入显示名称。");
        foreach (var field in descriptor.Fields)
        {
            if (field.Required && string.IsNullOrWhiteSpace(values.GetValueOrDefault(field.Key)))
                throw new InvalidOperationException($"请填写{field.Label}。");
            if (field.Kind == CloudFormFieldKind.Number && values.GetValueOrDefault(field.Key) is { Length: > 0 } number
                && (!int.TryParse(number, out var parsed) || parsed is < 1 or > 65535))
                throw new InvalidOperationException($"{field.Label}必须介于 1 和 65535 之间。");
        }
        if (descriptor.CrossFieldRule?.Invoke(values) is { Length: > 0 } error) throw new InvalidOperationException(error);
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
        if (SelectedProvider.IsS3Compatible)
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
            // The endpoint already exists, so testing it only has to reach the configured root. For an OAuth
            // provider that also avoids a browser round-trip the user did not ask for by opening the editor.
            await RunBusyAsync(TestButton, "正在验证连接…", async () => { await CloudEndpointService.VerifyAsync(_edit.RemoteName, RootPath); StatusText.Text = "连接验证成功。"; });
            return;
        }
        await RunBusyAsync(TestButton, SelectedProvider.RequiresOAuth ? "正在等待浏览器授权…" : "正在验证连接…", async () => { var remote = await EnsureRemoteAsync(); await CloudEndpointService.VerifyAsync(remote, RootPath); StatusText.Text = "连接验证成功。"; });
    }

    private void BrowseProbe_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListBox { MinWidth = 420, MinHeight = 260, Margin = new Thickness(12) };
        if (_probeDirectories.Count == 0) list.Items.Add("（当前目录没有子目录）"); else foreach (var directory in _probeDirectories) list.Items.Add("📁 " + directory);
        new Window { Title = "测试读取结果", Owner = this, Content = list, Width = 480, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProvider.IsS3Compatible)
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
            await RunBusyAsync(SaveButton, "正在保存…", async () => { await CloudEndpointService.SaveS3Async(remote, CurrentS3Values(), replace); SavedRemoteName = remote; SavedKind = EndpointType.S3;
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
            var reauthorize = OAuthCredentialsChanged();
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

    /// <summary>
    /// Whether saving has to send the user through the browser again. An OAuth provider's credentials only
    /// take effect through the authorization flow, but an unchanged one must not force a browser round-trip
    /// just to move the endpoint's root folder. A blank password box on an edit form is never a change: it
    /// means "keep the stored value".
    /// </summary>
    private bool OAuthCredentialsChanged()
    {
        if (_edit is null || !SelectedProvider.RequiresOAuth) return false;
        foreach (var field in SelectedProvider.Fields)
        {
            var typed = ReadField(field).Trim();
            if (field.Kind == CloudFormFieldKind.Password && typed.Length == 0) continue;
            if (!string.Equals(typed, _edit.Parameter(field.Key), StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private async Task<string> EnsureRemoteAsync()
    {
        if (_configuredRemote is not null && _configuredKind == SelectedKind) return _configuredRemote;
        // An edited endpoint keeps the name it was listed under; renaming would silently break every
        // profile, CLI job and schedule that addresses it.
        var remote = _edit?.RemoteName ?? CloudEndpointService.SanitizeRemoteName(_nameBox?.Text);
        var (values, passwordsAreObscured) = CollectProviderFields();
        await CloudEndpointService.CreateRemoteAsync(SelectedKind, remote, values, passwordsAreObscured);
        _configuredRemote = remote; _configuredKind = SelectedKind; return remote;
    }

    private async Task RunBusyAsync(Button button, string busyText, Func<Task> action)
    {
        if (_busy) return; _busy = true; var original = button.Content;
        try { BackButton.IsEnabled = TestButton.IsEnabled = SaveButton.IsEnabled = false; button.Content = busyText; StatusText.Text = busyText; await action(); }
        catch (Exception ex) { var message = RcloneUiError.Describe(ex, "cloud-endpoint-editor"); StatusText.Text = message; MessageBox.Show(message, "Feng Sync", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { BackButton.IsEnabled = TestButton.IsEnabled = SaveButton.IsEnabled = true; button.Content = original; _busy = false; }
    }
}
