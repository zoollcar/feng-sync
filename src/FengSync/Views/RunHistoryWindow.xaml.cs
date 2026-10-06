using System.Windows;
using System.Windows.Controls;
using FengSync.Core;
using FengSync.Localization;

namespace FengSync.Views;

/// <summary>Queryable durable run summaries, deliberately separate from recovery journals.</summary>
public partial class RunHistoryWindow : Window
{
    private readonly RunHistoryRepository _repository;
    private readonly string? _profileId;
    private bool _refreshing;
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
        if (_refreshing || OutcomeBox is null || PeriodBox is null || Entries is null) return;
        _refreshing = true;
        var original = RefreshButton.Content;
        try
        {
            RefreshButton.IsEnabled = false; RefreshButton.Content = Tr.F("Run.Refreshing");
            var outcome = OutcomeBox.SelectedIndex switch { 1 => RunOutcome.Succeeded, 2 => RunOutcome.PartialSuccess, 3 => RunOutcome.Failed, 4 => RunOutcome.Cancelled, _ => (RunOutcome?)null };
            var since = PeriodBox.SelectedIndex switch { 1 => DateTimeOffset.UtcNow.AddDays(-7), 2 => DateTimeOffset.UtcNow.AddDays(-30), _ => (DateTimeOffset?)null };
            Entries.ItemsSource = (await _repository.QueryAsync(_profileId, outcome, since)).Select(RunHistoryDisplayRow.From).ToList();
        }
        finally { RefreshButton.IsEnabled = true; RefreshButton.Content = original; _refreshing = false; }
    }
    private async void FilterChanged(object sender, SelectionChangedEventArgs e) => await RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
}

internal sealed record RunHistoryDisplayRow(
    string ProfileId, string OutcomeText, string CompletedLocalText, int Planned, int Succeeded, int Failed,
    string TransferredText, string? Detail, string? FailureCategory, bool? FailureRetryable, string? CorrelationId)
{
    public static RunHistoryDisplayRow From(RunHistoryEntry entry) => new(
        entry.ProfileId,
        entry.Outcome switch { RunOutcome.Succeeded => Tr.F("Run.OutcomeSucceeded"), RunOutcome.PartialSuccess => Tr.F("Run.OutcomePartial"), RunOutcome.Failed => Tr.F("Run.OutcomeFailed"), RunOutcome.Cancelled => Tr.F("Run.OutcomeCancelled"), _ => entry.Outcome.ToString() },
        entry.CompletedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
        entry.Planned, entry.Succeeded, entry.Failed, FormatBytes(entry.TransferredBytes), entry.Detail,
        entry.FailureCategory, entry.FailureRetryable, entry.CorrelationId);

    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KB" : bytes < 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024:N1} MB" : $"{bytes / 1024d / 1024 / 1024:N2} GB";
}
