using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// E5 的引导本身：一个标题、一段说明、四个步骤、一个「不再显示」、两个按钮。内容全部来自
/// <see cref="CloudFirstRunGuide"/>，所以措辞与「该不该出现」是同一处说了算。
/// </summary>
/// <remarks>
/// Enter 落在 <b>稍后再说</b> 上，而不是「新建端点」——与 <c>CloudSyncWizardWindow</c> 的取舍相反，
/// 而且理由是同一个的两面：那一张是用户刚把两格都填完的表单，这一张是应用主动插进来的可选提示。
/// 一次误按 Enter 不应该凭空开出一个端点编辑器，所以不改任何东西的那颗按钮是默认。
/// 样式经 <c>TextPrompt.Resource&lt;T&gt;</c> 取（内联 hex 为零），可操作的控件都有 AutomationId。
/// </remarks>
internal sealed class CloudFirstRunPrompt : Window
{
    private readonly CheckBox _neverAgain = new()
    {
        Content = CloudFirstRunGuide.NeverAgainLabel,
        Margin = new Thickness(0, 12, 0, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    public CloudFirstRunPrompt()
    {
        Title = CloudFirstRunGuide.Title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "CloudFirstRunPrompt");
        AutomationProperties.SetName(this, CloudFirstRunGuide.Title);
        Content = BuildLayout();
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel { Margin = new Thickness(20) };
        var caption = TextPrompt.Resource<Style>("CaptionTextStyle");

        root.Children.Add(new TextBlock
        {
            Text = CloudFirstRunGuide.Intro,
            Style = caption,
            TextWrapping = TextWrapping.Wrap
        });

        var steps = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var index = 1;
        foreach (var step in CloudFirstRunGuide.Steps())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var number = new TextBlock
            {
                Text = index.ToString(),
                Width = 22,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Top
            };
            DockPanel.SetDock(number, Dock.Left);
            row.Children.Add(number);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = step.Title, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = step.Detail, Style = caption, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(text);
            steps.Children.Add(row);
            index++;
        }
        root.Children.Add(steps);

        AutomationProperties.SetAutomationId(_neverAgain, "CloudFirstRunNeverAgain");
        root.Children.Add(_neverAgain);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var later = new Button
        {
            Content = CloudFirstRunGuide.LaterLabel,
            IsDefault = true,
            IsCancel = true,
            Style = TextPrompt.Resource<Style>("TertiaryButtonStyle")
        };
        AutomationProperties.SetAutomationId(later, "CloudFirstRunLater");
        later.Click += (_, _) => { Answer = _neverAgain.IsChecked == true ? CloudFirstRunGuide.Choice.Never : CloudFirstRunGuide.Choice.Later; DialogResult = true; };
        var start = new Button
        {
            Content = CloudFirstRunGuide.StartLabel,
            Style = TextPrompt.Resource<Style>("PrimaryButtonStyle"),
            Margin = new Thickness(8, 0, 0, 0)
        };
        AutomationProperties.SetAutomationId(start, "CloudFirstRunStart");
        start.Click += (_, _) => { Answer = CloudFirstRunGuide.Choice.Start; DialogResult = true; };
        buttons.Children.Add(later);
        buttons.Children.Add(start);
        root.Children.Add(buttons);
        return root;
    }

    /// <summary>Which button closed the guide, and whether the box was ticked on the way out.</summary>
    public CloudFirstRunGuide.Choice Answer { get; private set; } = CloudFirstRunGuide.Choice.Later;
}
