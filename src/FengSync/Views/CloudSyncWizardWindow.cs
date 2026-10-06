using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FengSync.Core;
using FengSync.Localization;
using FengSync.Services;
using Microsoft.Win32;

namespace FengSync.Views;

/// <summary>
/// E2：一键双向同步向导。两格（本地文件夹 / 云盘端点）加一个名字，产出的是一个
/// <see cref="SyncProfile"/>，模式固定 <see cref="SyncMode.TwoWay"/>。
/// </summary>
/// <remarks>
/// <para>它**不**另造一个 Profile 表单：保存走既有的 <see cref="ProfileStore"/>，校验走
/// <see cref="CloudSyncTask.WizardOfferFor"/>，需要落点或答案形状的动作（挑文件夹、挑端点）都从这里
/// 问出去。这个窗存在的理由只是「少点几下」——E1 的 创建同步任务… 是同一件事的完整版本，
/// 它把云端那侧已经填好然后交给 Profile 编辑器；这里的取舍是反过来：两格填完直接建。</para>
/// <para>Enter 落在 <b>创建</b> 上，与 <c>TextPrompt.Confirm</c> 落在「取消」上的做法不同，而且是有意的：
/// 那条规则针对的是**确认框**——一个没人能应答的确认框会把机器交出去。这里是一张表单，两格都是用户
/// 刚刚自己挑的，创建不是破坏性动作，也没有任何需要「先想一想」的默认。</para>
/// <para>代码搭的窗拿不到页面 XAML 的护栏扫描，所以三条规矩由作者自己守住：样式一律经
/// <c>TextPrompt.Resource&lt;T&gt;</c> 从合并字典取（内联 hex 为零）、可操作的控件都有
/// <see cref="AutomationProperties.AutomationId"/>、<see langword="null"/> 的含义由调用方按
/// 「用户取消」处理。</para>
/// </remarks>
public sealed class CloudSyncWizardWindow : Window
{
    private readonly TextBox _localBox = NewBox("CloudSyncWizardLocal", Tr.F("SyncWizard.LocalFolderName"));
    private readonly TextBox _cloudBox = NewBox("CloudSyncWizardCloud", Tr.F("SyncWizard.CloudEndpointName"));
    private readonly TextBox _nameBox = NewBox("CloudSyncWizardName", Tr.F("SyncWizard.Name"));
    private readonly TextBlock _error = new()
    {
        Style = TextPrompt.Resource<Style>("CaptionTextStyle"),
        Foreground = TextPrompt.Resource<System.Windows.Media.Brush>("DangerBrush"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0)
    };
    private readonly IReadOnlyCollection<string> _existingNames;
    private readonly Func<string, string?, string?>? _askEndpoint;
    private readonly Func<string?>? _askLocalFolder;

    /// <summary>What the user filled in, or <see langword="null"/> when the window was cancelled.</summary>
    public CloudSyncTask.Plan? Result { get; private set; }

