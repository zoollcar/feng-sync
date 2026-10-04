using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// Draws the rows <see cref="CloudItemDetails"/> decided. It owns no rule of its own — which facts exist,
/// what a missing one is called, and what a folder's size may claim are all answered before this is reached,
/// which is also what keeps the panel testable: a dialog nobody can answer in a test still has nothing to
/// decide here. Built in code like <see cref="TextPrompt"/> because a label/value list is not a layout worth
/// a XAML window.
/// </summary>
internal static class CloudDetailsPrompt
{
    /// <summary>
    /// Shows one details panel. <paramref name="computeHash"/> is what the 计算校验值 button calls, and it is
    /// passed only when the answer can still change (a file that is still there and has no checksum shown);
    /// without it the panel is read-only, which is the case for a folder, a selection and an object the
    /// backend no longer has.
    /// </summary>
    public static void Show(Window owner, string title, IReadOnlyList<CloudDetailRow> rows,
        Func<Task<IReadOnlyList<CloudDetailRow>>>? computeHash = null)
    {
        var window = TextPrompt.NewWindow(title, owner, 560);
        var body = new StackPanel { Margin = new Thickness(20) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var scroll = new ScrollViewer
        {
            Content = grid,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 12)
        };
        AutomationProperties.SetAutomationId(scroll, "CloudDetails");
        body.Children.Add(scroll);

        var hint = new TextBlock
        {
            Style = TextPrompt.Resource<Style>("CaptionTextStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Visibility = Visibility.Collapsed
        };
        body.Children.Add(hint);

        var hashButton = new Button
        {
            Content = "计算校验值",
            Style = TextPrompt.Resource<Style>("SecondaryButtonStyle"),
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(hashButton, "CloudDetailsHash");
        var close = new Button { Content = "关闭", IsDefault = true, IsCancel = true, Style = TextPrompt.Resource<Style>("TertiaryButtonStyle") };
        AutomationProperties.SetAutomationId(close, "CloudDetailsClose");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 4)
        };
        // Reading a checksum can cost a full pass over the object, so it is the one thing here the user has to
        // ask for; opening 属性 must never start it. Enter closes the panel instead of computing anything.
        buttons.Children.Add(hashButton);
        buttons.Children.Add(close);
        body.Children.Add(buttons);

        void Render(IReadOnlyList<CloudDetailRow> shown)
        {
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            for (var index = 0; index < shown.Count; index++)
            {
                var (label, value) = shown[index];
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Add(grid, 0, index, new TextBlock
                {
                    Text = label,
                    Style = TextPrompt.Resource<Style>("CaptionTextStyle"),
                    Margin = new Thickness(0, 0, 12, 6)
                }, "CloudDetailsLabel");
                Add(grid, 1, index, new TextBlock
                {
                    Text = value,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6)
                }, "CloudDetailsValue");
            }
        }

        if (computeHash is not null)
        {
            hashButton.Visibility = Visibility.Visible;
            hint.Text = "校验值需要读取整个文件；云端对象会因此走完一遍下载，大文件请耐心等待。";
            hint.Visibility = Visibility.Visible;
            hashButton.Click += async (_, _) =>
            {
                hashButton.IsEnabled = false;
                close.IsEnabled = false;
                try { Render(await computeHash()); }
                finally
                {
                    hashButton.IsEnabled = true;
                    close.IsEnabled = true;
                }
            };
        }

        Render(rows);
        window.Content = body;
        window.ShowDialog();
    }

    private static void Add(Grid grid, int column, int row, TextBlock text, string automationPrefix)
    {
        AutomationProperties.SetAutomationId(text, automationPrefix + row);
        Grid.SetColumn(text, column);
        Grid.SetRow(text, row);
        grid.Children.Add(text);
    }
}
