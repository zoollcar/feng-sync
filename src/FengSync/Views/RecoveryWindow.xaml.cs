using System.Windows;
using System.Windows.Controls;
using FengSync.Core;

namespace FengSync.Views;

/// <summary>Startup recovery view: it never publishes or rebuilds a baseline automatically.</summary>
public partial class RecoveryWindow : Window
{
    private readonly IReadOnlyList<RecoveryItem> _items;
    private readonly RecoveryCoordinator _coordinator;
    public RecoveryWindow(IReadOnlyList<RecoveryItem> items, RecoveryCoordinator coordinator)
    {
        InitializeComponent();
        _items = items; _coordinator = coordinator;
        Items.ItemsSource = items.Select(x => new RecoveryRow(x));
        if (items.Count > 0) Items.SelectedIndex = 0;
    }
    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Items.SelectedItem is RecoveryRow row) Status.Text = row.Detail;
    }
    private void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        var removed = _coordinator.RemoveSafeLocalTemporaryFiles(_items);
        Status.Text = removed == 0 ? "没有可安全删除的本地临时文件。" : $"已删除 {removed} 个本地临时文件；恢复记录会保留，重新比较后才能继续。";
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record RecoveryRow(string Kind, DateTimeOffset When, string Detail)
    {
        public RecoveryRow(RecoveryItem item) : this(item.Journal is null ? "基线事务" : "同步作业", item.Journal?.CreatedUtc ?? item.Transaction!.StartedUtc, item.Detail) { }
    }
}
