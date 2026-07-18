using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FengSync.Core;
using FengSync.Core.Automation;

namespace FengSync.Views;

/// <summary>Bounded batch queue surface; every profile remains visible after success or failure.</summary>
public sealed class BatchRunWindow : Window
{
    private readonly IReadOnlyList<SyncProfile> _profiles;
    private readonly int _concurrency;
    private readonly Func<SyncProfile, CancellationToken, Task<ProfileRunResult>> _run;
    private readonly ObservableCollection<Row> _rows;
    private readonly TextBlock _summary = new();
    private CancellationTokenSource? _cancel;
    public BatchRunWindow(IReadOnlyList<SyncProfile> profiles, int concurrency, Func<SyncProfile, CancellationToken, Task<ProfileRunResult>> run)
    {
        _profiles = profiles; _concurrency = concurrency; _run = run; _rows = new(profiles.Select(p => new Row(p)));
        Title = "批处理运行"; Width = 680; Height = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var start = new Button { Content = "开始", IsDefault = true }; var stop = new Button { Content = "停止队列", Margin = new Thickness(8, 0, 0, 0) };
        start.Click += async (_, _) => await ExecuteAsync(); stop.Click += (_, _) => _cancel?.Cancel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _summary, start, stop } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var panel = new DockPanel { Margin = new Thickness(18) }; panel.Children.Add(buttons); panel.Children.Add(new DataGrid { ItemsSource = _rows, AutoGenerateColumns = true, IsReadOnly = true }); Content = panel;
    }
    private async Task ExecuteAsync()
    {
        if (_cancel is not null) return; _cancel = new CancellationTokenSource();
        try
        {
            _summary.Text = $"队列运行中（最多 {_concurrency} 项）…";
            var scheduler = new BatchScheduler(_concurrency);
            var results = await scheduler.RunAsync(_profiles.Select((p, i) => (Func<CancellationToken, Task<ProfileRunResult>>)(async token => { _rows[i].State = "运行中"; try { var value = await _run(p, token); _rows[i].State = "成功"; return value; } catch (Exception ex) { _rows[i].State = "失败：" + ex.Message; throw; } })), _cancel.Token);
            _summary.Text = $"完成：{results.Count(x => x.Succeeded)} 成功，{results.Count(x => !x.Succeeded)} 失败。";
        }
        finally { _cancel.Dispose(); _cancel = null; }
    }
    private sealed class Row(SyncProfile profile) : INotifyPropertyChanged
    {
        private string _state = "等待";
        public string Name => profile.Name; public string State { get => _state; set { _state = value; PropertyChanged?.Invoke(this, new(nameof(State))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
