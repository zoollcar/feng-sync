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
    /// Shows one details panel. <paramref name="action"/> is the one expensive read the panel may still offer,
    /// already named and worded by <see cref="CloudItemDetails"/>: a checksum walks one object and a size walks a
    /// whole subtree, so which of them a row earns — and whether it earns neither — is decided before this is
    /// reached. Without it the panel is read-only, which is the case for a selection and for an object the backend
    /// no longer has. Running the action redraws the rows from the answer it produced and asks what is left to
    /// offer: a directory that has just been counted and a file whose backend has no checksums stop asking,
    /// because a second click would spend the same read for the same sentence.
    /// </summary>
    public static void Show(Window owner, string title, IReadOnlyList<CloudDetailRow> rows,
        CloudDetailsAction? action = null)
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
        AutomationProperties.SetAutomationId(hint, "CloudDetailsHint");
        body.Children.Add(hint);

        var actionButton = new Button
        {
            Style = TextPrompt.Resource<Style>("SecondaryButtonStyle"),
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(actionButton, "CloudDetailsAction");
        var close = new Button { Content = "关闭", IsDefault = true, IsCancel = true, Style = TextPrompt.Resource<Style>("TertiaryButtonStyle") };
        AutomationProperties.SetAutomationId(close, "CloudDetailsClose");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 4)
        };
        // Both reads cost the cloud real work — a checksum a pass over one object, a size a walk down a whole
        // subtree — so neither happens on opening 属性; the user asks for the one they want. Enter closes the
        // panel instead of starting anything.
        buttons.Children.Add(actionButton);
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

        if (action is not null)
        {
            actionButton.Content = action.Label;
            hint.Text = action.Hint;
            hint.Visibility = Visibility.Visible;
            actionButton.Visibility = Visibility.Visible;
            actionButton.Click += async (_, _) =>
            {
                // Both buttons go dark for the duration: the read cannot be recalled, and leaving 关闭 clickable
                // would let the panel vanish while its answer is still in flight.
                actionButton.IsEnabled = false;
                close.IsEnabled = false;
                try
                {
                    var panel = await action.Run();
                    Render(panel.Rows);
                    if (panel.Action is null)
                    {
                        actionButton.Visibility = Visibility.Collapsed;
                        hint.Visibility = Visibility.Collapsed;
                    }
                }
                finally
                {
                    actionButton.IsEnabled = true;
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