    /// <param name="existingNames">Profile names already on disk, so the suggested name can be made unique.</param>
    /// <param name="initialLocalPath">A local folder to start from, e.g. the one a previous visit used.</param>
    /// <param name="askEndpoint">
    /// Asks for one side's endpoint and answers its URI, or <see langword="null"/> for a cancel. This is the
    /// seam: <see cref="RemoteEndpointPickerWindow"/> is a modal window no in-process test can answer, so
    /// the window under test supplies this instead.
    /// </param>
    /// <param name="askLocalFolder">
    /// The same seam for the local folder. Empty means use the real <see cref="OpenFolderDialog"/>.
    /// </param>
    public CloudSyncWizardWindow(IReadOnlyCollection<string> existingNames, string? initialLocalPath = null,
        Func<string, string?, string?>? askEndpoint = null, Func<string?>? askLocalFolder = null)
    {
        _existingNames = existingNames;
        _askEndpoint = askEndpoint;
        _askLocalFolder = askLocalFolder;
        _localBox.Text = initialLocalPath ?? "";
        Title = Tr.F("SyncWizard.WindowTitle");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "CloudSyncWizardWindow");
        AutomationProperties.SetName(this, Tr.F("SyncWizard.WindowName"));
        Content = BuildLayout();
        _localBox.Focus();
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(20) };
        var caption = TextPrompt.Resource<Style>("CaptionTextStyle");
        var section = TextPrompt.Resource<Style>("SectionTitleTextStyle");

        root.Children.Add(new TextBlock
        {
            Text = Tr.F("SyncWizard.Intro"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap
        });

        root.Children.Add(Header(Tr.F("SyncWizard.LocalFolder"), section));
        root.Children.Add(Row(Tr.F("SyncWizard.BrowseLocal"), "CloudSyncWizardBrowseLocal", BrowseLocal, _localBox));
        root.Children.Add(new TextBlock
        {
            Text = Tr.F("SyncWizard.LocalFolderHint"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        root.Children.Add(Header(Tr.F("SyncWizard.CloudEndpoint"), section));
        root.Children.Add(Row(Tr.F("SyncWizard.PickCloud"), "CloudSyncWizardPickCloud", PickCloud, _cloudBox));
        root.Children.Add(new TextBlock
        {
            Text = Tr.F("SyncWizard.CloudEndpointHint"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        root.Children.Add(Header(Tr.F("SyncWizard.NameHeader"), section));
        root.Children.Add(_nameBox);
        root.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var create = new Button { Content = Tr.F("SyncWizard.Create"), IsDefault = true, Style = TextPrompt.Resource<Style>("PrimaryButtonStyle") };
        AutomationProperties.SetAutomationId(create, "CloudSyncWizardCreate");
        create.Click += (_, _) => Accept();
        var cancel = new Button
        {
            Content = Tr.F("SyncWizard.Cancel"),
            IsCancel = true,
            Style = TextPrompt.Resource<Style>("TertiaryButtonStyle"),
            Margin = new Thickness(8, 0, 0, 0)
        };
        AutomationProperties.SetAutomationId(cancel, "CloudSyncWizardCancel");
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        return root;
    }

    private void Accept()
    {
        var offer = CloudSyncTask.WizardOfferFor(_localBox.Text, _cloudBox.Text, _existingNames);
        if (offer.Plan is not { } plan) { _error.Text = offer.Refusal ?? Tr.F("SyncWizard.CreateFailed"); return; }
        // The suggested name follows the two boxes until the user types one of their own; that is why it is
        // recomputed here instead of being filled in once when the window opened.
        var name = (_nameBox.Text ?? "").Trim();
        Result = name.Length == 0
            ? plan
            : plan with { Name = CloudSyncTask.UniqueName(name, _existingNames) };
        DialogResult = true;
    }

    private void BrowseLocal()
    {
        string? folder;
        if (_askLocalFolder is { } ask) folder = ask();
        else
        {
            var dialog = new OpenFolderDialog { Title = Tr.F("SyncWizard.LocalFolderName"), Multiselect = false };
            if (!string.IsNullOrWhiteSpace(_localBox.Text)) dialog.InitialDirectory = _localBox.Text;
            if (dialog.ShowDialog(this) != true) return;
            folder = dialog.FolderName;
        }
        if (folder is not { Length: > 0 } chosen) return;
        _localBox.Text = chosen;
        _nameBox.Clear();
    }

    private void PickCloud()
    {
        if (_askEndpoint is not { } ask) { _error.Text = Tr.F("SyncWizard.NoEndpointPicker"); return; }
        if (ask("Right", _cloudBox.Text) is not { Length: > 0 } uri) return;
        _cloudBox.Text = uri;
        _nameBox.Clear();
    }

    private static TextBlock Header(string text, Style? style) => new()
    {
        Text = text,
        Style = style,
        Margin = new Thickness(0, 16, 0, 0)
    };

    private static UIElement Row(string caption, string automationId, Action onClick, TextBox box)
    {
        var dock = new DockPanel();
        var button = new Button
        {
            Content = caption,
            Style = TextPrompt.Resource<Style>("SecondaryButtonStyle"),
            Width = 96
        };
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => onClick();
        DockPanel.SetDock(button, Dock.Right);
        dock.Children.Add(button);
        dock.Children.Add(box);
        return dock;
    }

    private static TextBox NewBox(string automationId, string name)
    {
        var box = new TextBox { Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetAutomationId(box, automationId);
        AutomationProperties.SetName(box, name);
        return box;
    }
}
