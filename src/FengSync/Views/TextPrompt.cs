using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace FengSync.Views;

/// <summary>
/// The two small prompts the file browser needs — a name to create or rename to, and a firm confirmation
/// for a destructive action. Built in code rather than as a XAML window because they carry no layout worth
/// sharing, and because keeping them here means the page-XAML design guardrails stay focused on real pages.
/// </summary>
internal static class TextPrompt
{
    /// <summary>
    /// Asks for a single name. <paramref name="validate"/> returns the reason the value is unusable, or
    /// <see langword="null"/> when it is fine; a rejection keeps the window open with the reason inline,
    /// which is what makes "a name cannot contain a slash" a correction rather than a lost attempt.
    /// </summary>
    public static string? Ask(Window owner, string title, string label, string initial, Func<string, string?> validate)
    {
        var window = NewWindow(title, owner, 440);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });

        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 6, 8, 6) };
        AutomationProperties.SetAutomationId(box, "TextPromptNameBox");
        root.Children.Add(box);

        var error = new TextBlock
        {
            Style = Resource<Style>("CaptionTextStyle"),
            Foreground = Resource<System.Windows.Media.Brush>("DangerBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        root.Children.Add(error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var confirm = new Button { Content = "确定", IsDefault = true, Style = Resource<Style>("PrimaryButtonStyle") };
        AutomationProperties.SetAutomationId(confirm, "TextPromptConfirm");
        var cancel = new Button { Content = "取消", IsCancel = true, Style = Resource<Style>("TertiaryButtonStyle"), Margin = new Thickness(8, 0, 0, 0) };
        confirm.Click += (_, _) =>
        {
            var reason = validate(box.Text);
            if (reason is not null) { error.Text = reason; box.Focus(); box.SelectAll(); return; }
            window.DialogResult = true;
        };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        window.Content = root;
        box.Focus();
        box.SelectAll();
        return window.ShowDialog() == true ? box.Text : null;
    }

    /// <summary>
    /// A confirmation whose default button is the safe one. <paramref name="danger"/> makes the confirming
    /// action the red variant, which is what the design system reserves for the choice that destroys data.
    /// </summary>
    public static bool Confirm(Window owner, string title, string message, bool danger = false)
    {
        var window = NewWindow(title, owner, 480);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var confirm = new Button
        {
            Content = "继续",
            Style = Resource<Style>(danger ? "DangerButtonStyle" : "PrimaryButtonStyle")
        };
        AutomationProperties.SetAutomationId(confirm, "TextPromptConfirm");
        var cancel = new Button { Content = "取消", IsDefault = true, IsCancel = true, Style = Resource<Style>("TertiaryButtonStyle"), Margin = new Thickness(8, 0, 0, 0) };
        confirm.Click += (_, _) => window.DialogResult = true;
        // Cancel first, and as the default: an accidental Enter must not confirm a delete.
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        root.Children.Add(buttons);

        window.Content = root;
        return window.ShowDialog() == true;
    }

    private static Window NewWindow(string title, Window owner, double width) => new()
    {
        Title = title,
        Owner = owner,
        Width = width,
        SizeToContent = SizeToContent.Height,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false
    };

    /// <summary>
    /// Looks a shared style up without assuming a shell merged the theme dictionaries — a preview or a test
    /// process may have none, and a prompt that cannot find its styles must still be usable.
    /// </summary>
    internal static T? Resource<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key) as T;
}
