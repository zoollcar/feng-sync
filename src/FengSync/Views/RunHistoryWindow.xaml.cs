using System.Windows;
using System.Windows.Controls;
using FengSync.Core;

namespace FengSync.Views;

/// <summary>Queryable durable run summaries, deliberately separate from recovery journals.</summary>
public partial class RunHistoryWindow : Window
{
    private readonly RunHistoryRepository _repository;
    private readonly string? _profileId;
    public RunHistoryWindow(string? profileId = null, RunHistoryRepository? repository = null)
    {
        // Initial selected values in XAML can raise SelectionChanged while
        // InitializeComponent is still running. Set the dependencies first so
        // the handler never attempts to refresh with a null repository.
        _profileId = profileId;
        _repository = repository ?? new RunHistoryRepository();
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }
    private async Task RefreshAsync()
    {
        // Either ComboBox can raise SelectionChanged during InitializeComponent,
        // before the other named controls have been created.
        if (OutcomeBox is null || PeriodBox is null || Entries is null) return;
        var outcome = OutcomeBox.SelectedIndex switch { 1 => RunOutcome.Succeeded, 2 => RunOutcome.PartialSuccess, 3 => RunOutcome.Failed, 4 => RunOutcome.Cancelled, _ => (RunOutcome?)null };
        var since = PeriodBox.SelectedIndex switch { 1 => DateTimeOffset.UtcNow.AddDays(-7), 2 => DateTimeOffset.UtcNow.AddDays(-30), _ => (DateTimeOffset?)null };
        Entries.ItemsSource = await _repository.QueryAsync(_profileId, outcome, since);
    }
    private async void FilterChanged(object sender, SelectionChangedEventArgs e) => await RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
}
