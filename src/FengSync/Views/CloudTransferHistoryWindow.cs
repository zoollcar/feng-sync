using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FengSync.Services;

namespace FengSync.Views;

/// <summary>
/// The transfer archive: what was moved, and how it ended, for transfers that have left the queue.
/// </summary>
/// <remarks>
/// Built in code rather than as a XAML page. It shows one list of pre-rendered lines and has no layout worth
/// sharing, which is the same reason the browser's prompts are built this way — and it keeps the page-XAML
/// design guardrails pointed at real pages.
/// </remarks>
public sealed class CloudTransferHistoryWindow : Window
{
    private readonly CloudTransferHistoryRepository _history;
    private readonly ListBox _list = new();
    private readonly TextBlock _summary;

    public CloudTransferHistoryWindow(CloudTransferHistoryRepository history)
    {
        _history = history;
        Title = "传输历史";
        Width = 900;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "CloudTransferHistoryWindow");
        AutomationProperties.SetName(this, "已归档的云盘传输记录");

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = "传输历史",
            Style = Resource<Style>("DisplayTitleTextStyle")
        });
        // No static AutomationProperties.Name: this block *is* the summary, and a label would replace it.
        _summary = new TextBlock
        {
            Style = Resource<Style>("CaptionTextStyle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        AutomationProperties.SetAutomationId(_summary, "CloudTransferHistorySummary");
        header.Children.Add(_summary);
        root.Children.Add(header);

        _list.BorderThickness = new Thickness(0);
        AutomationProperties.SetAutomationId(_list, "CloudTransferHistoryList");
        AutomationProperties.SetName(_list, "归档的传输记录");
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var clear = new Button { Content = "清空历史", Style = Resource<Style>("DangerButtonStyle") };
        AutomationProperties.SetAutomationId(clear, "CloudTransferHistoryClear");
        AutomationProperties.SetName(clear, "删除全部归档记录");
        clear.Click += async (_, _) =>
        {
            if (!TextPrompt.Confirm(this, "清空传输历史", "删除全部归档的传输记录？此操作不可撤销。", danger: true)) return;
            await _history.ClearAsync();
            await ReloadAsync();
        };
        buttons.Children.Add(clear);
        var close = new Button { Content = "关闭", IsCancel = true, Style = Resource<Style>("TertiaryButtonStyle"), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(close, "CloudTransferHistoryClose");
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
        Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>The lines currently shown, so a test can read them without touching the visual tree.</summary>
    internal IReadOnlyList<string> Lines => [.. _list.Items.Cast<string>()];

    internal string SummaryLabel => _summary.Text;

    internal async Task ReloadAsync()
    {
        var entries = await _history.QueryAsync();
        _summary.Text = CloudTransferHistorySummary.DescribeAll(entries);
        _list.Items.Clear();
        foreach (var entry in entries) _list.Items.Add(CloudTransferHistorySummary.Describe(entry));
    }

    /// <summary>
    /// Looks a shared style up without assuming a shell merged the theme dictionaries — a test process may have
    /// none, and a window that cannot find its styles must still be usable.
    /// </summary>
    private static T? Resource<T>(string key) where T : class => Application.Current?.TryFindResource(key) as T;
}
