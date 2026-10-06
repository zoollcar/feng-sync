using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using FengSync.Core;
using FengSync.Core.Automation;
using FengSync.Localization;
using FengSync.Services;

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
        Title = Tr.F("Batch.WindowTitle"); Width = 680; Height = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "BatchRunWindow");
        AutomationProperties.SetName(this, Tr.F("Batch.WindowTitle"));
        AutomationProperties.SetAutomationId(_summary, "BatchRunSummary");
        AutomationProperties.SetLiveSetting(_summary, AutomationLiveSetting.Polite);
        var start = new Button { Content = Tr.F("Batch.Start"), IsDefault = true }; var stop = new Button { Content = Tr.F("Batch.StopQueue"), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(start, "BatchRunStart"); AutomationProperties.SetName(start, Tr.F("Batch.StartName"));
        AutomationProperties.SetAutomationId(stop, "BatchRunStop"); AutomationProperties.SetName(stop, Tr.F("Batch.StopName"));
        start.Click += async (_, _) => await ExecuteAsync(); stop.Click += (_, _) => _cancel?.Cancel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _summary, start, stop } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var grid = new DataGrid { ItemsSource = _rows, AutoGenerateColumns = true, IsReadOnly = true };
        AutomationProperties.SetAutomationId(grid, "BatchRunProfiles"); AutomationProperties.SetName(grid, Tr.F("Batch.ProfileListName"));
        var panel = new DockPanel { Margin = new Thickness(18) }; panel.Children.Add(buttons); panel.Children.Add(grid); Content = panel;
    }
    private async Task ExecuteAsync()
    {
        if (_cancel is not null) return; _cancel = new CancellationTokenSource();
        try
        {
            _summary.Text = Tr.Format("Batch.Running", _concurrency);
            var scheduler = new BatchScheduler(_concurrency);
            var results = await scheduler.RunAsync(_profiles.Select((p, i) => (Func<CancellationToken, Task<ProfileRunResult>>)(async token => { _rows[i].State = Tr.F("Batch.StateRunning"); try { var value = await _run(p, token); _rows[i].State = Tr.F("Batch.StateSucceeded"); return value; } catch (Exception ex) { _rows[i].State = Tr.Format("Batch.StateFailed", RcloneUiError.Describe(ex, "batch-run")); throw; } })), _cancel.Token);
            _summary.Text = Tr.Format("Batch.Finished", results.Count(x => x.Succeeded), results.Count(x => !x.Succeeded));
        }
        finally { _cancel.Dispose(); _cancel = null; }
    }
    private sealed class Row(SyncProfile profile) : INotifyPropertyChanged
    {
        private string _state = Tr.F("Batch.StateWaiting");
        public string Name => profile.Name; public string State { get => _state; set { _state = value; PropertyChanged?.Invoke(this, new(nameof(State))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
