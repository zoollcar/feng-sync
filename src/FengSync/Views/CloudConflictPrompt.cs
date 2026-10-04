using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// The question a taken destination name asks, one file at a time, with the answer reusable for the ones
/// still waiting. Built in code like <see cref="TextPrompt"/> because it carries no layout worth sharing.
/// It exists at all because a three-button message box cannot offer 覆盖 / 保留两者 / 跳过 without dropping
/// one of them, and dropping 保留两者 is what made every conflict a choice between losing the old file and
/// losing the new one.
/// </summary>
internal static class CloudConflictPrompt
{
    /// <summary>
    /// Asks about one conflicted name. <paramref name="remaining"/> counts the files still undecided,
    /// including this one, which is what makes "apply to the rest" an honest offer rather than a promise about
    /// a number the user cannot see. <paramref name="taken"/> supplies the preview next to 保留两者: choosing
    /// between replacing and renaming has to show that the second keeps both files, rather than let the user
    /// guess what the rename costs them.
    /// </summary>
    public static CloudConflictAnswer? Ask(Window owner, string path, int remaining, IReadOnlyCollection<string> taken)
    {
        var name = CloudTransferTask.NameOf(path);
        var window = new Window
        {
            Title = "目标已有同名文件",
            Owner = owner,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        // The answer travels through this local rather than through DialogResult, which can only say yes or
        // no; a closed window that never reached a button leaves it null, which is the cancellation.
        CloudConflictAnswer? answer = null;

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = $"“{name}”已经在目标目录里。", TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock
        {
            Text = remaining > 1 ? $"本次还有 {remaining} 个同名文件要决定。" : "这是最后一个同名文件。",
            Style = TextPrompt.Resource<Style>("CaptionTextStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
        root.Children.Add(new TextBlock
        {
            Text = "目标：" + path,
            Style = TextPrompt.Resource<Style>("CaptionTextStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var applyToAll = new CheckBox
        {
            Content = remaining > 1 ? $"把这次选择应用到其余 {remaining - 1} 个同名文件" : "把这次选择应用到其余同名文件",
            Margin = new Thickness(0, 14, 0, 0),
            IsEnabled = remaining > 1
        };
        AutomationProperties.SetAutomationId(applyToAll, "CloudConflictApplyToAll");
        root.Children.Add(applyToAll);

        Button Choice(string content, string automationId, string style, CloudConflictChoice option, bool isDefault)
        {
            var button = new Button
            {
                Content = content,
                Style = TextPrompt.Resource<Style>(style),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault
            };
            AutomationProperties.SetAutomationId(button, automationId);
            button.Click += (_, _) =>
            {
                answer = new CloudConflictAnswer(option, applyToAll.IsChecked == true && remaining > 1);
                window.DialogResult = true;
            };
            return button;
        }

        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true,
            Style = TextPrompt.Resource<Style>("TertiaryButtonStyle"),
            Margin = new Thickness(8, 0, 0, 0)
        };
        AutomationProperties.SetAutomationId(cancel, "CloudConflictCancel");

        var overwrite = Choice("覆盖", "CloudConflictOverwrite", "PrimaryButtonStyle", CloudConflictChoice.Overwrite, isDefault: false);
        var keepBoth = Choice($"保留两者（存为“{CloudFileNames.KeepBothName(name, taken)}”）",
            "CloudConflictKeepBoth", "SecondaryButtonStyle", CloudConflictChoice.KeepBoth, isDefault: false);
        // Skipping is the answer that changes nothing, so it is the one allowed to take Enter; an accidental
        // keypress must not replace a file.
        var skip = Choice("跳过", "CloudConflictSkip", "SecondaryButtonStyle", CloudConflictChoice.Skip, isDefault: true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(skip);
        buttons.Children.Add(keepBoth);
        buttons.Children.Add(overwrite);
        root.Children.Add(buttons);

        window.Content = root;
        overwrite.Focus();
        return window.ShowDialog() == true ? answer : null;
    }
}
