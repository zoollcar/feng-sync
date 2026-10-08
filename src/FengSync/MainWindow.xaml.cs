using FengSync.Core;
using FengSync.Core.Capabilities;
using FengSync.Core.Configuration;
using FengSync.Core.Updates;
using FengSync.Core.Execution;
using FengSync.Core.Scanning;
using FengSync.Core.SftpServer;
using FengSync.Localization;
using FengSync.Services;
using FluentIcon = FluentIcons.Common.Icon;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using System.IO;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Input;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using FengSync.Views;

namespace FengSync;
public partial class MainWindow : Window
{
    private readonly ObservableCollection<ComparisonRow> _rows = [];
    private readonly ObservableCollection<SyncProfile> _profiles = [];
    private readonly ProfileStore _profileStore = new();
    private readonly ApplicationVersionService _versionService = new();
    private UpdateCoordinator? _updates;
    private SyncPlan? _plan;
    private PlanSnapshot? _snapshot;
    private ComparisonSnapshot? _comparison;
    private IEndpoint? _left, _right;
    private RcloneRcClient? _rcloneClient;
    private ApplicationSettings _settings;
    private CancellationTokenSource? _syncCancellation;
    private CancellationTokenSource? _compareCancellation;
    private bool _syncInProgress;
    private bool _compareInProgress;
    private bool _closing;
    /// <summary>Created lazily with the shared queue, so a window built before settings load still works.</summary>
    private CloudCompletionNotifier? _cloudNotifier;
    /// <summary>Guards the one-shot restore of unfinished transfers from the previous session.</summary>
    private bool _cloudTransfersRestored;
    private ChangeSummary _lastSummary = ChangeSummary.Empty;
    private ICollectionView? _rowsView;
    // The single source of truth for endpoint header and title text — bound items must agree here.
    private string _filterKind = "All";

    private SyncMode SelectedMode => (SyncMode)Math.Max(0, SyncModeBox?.SelectedIndex ?? 0);
    public MainWindow()
    {
        InitializeComponent();
        // A desktop-free test run must not draw the shell on the user's screen, and nothing outside this
        // process can act before it has been drawn — so the window is moved out of the way here, before it
        // is ever shown. See TestWindowPlacement.
        TestWindowPlacement.Apply(this);
        Comparison.ItemsSource = _rows;
        ProfileList.ItemsSource = _profiles;
        Comparison.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler((_, _) => Dispatcher.BeginInvoke(RefreshSummary)));
        Comparison.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler((_, _) => Dispatcher.BeginInvoke(RefreshSummary)));
        _settings = new();
        _updates = new UpdateCoordinator(_versionService, new GitHubReleaseClient(), () => _settings, ApplyApplicationSettingsAsync, ExitForUpdateAsync);
        UpdateSettingsText();
        UpdateHeaderForCurrentProfile();
        UpdateComparisonEmptyState();
        Status.Text = Tr.F("Main.LoadingSettings");
        // E1: the cloud manager window can create a sync task, so this list is no longer the only writer of
        // profiles.json. Without this the user builds a task over there and comes back to a list that has
        // never heard of it — the file is right and the screen is stale, which reads as a lost task.
        SyncProfileNotifier.Saved += OnSyncProfileSaved;
        Closed += (_, _) => SyncProfileNotifier.Saved -= OnSyncProfileSaved;
        Loaded += async (_, _) => await InitializeAsync();
    }

    /// <summary>Re-reads the list after somebody else saved a profile, and adopts the new one.</summary>
    private void OnSyncProfileSaved(SyncProfile saved)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await LoadProfilesAsync();
                ProfileList.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == saved.Id);
                Status.Text = Tr.Format("Main.ProfileSyncedFromCloud", saved.Name);
            }
            catch (Exception ex) { Status.Text = Tr.Format("Main.ProfileRefreshFailed", RcloneUiError.Describe(ex, "profile-list-refresh")); }
        });
    }

    private void ApplyRowFilter()
    {
        _rowsView ??= CollectionViewSource.GetDefaultView(_rows);
        if (_rowsView is null) return;
        var filter = _filterKind;
        _rowsView.Filter = obj =>
        {
            if (obj is not ComparisonRow row) return false;
            return filter switch
            {
                "Upload" => row.OperationLabel == Tr.F("Main.KindUpload"),
                "Download" => row.OperationLabel == Tr.F("Main.KindDownload"),
                "Delete" => row.OperationLabel == Tr.F("Main.KindDelete"),
                "Conflict" => row.Operation.IsConflict && !row.IsIgnored && !row.IsFilterExcluded,
                "Ignored" => row.IsIgnored || row.IsFilterExcluded,
                _ => true
            };
        };
    }

    private void SummaryFilter_Click(object sender, RoutedEventArgs e)
    {
        var requested = (sender as FrameworkElement)?.Tag as string ?? "All";
        _filterKind = requested == "All" || requested == _filterKind ? "All" : requested;
        ApplyRowFilter();
        UpdateSummaryFilterButtons();
        var label = _filterKind switch { "Upload" => Tr.F("Main.KindUpload"), "Download" => Tr.F("Main.KindDownload"), "Delete" => Tr.F("Main.KindDelete"), "Conflict" => Tr.F("Main.KindConflict"), _ => Tr.F("Main.KindAll") };
        Status.Text = Tr.Format("Main.FilterStatus", label);
    }

    private void UpdateSummaryFilterButtons()
    {
        if (UploadFilterButton is null) return;
        foreach (var (button, kind) in new[]
        {
            (UploadFilterButton, "Upload"), (DownloadFilterButton, "Download"),
            (DeleteFilterButton, "Delete"), (ConflictFilterButton, "Conflict"),
            (ChangeSummaryTotal, "All")
        })
        {
            var active = _filterKind == kind || (_filterKind == "All" && kind == "All");
            if (active) button.Background = (Brush)FindResource("AccentSubtleBackgroundBrush");
            else button.ClearValue(Button.BackgroundProperty);
            button.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "BorderSubtleBrush");
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ==== Top-level actions =================================================================
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_compareInProgress || _syncInProgress) return;
        _compareInProgress = true;
        _compareCancellation = new();
        var token = _compareCancellation.Token;
        UpdateActionButtons();
        try
        {
            Status.Text = Tr.Format("Main.PreparingComparison", LeftPath.Text, RightPath.Text);
            await BuildPlanAsync(token);
        }
        catch (OperationCanceledException) { Status.Text = Tr.F("Main.CompareCancelled"); }
        catch (Exception ex) { SyncButton.IsEnabled = false; Status.Text = RcloneUiError.Describe(ex, "comparison"); }
        finally
        {
            _compareCancellation?.Dispose();
            _compareCancellation = null;
            _compareInProgress = false;
            _ = _updates?.CheckDeferredAsync(this, _syncInProgress || _compareInProgress);
            UpdateActionButtons();
        }
    }
    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_syncInProgress || _compareInProgress || _plan is null || _left is null || _right is null) return;
        ProgressWindow? progressDialog = null;
        var attemptedProfile = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create(Tr.F("Main.TemporaryProfile"), LeftPath.Text, RightPath.Text);
        _syncInProgress = true;
        UpdateActionButtons();
        try
        {
            var effective = CurrentSettings; Comparison.CommitEdit(DataGridEditingUnit.Cell, true); Comparison.CommitEdit(DataGridEditingUnit.Row, true);
            var operations = _rows.Select(x => x.Operation).ToList(); var current = new SyncPlan(operations);
            if (!current.CanExecute || _snapshot is null) { Status.Text = Tr.F("Main.ResolveConflictsFirst"); return; }
            var profile = attemptedProfile;
            _syncCancellation = new();
            progressDialog = new ProgressWindow(operations, !_settings.ShowCompleted) { Owner = this };
            progressDialog.Show();
            progressDialog.ShowInitialization("1 / 5", Tr.F("Main.SyncPreparingPlan"));
            Status.Text = Tr.F("Main.SyncPreparing");

            progressDialog.ShowInitialization("2 / 5", Tr.F("Main.SyncConfirmingSnapshot"));
            var leftEntries = _snapshot.LeftEntries;
            var rightEntries = _snapshot.RightEntries;
            progressDialog.ShowInitialization("3 / 5", Tr.F("Main.SyncSafetyChecks"));
            var safety = new SafetyValidator().ValidatePlan(current, leftEntries.Count, rightEntries.Count, SelectedMode, profile.MaxDeletes, profile.MaxDeleteRatio)
                .Combine(new SafetyValidator().ValidateCapacity(current, leftEntries, rightEntries, _left, _right));
            var risk = SyncRiskSummary.Create(current, leftEntries, rightEntries);
            var thresholdOverride = SyncConfirmationPolicy.CanOverrideWithProfileName(safety);
            if (safety.HasBlockingIssues && !thresholdOverride) { var message = string.Join(" ", safety.Issues.Select(x => x.Message)); Status.Text = message; progressDialog.Complete(false, message); await RecordManualRunAsync(profile, RunOutcome.Failed, message); return; }
            if (SyncConfirmationPolicy.RequiresConfirmation(risk) || thresholdOverride)
            {
                progressDialog.ShowInitialization("4 / 5", Tr.F("Main.SyncAwaitingConfirmation"));
                var confirmation = new SyncConfirmationWindow(risk, safety, profile.Name, risk.TransferBytes) { Owner = this };
                if (confirmation.ShowDialog() != true) { Status.Text = Tr.F("Main.SyncConfirmationCancelled"); progressDialog.Complete(new SyncRunResult(Guid.NewGuid(), []), Tr.F("Main.SyncConfirmationCancelled"), cancelled: true); await RecordManualRunAsync(profile, RunOutcome.Cancelled, Tr.F("Main.SyncConfirmationCancelled")); return; }
            }
            progressDialog.ShowInitialization("5 / 5", Tr.F("Main.SyncBuildingBaseline"));
            Status.Text = Tr.Format("Main.SyncConcurrent", effective.MaxConcurrentCopies);
            var baselineRepository = new BaselineRepository(); var transaction = await baselineRepository.BeginAsync(_left, _right, _syncCancellation.Token);
            progressDialog.BeginTransfers(effective.MaxConcurrentCopies);
            var run = await new SyncExecutorV2().ExecuteAsync(_snapshot, _left, _right, new Progress<TransferProgress>(p => progressDialog.Report(p)), _syncCancellation.Token, effective.VerifyCopies, effective.Versioning, journals: new TaskJournalStore(), maxConcurrentCopies: effective.MaxConcurrentCopies, resumeMinimumBytes: effective.ResumeMinimumBytes);
            transaction = run.Operations.Where(x => x.Stage == TransferStage.Committed).Aggregate(transaction, (current, item) => current.RecordCommitted(item.Path));
            BaselineCommitResult? baselineCommit = null;
            if (_comparison is not null && run.SucceededOperations > 0)
                baselineCommit = await baselineRepository.CommitFromResultsAsync(_left, _right,
                    new BaselineCommitInput(_comparison, run.Operations.ToDictionary(x => x.OperationId), transaction), _syncCancellation.Token);
            transaction = run.Succeeded ? transaction.Complete() : transaction.Rollback(needsRecovery: true);
            await baselineRepository.SaveAsync(transaction, _syncCancellation.Token);
            progressDialog.SetRetry(operations, async retryPlan =>
            {
                var retrySnapshot = await PlanSnapshot.CaptureAsync(retryPlan, _left, _right);
                return await new SyncExecutorV2().ExecuteAsync(retrySnapshot, _left, _right, new Progress<TransferProgress>(p => progressDialog.Report(p)), CancellationToken.None, effective.VerifyCopies, effective.Versioning, journals: new TaskJournalStore(), maxConcurrentCopies: effective.MaxConcurrentCopies, resumeMinimumBytes: effective.ResumeMinimumBytes);
            });
            if (!run.Succeeded)
            {
                var outcome = run.SucceededOperations > 0 ? RunOutcome.PartialSuccess : RunOutcome.Failed;
                await AppendRunHistoryAsync(profile, operations, run, outcome, Tr.F("Main.SyncHasFailures"));
                SetLastRun(profile, DateTimeOffset.UtcNow);
                Status.Text = Tr.Format("Main.SyncPartiallyFailed", run.FailedOperations);
                LastRunStatus.Text = Tr.Format("Main.LastRun", DateTime.Now);
                progressDialog.Complete(run, Tr.Format("Main.SyncFailedDetail", run.FailedOperations));
                return;
            }
            await AppendRunHistoryAsync(profile, operations, run, RunOutcome.Succeeded, null);
            SetLastRun(profile, DateTimeOffset.UtcNow);
            Status.Text = baselineCommit?.Status == BaselineCommitStatus.Unchanged ? Tr.F("Main.SyncDoneBaselineUnchanged") : Tr.F("Main.SyncDoneBaselineWritten");
            LastRunStatus.Text = Tr.Format("Main.LastRun", DateTime.Now);
            progressDialog.Complete(run, Tr.F("Main.SyncDone"));
        }
        catch (OperationCanceledException) when (_syncCancellation?.IsCancellationRequested == true)
        {
            Status.Text = Tr.F("Main.SyncCancelled");
            progressDialog?.Complete(new SyncRunResult(Guid.NewGuid(), []), Tr.F("Main.SyncCancelled"), cancelled: true);
            await RecordManualRunAsync(attemptedProfile, RunOutcome.Cancelled, Tr.F("Main.SyncCancelled"));
        }
        catch (OperationCanceledException ex)
        {
            var message = Tr.F("Main.SyncFailedInterrupted") + RcloneUiError.Describe(ex, "sync-aggregate");
            Status.Text = message;
            progressDialog?.Complete(false, message);
            await RecordManualRunAsync(attemptedProfile, RunOutcome.Failed, message);
        }
        catch (Exception ex)
        {
            var detail = RcloneUiError.Describe(ex, "sync-run");
            Status.Text = Tr.F("Main.SyncFailedPrefix") + detail;
            progressDialog?.Complete(false, detail);
            await RecordManualRunAsync(attemptedProfile, RunOutcome.Failed, detail, ex);
        }
        finally { _syncCancellation?.Dispose(); _syncCancellation = null; _syncInProgress = false; RefreshSummary(); UpdateActionButtons(); _ = _updates?.CheckDeferredAsync(this, _syncInProgress || _compareInProgress); }
    }
    private static Task AppendRunHistoryAsync(SyncProfile profile, IReadOnlyCollection<SyncOperation> operations, SyncRunResult run, RunOutcome outcome, string? detail)
        => new RunHistoryRepository().AppendAsync(new RunHistoryEntry(profile.Id, outcome, DateTimeOffset.UtcNow, operations.Count(x => x.Selected), run.SucceededOperations, run.FailedOperations, run.Operations.Sum(x => x.BytesTransferred), detail, run.RunId));
    private async Task RecordManualRunAsync(SyncProfile profile, RunOutcome outcome, string? detail, Exception? exception = null)
    {
        var timestamp = DateTimeOffset.UtcNow;
        await new RunHistoryRepository().AppendAsync(RunHistoryEntry.FromFailure(profile.Id, outcome, timestamp, detail, exception), CancellationToken.None);
        SetLastRun(profile, timestamp);
    }
    private void SetLastRun(SyncProfile profile, DateTimeOffset timestamp)
    {
        var index = _profiles.IndexOf(profile);
        if (index < 0) return;
        // Replacing an item in an ObservableCollection can clear WPF's current
        // selection synchronously. Capture the selection first, then restore it
        // to the updated record so the endpoint workspace stays usable after a run.
        var wasSelected = (ProfileList.SelectedItem as SyncProfile)?.Id == profile.Id;
        var updated = profile with { LastRunUtc = timestamp };
        _profiles[index] = updated;
        if (wasSelected) ProfileList.SelectedItem = updated;
    }
    private void ApplyDirection(IEnumerable<ComparisonRow> rows, bool keepLeft)
    {
        var requested = rows.DistinctBy(x => x.Operation.OperationId).ToList();
        var reversingDirectories = requested.Where(x =>
            (x.Operation.Kind == OperationKind.CreateRightDirectory && !keepLeft) ||
            (x.Operation.Kind == OperationKind.CreateLeftDirectory && keepLeft)).ToList();
        var filteredDescendant = _rows.FirstOrDefault(candidate => candidate.IsFilterExcluded &&
            reversingDirectories.Any(root => DirectionOverrideExpander.IsDescendant(candidate.Operation.Path, root.Operation.Path)));
        if (filteredDescendant is not null)
        {
            Status.Text = Tr.Format("Main.ReverseDeleteBlocked", filteredDescendant.Operation.Path);
            return;
        }

        var expanded = DirectionOverrideExpander.Expand(_rows.Select(x => x.Operation), requested.Select(x => x.Operation), keepLeft);
        var expandedIds = expanded.Select(x => x.OperationId).ToHashSet();
        var affectedRows = _rows.Where(x => expandedIds.Contains(x.Operation.OperationId)).ToList();
        var linked = Math.Max(0, affectedRows.Count - requested.Count);
        var changed = 0; var errors = new List<string>();
        foreach (var row in affectedRows)
        {
            try
            {
                // Reversing a directory is an explicit decision about its whole
                // subtree, so temporary per-row ignores within that subtree must
                // be re-enabled to make the parent deletion achievable.
                row.IsIgnored = false;
                row.Operation.Selected = true;
                row.Operation.OverrideCopyDirection(keepLeft, row.Left, row.Right);
                row.Refresh(); changed++;
            }
            catch (Exception ex) { errors.Add($"{row.Operation.Path}：{RcloneUiError.Describe(ex, "operation-retry")}"); }
        }
        Comparison.Items.Refresh(); RefreshSummary();
        var linkage = linked > 0 ? Tr.Format("Main.LinkedFolderItems", linked) : "";
        Status.Text = errors.Count == 0 ? Tr.Format("Main.OverwriteSet", changed, (keepLeft ? Tr.F("Main.KeepLeft") : Tr.F("Main.KeepRight")), linkage) : Tr.Format("Main.OverwriteChanged", changed, string.Join(" ", errors));
    }
    private void ActionMenu_KeepLeft_Click(object sender, RoutedEventArgs e) => ApplyActionFromMenu(sender, true);
    private void ActionMenu_KeepRight_Click(object sender, RoutedEventArgs e) => ApplyActionFromMenu(sender, false);
    private void ComparisonActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }
    private void ActionMenu_Ignore_Click(object sender, RoutedEventArgs e) => SetIgnoredFromMenu(sender, true);
    private void ActionMenu_Enable_Click(object sender, RoutedEventArgs e) => SetIgnoredFromMenu(sender, false);
    private void ApplyActionFromMenu(object sender, bool keepLeft)
    {
        if (GetRowFromMenu(sender) is not ComparisonRow row) { Status.Text = Tr.F("Main.SelectRowsToOverwrite"); return; }
        var rows = Comparison.SelectedItems.Cast<ComparisonRow>().Contains(row) ? Comparison.SelectedItems.Cast<ComparisonRow>() : [row];
        ApplyDirection(rows, keepLeft);
    }
    private void SetIgnoredFromMenu(object sender, bool ignored)
    {
        if (GetRowFromMenu(sender) is not ComparisonRow row) { Status.Text = Tr.F("Main.SelectRowsToChange"); return; }
        if (row.IsFilterExcluded) { Status.Text = Tr.F("Main.FileFilteredByProfile"); return; }
        if (row.IsIgnored == ignored) { Status.Text = ignored ? Tr.F("Main.FileIgnored") : Tr.F("Main.FileEnabled"); return; }
        row.IsIgnored = ignored;
        row.Operation.Selected = !ignored;
        row.Refresh(); Comparison.Items.Refresh(); RefreshSummary();
        Status.Text = ignored ? Tr.F("Main.FileTemporarilyIgnored") : Tr.F("Main.FileReenabled");
    }
    private static ComparisonRow? GetRowFromMenu(object sender)
    {
        if (sender is MenuItem { DataContext: ComparisonRow direct }) return direct;
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement target } } && target.DataContext is ComparisonRow viaTarget) return viaTarget;
        return null;
    }

    // ==== ChangeSummary ============================================================
    private ChangeSummary BuildSummary()
    {
        // Selected only — totals reflect the executable subset, not every difference.
        var copyLeftSelected = _rows.Where(x => x.Selected && x.Operation.Kind is OperationKind.CopyLeftToRight).ToList();
        var copyRightSelected = _rows.Where(x => x.Selected && x.Operation.Kind is OperationKind.CopyRightToLeft).ToList();
        var deleteSelected = _rows.Where(x => x.Selected && x.Operation.Kind is OperationKind.DeleteLeft or OperationKind.DeleteRight).ToList();

        long uploadBytes = copyLeftSelected.Sum(x => x.Left?.Fingerprint?.Size ?? 0);
        long downloadBytes = copyRightSelected.Sum(x => x.Right?.Fingerprint?.Size ?? 0);

        var deleteBytes = deleteSelected.Sum(x => x.Operation.Kind == OperationKind.DeleteLeft ? (x.Left?.Fingerprint?.Size ?? 0) : (x.Right?.Fingerprint?.Size ?? 0));
        var deleteBytesAnyKnown = deleteSelected.Any(x => x.Operation.Kind == OperationKind.DeleteLeft ? x.Left?.Fingerprint?.Size is not null : x.Right?.Fingerprint?.Size is not null);
        long? deleteBucket = deleteSelected.Count == 0 ? 0 : (deleteBytesAnyKnown ? (long?)deleteBytes : null);

        // Conflict counting: ignore rows that the user dismissed; size uses the larger known side, "0 B" when there are no conflicts.
        var conflictRows = _rows.Where(x => x.Operation.IsConflict && !x.IsIgnored && !x.IsFilterExcluded).ToList();
        long conflictBytes = conflictRows.Sum(x => Math.Max(x.Left?.Fingerprint?.Size ?? 0, x.Right?.Fingerprint?.Size ?? 0));
        var conflictBucket = new ChangeBucket(conflictRows.Count, conflictBytes);

        // Total: deduplicated operation count and only upload + download transfer bytes.
        var opsForTotal = _rows.Where(x => x.Selected).Select(x => x.Operation.OperationId).Distinct().Count();
        var totalBucket = new ChangeBucket(opsForTotal, uploadBytes + downloadBytes);

        return new ChangeSummary(
            new(copyLeftSelected.Count, uploadBytes),
            new(copyRightSelected.Count, downloadBytes),
            new(deleteSelected.Count, deleteBucket),
            conflictBucket,
            totalBucket);
    }

    private void RefreshSummary()
    {
        var summary = BuildSummary();
        _lastSummary = summary;
        UploadSummary.Text = Tr.Format("Main.SummaryUpload", summary.Upload.Count);
        UploadSizeSummary.Text = FormatBytes(summary.Upload.Bytes);
        DownloadSummary.Text = Tr.Format("Main.SummaryDownload", summary.Download.Count);
        DownloadSizeSummary.Text = FormatBytes(summary.Download.Bytes);
        DeleteSummary.Text = Tr.Format("Main.SummaryDelete", summary.Delete.Count);
        DeleteSizeSummary.Text = summary.Delete.Bytes is null ? "—" : FormatBytes(summary.Delete.Bytes);
        ConflictSummary.Text = Tr.Format("Main.SummaryConflict", summary.Conflict.Count);
        ConflictSizeSummary.Text = FormatBytes(summary.Conflict.Bytes);
        TotalSummary.Text = Tr.Format("Main.SummaryTotal", summary.Total.Count);
        TotalSizeSummary.Text = FormatBytes(summary.Total.Bytes);

        SelectedLabel.Text = _rows.Count(x => x.Selected).ToString();
        UpdateSummaryFilterButtons();
        UpdateComparisonEmptyState();
        UpdateActionButtons();
    }

    private record SafetyState(string Headline, string Description, string Severity);
    private SafetyState _safetySummary = new("", Tr.F("Main.PickEndpointsToCompareFull"), "Neutral");

    private void UpdateActionButtons()
    {
        if (CompareButton is null || SyncButton is null) return;
        var profile = ProfileList?.SelectedItem as SyncProfile ?? SyncProfile.Create(Tr.F("Main.TemporaryProfile"), LeftPath?.Text ?? "", RightPath?.Text ?? "");
        var canRun = new FeatureCapabilityService().Evaluate(profile with { LeftPath = LeftPath?.Text ?? "", RightPath = RightPath?.Text ?? "", Mode = SelectedMode }).CanRun;
        var hasProfile = ProfileList?.SelectedItem is SyncProfile selectedProfile && !string.IsNullOrWhiteSpace(selectedProfile.Name);
        CompareButton.IsEnabled = canRun && !_closing && !_compareInProgress && !_syncInProgress && hasProfile;
        var planUsable = _plan is not null && new SyncPlan(_rows.Select(x => x.Operation).ToList()).CanExecute && !_safetySummary.Headline.Contains(Tr.F("Main.KindBlocked"));
        SyncButton.IsEnabled = canRun && !_compareInProgress && !_syncInProgress && planUsable;
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null) return "—";
        var b = bytes.Value;
        return b switch
        {
            < 1024 => $"{b:N0} B",
            < 1024 * 1024 => $"{b / 1024d:N1} KB",
            < 1024L * 1024 * 1024 => $"{b / 1024d / 1024:N1} MB",
            _ => $"{b / 1024d / 1024 / 1024:N2} GB"
        };
    }

    // ==== Endpoint bar handlers ===================================================
    private void BrowseLeft_Click(object s, RoutedEventArgs e) => Browse(LeftPath);
    private void BrowseRight_Click(object s, RoutedEventArgs e) => Browse(RightPath);
    private void Browse(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() == true) ChangeEndpointPath(target, dialog.FolderName);
    }
    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        (LeftPath.Text, RightPath.Text) = (RightPath.Text, LeftPath.Text);
        UpdateEndpointHeaders();
        if (_plan is not null) await RecompareAsync();
    }

    private void ChangeEndpointPath(System.Windows.Controls.TextBox target, string value)
    {
        target.Text = value;
        UpdateEndpointHeaders();
        UpdateActionButtons();
    }
    private void Comparison_CurrentCellChanged(object s, EventArgs e) => Dispatcher.BeginInvoke(RefreshSummary);
    private void UpdateComparisonEmptyState()
    {
        if (ComparisonEmptyState is null) return;
        var visible = _rows.Count == 0;
        ComparisonEmptyState.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (ComparisonEmptyStateText is not null)
            ComparisonEmptyStateText.Text = _plan is null
                ? Tr.F("Main.EmptyPickEndpoints")
                : Tr.F("Main.NoDifferences");
    }
    private void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _settings = _settings with { MainWindowSidebarWidth = ClampSidebarWidth(SidebarColumn.ActualWidth) };
    }
    private void SidebarSplitter_KeyDown(object sender, KeyEventArgs e)
    {
        var delta = e.Key switch { Key.Left => -12d, Key.Right => 12d, _ => 0d };
        if (delta == 0) return;
        var width = ClampSidebarWidth(SidebarColumn.ActualWidth + delta);
        SidebarColumn.Width = new GridLength(width);
        _settings = _settings with { MainWindowSidebarWidth = width };
        e.Handled = true;
    }
    private static double ClampSidebarWidth(double value) => double.IsNaN(value) || double.IsInfinity(value) ? 248 : Math.Clamp(value, 220, 320);
    private EffectiveProfileSettings CurrentSettings => EffectiveProfileSettings.Resolve(ProfileList?.SelectedItem as SyncProfile ?? SyncProfile.Create(Tr.F("Main.ModeDefault"), "", ""), _settings);
    private void UpdateSettingsText() { if (ConcurrencyLabel is not null) ConcurrencyLabel.Text = CurrentSettings.MaxConcurrentCopies + Tr.F("Main.ConcurrentSuffix"); }
    private void SyncMode_Changed(object s, SelectionChangedEventArgs e) { if (_plan is not null) Compare_Click(this, new RoutedEventArgs()); }
    private string ModeTitle() => SelectedMode switch { SyncMode.Mirror => Tr.F("Main.ModeMirrorArrow"), SyncMode.Update => Tr.F("Main.ModeUpdateArrow"), SyncMode.Custom => Tr.F("Main.ModeCustomArrow"), _ => Tr.F("Main.ModeTwoWayArrow") };
    private static string SettingsPath => Path.Combine(AppDataPaths.Root, "FengSync.local.json");
    private async Task InitializeAsync()
    {
        try
        {
            var loaded = await new SettingsStore().LoadAsync();
            _settings = loaded.Settings;
            ApplyCloudTransferSettings(_settings);
            SidebarColumn.Width = new GridLength(ClampSidebarWidth(_settings.MainWindowSidebarWidth));
            UpdateSettingsText();
            UpdateEndpointHeaders();
            Status.Text = loaded.RecoveredFromCorruption
                ? Tr.Format("Main.SettingsCorrupt", loaded.BackupPath)
                : loaded.Migrated
                    ? Tr.Format("Main.SettingsMigrated", loaded.MigratedFromSchemaVersion, loaded.MigrationBackupPath)
                    : Tr.F("Main.PickEndpointsToComparePeriod");
        }
        catch (Exception ex) { _settings = new(); Status.Text = Tr.F("Main.SettingsLoadFailed") + ex.Message; }
        await RestoreCloudTransfersAsync(_settings);
        await LoadProfilesAsync();
        if (App.CurrentApp.UpdatedFromVersion is { Length: > 0 } from && App.CurrentApp.UpdateTaskDirectory is { Length: > 0 } task)
        {
            Status.Text = Tr.Format("Main.UpdatedFrom", from, _versionService.DisplayVersion);
            try { await File.WriteAllTextAsync(Path.Combine(Path.GetFullPath(task), "success"), "ok"); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Unable to confirm update: " + ex.Message); }
        }
        _ = Dispatcher.InvokeAsync(() => _ = _updates?.CheckAsync(this, manual: false, _syncInProgress || _compareInProgress));
    }
    private Task RecompareAsync() { Compare_Click(this, new RoutedEventArgs()); return Task.CompletedTask; }
    private async Task LoadProfilesAsync()
    {
        _profiles.Clear(); foreach (var item in await _profileStore.LoadAsync()) _profiles.Add(item);
        var history = new RunHistoryRepository();
        for (var index = 0; index < _profiles.Count; index++)
        {
            var profile = _profiles[index];
            var lastRun = (await history.QueryAsync(profile.Id)).FirstOrDefault();
            _profiles[index] = profile with { LastRunUtc = lastRun?.CompletedUtc };
        }
        if (_profiles.Count == 0) _profiles.Add(SyncProfile.Create(Tr.F("Main.UnnamedProfile"), "", ""));
        var saved = _profiles.ToList().FindIndex(x => x.Id == _settings.LastSelectedProfileId); ProfileList.SelectedIndex = saved >= 0 ? saved : 0;
    }

    private async void NewProfile_Click(object s, RoutedEventArgs e)
    {
        var profile = SyncProfile.Create(Tr.F("Main.UnnamedProfileSuffix") + (_profiles.Count + 1), "", ""); _profiles.Add(profile); ProfileList.SelectedItem = profile; await PersistProfilesAsync(); Status.Text = Tr.F("Main.ProfileCreated");
    }
    private async void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not SyncProfile original) { Status.Text = Tr.F("Main.SelectProfileToCopy"); return; }
        var copy = original with { Id = Guid.NewGuid().ToString("N"), Name = original.Name + Tr.F("Main.ProfileCopySuffix") };
        _profiles.Add(copy); ProfileList.SelectedItem = copy; await PersistProfilesAsync();
        Status.Text = Tr.Format("Main.ProfileCopiedAs", copy.Name);
    }
    private async void DeleteProfile_Click(object s, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not SyncProfile profile) { Status.Text = Tr.F("Main.SelectProfileToRemove"); return; }
        if (MessageBox.Show(Tr.Format("Main.ConfirmRemoveProfile", profile.Name), Tr.F("Main.RemoveProfile"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _profiles.Remove(profile);
        if (_profiles.Count == 0) _profiles.Add(SyncProfile.Create(Tr.F("Main.UnnamedProfile"), "", ""));
        ProfileList.SelectedIndex = 0; await PersistProfilesAsync(); Status.Text = Tr.F("Main.ProfileRemoved");
    }
    private async void EditProfile_Click(object s, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not SyncProfile original) { Status.Text = Tr.F("Main.SelectAProfile"); return; }
        var editable = original with { LeftPath = LeftPath.Text, RightPath = RightPath.Text, Mode = SelectedMode };
        var updated = new ProfileDialogService().Edit(this, editable, _settings);
        if (updated is null) return;
        var index = _profiles.IndexOf(original);
        if (index >= 0) _profiles[index] = updated; else _profiles.Add(updated);
        ProfileList.SelectedItem = updated;
        await PersistProfilesAsync();
        ApplyProfile(updated);
        Status.Text = Tr.F("Main.ProfileSettingsSaved");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings, ApplyApplicationSettingsAsync, ShowSftpServerSettingsAsync, CleanupExpiredLocalTemporaryFilesAsync, ProfileList.SelectedItem as SyncProfile) { Owner = this };
        dialog.ShowDialog();
    }

    private async void ExportPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) { Status.Text = Tr.F("Main.NoChangesToExport"); return; }
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
            FileName = "feng-sync-plan.csv",
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            // Always export the current view (filter applied); never include credentials.
            var visible = Comparison.Items.Cast<ComparisonRow>().ToList();
            if (string.Equals(System.IO.Path.GetExtension(dialog.FileName), ".json", StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonSerializer.Serialize(visible.Select(r => new
                {
                    r.Name,
                    r.RelativePath,
                    Path = r.Operation.Path,
                    Operation = r.OperationLabel,
                    Direction = r.DirectionDisplay,
                    SizeBytes = r.OperationSize,
                    ModifiedUtc = r.ModifiedUtc,
                    Selected = r.Selected
                }));
                await File.WriteAllTextAsync(dialog.FileName, json);
            }
            else
            {
                var lines = new List<string> { "Name,RelativePath,Path,Operation,Direction,Size,Modified,Selected" };
                foreach (var r in visible)
                {
                    lines.Add($"{Escape(r.Name)},{Escape(r.RelativePath)},{Escape(r.Operation.Path)},{Escape(r.OperationLabel)},{Escape(r.DirectionDisplay)},{r.OperationSize?.ToString() ?? ""},{r.ModifiedUtc?.ToString("yyyy-MM-dd HH:mm") ?? ""},{(r.Selected ? "true" : "false")}");
                }
                await File.WriteAllLinesAsync(dialog.FileName, lines);
            }
            Status.Text = Tr.Format("Main.ChangesExported", visible.Count, dialog.FileName);
        }
        catch (Exception ex) { Status.Text = Tr.F("Main.ExportFailed") + ex.Message; }
    }
    private static string Escape(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    private void ProfileList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        UpdateHeaderForCurrentProfile();
        if (ProfileList.SelectedItem is SyncProfile profile) { _settings = _settings with { LastSelectedProfileId = profile.Id }; ApplyProfile(profile); }
    }
    private void UpdateHeaderForCurrentProfile()
    {
        var profile = ProfileList.SelectedItem as SyncProfile;
        CurrentProfileTitle.Text = profile is null ? Tr.F("Main.NoProfileSelected") : profile.Name;
        if (ProfileContextStatus != null) ProfileContextStatus.Text = profile is null ? Tr.F("Main.ProfileWorkspace") : Tr.Format("Main.Loaded", profile.Name);
        // Show a relative description under the title — defaults to a friendly hint.
        if (LastRunStatus != null && profile is not null)
            LastRunStatus.Text = profile.LastRunUtc is not null
                ? Tr.Format("Main.LastRun", profile.LastRunUtc.Value.ToLocalTime())
                : string.IsNullOrEmpty(profile.LeftPath) && string.IsNullOrEmpty(profile.RightPath)
                    ? Tr.F("Main.PickEndpointsBelow")
                    : Tr.F("Main.TwoWayReadyTitle");
    }
    private void ApplyProfile(SyncProfile profile)
    {
        if (LeftPath != null) LeftPath.Text = profile.LeftPath;
        if (RightPath != null) RightPath.Text = profile.RightPath;
        if (SyncModeBox != null) SyncModeBox.SelectedIndex = (int)profile.Mode;
        UpdateSettingsText();
        UpdateEndpointHeaders();
        UpdateHeaderForCurrentProfile();
        var compatibility = new FeatureCapabilityService().Evaluate(profile);
        UpdateActionButtons();
        Status.Text = compatibility.CanRun ? Tr.F("Main.Ready") : Tr.Format("Main.ProfileNeedsRepair", compatibility.Summary);
    }
    private void UpdateEndpointHeaders()
    {
        var (leftTitle, leftIcon) = DescribeEndpoint(LeftPath?.Text);
        var (rightTitle, rightIcon) = DescribeEndpoint(RightPath?.Text);
        if (LeftEndpointTitle != null) LeftEndpointTitle.Text = leftTitle;
        if (LeftEndpointIcon != null) LeftEndpointIcon.Icon = leftIcon;
        if (RightEndpointTitle != null) RightEndpointTitle.Text = rightTitle;
        if (RightEndpointIcon != null) RightEndpointIcon.Icon = rightIcon;
    }
    private void EndpointPath_TextChanged(object sender, TextChangedEventArgs e) => UpdateEndpointHeaders();
    private static (string Title, FluentIcon Icon) DescribeEndpoint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (Tr.F("Main.NoEndpoint"), FluentIcon.Folder);
        if (EndpointSchemes.SchemeOf(path) is not { } scheme) return (Tr.F("SyncWizard.LocalFolder"), FluentIcon.Folder);
        var label = EndpointSchemes.TypeForScheme(scheme) is { } type
            ? EndpointSchemes.DisplayNameFor(type)
            : scheme.ToUpperInvariant();
        // "S3 Bucket" reads as a heading for a bucket rather than for the endpoint, so the header keeps
        // the short form the profile list has always shown.
        return (label == "S3 Bucket" ? "S3" : label, FluentIcon.Cloud);
    }
    private async Task BuildPlanAsync(CancellationToken cancellationToken)
    {
        var profile = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create(Tr.F("Main.TemporaryProfile"), LeftPath.Text, RightPath.Text) with { Mode = SelectedMode }; var compatibility = new FeatureCapabilityService().Evaluate(profile with { LeftPath = LeftPath.Text, RightPath = RightPath.Text, Mode = SelectedMode }); if (!compatibility.CanRun) throw new InvalidOperationException(Tr.F("Main.ProfileNeedsRepairPrefix") + compatibility.Summary); var effective = CurrentSettings; (_left, _right) = await CreateEndpointsAsync(LeftPath.Text, RightPath.Text);
        var configurationSafety = _left is LocalEndpoint configLeft && _right is LocalEndpoint configRight ? new SafetyValidator().ValidateConfiguration(configLeft.Root, configRight.Root, effective.Versioning?.ArchiveDirectory) : SafetyValidationResult.Pass;
        if (configurationSafety.HasBlockingIssues) throw new InvalidOperationException(string.Join(" ", configurationSafety.Issues.Select(x => x.Message)));
        Status.Text = Tr.Format("Main.ScanningEndpoints", LeftPath.Text, RightPath.Text);
        var leftProgress = new Progress<ScanProgress>(x => Status.Text = x.Completed ? Tr.Format("Main.LeftScanDone", x.ItemsScanned) : Tr.Format("Main.ScanningLeft", x.ItemsScanned, (string.IsNullOrEmpty(x.CurrentPath) ? "" : " · " + x.CurrentPath)));
        var rightProgress = new Progress<ScanProgress>(x => Status.Text = x.Completed ? Tr.Format("Main.RightScanDone", x.ItemsScanned) : Tr.Format("Main.ScanningRight", x.ItemsScanned, (string.IsNullOrEmpty(x.CurrentPath) ? "" : " · " + x.CurrentPath)));
        var scans = await Task.WhenAll(_left.ScanAsync(leftProgress, cancellationToken), _right.ScanAsync(rightProgress, cancellationToken));
        Status.Text = Tr.Format("Main.ScanDone", scans[0].Count, scans[1].Count);
        var left = scans[0].ToDictionary(x => x.Path, _left.Capabilities.EffectivePaths.CreateComparer()); var right = scans[1].ToDictionary(x => x.Path, _right.Capabilities.EffectivePaths.CreateComparer()); var baselineRepository = new BaselineRepository(); var baselineLoad = await baselineRepository.LoadDetailedAsync(_left, _right); var baseline = baselineLoad.CanPropagateDeletes ? baselineLoad.Entries : null; var baselineWarning = baselineLoad.Warning;
        _plan = new ModePlanner().Build(SelectedMode, left.Values, right.Values, baseline, SyncFilter.Empty, _left.Capabilities, _right.Capabilities);
        var filter = effective.Filter.CreateEngine();
        var ignoredPaths = new HashSet<string>(_plan.Operations.Where(op =>
        {
            var entry = left.GetValueOrDefault(op.Path) ?? right.GetValueOrDefault(op.Path);
            return !filter.Evaluate(op.Path, new FilterEntryAttributes(entry?.Fingerprint?.Size, entry?.Fingerprint?.ModifiedUtc)).Included;
        }).Select(op => op.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var operation in _plan.Operations.Where(op => ignoredPaths.Contains(op.Path))) operation.Selected = false;
        Status.Text = Tr.Format("Main.AnalysisDone", _plan.Operations.Count);
        var planSafety = new SafetyValidator().ValidatePlan(_plan, left.Count, right.Count, SelectedMode, profile.MaxDeletes, profile.MaxDeleteRatio).Combine(new SafetyValidator().ValidateCapacity(_plan, left, right, _left, _right));
        _safetySummary = planSafety.HasBlockingIssues && !SyncConfirmationPolicy.CanOverrideWithProfileName(planSafety)
            ? new SafetyState(Tr.F("Main.SafetyBlocked"), Tr.F("Main.SafetyBlockedDetail"), "Danger")
            : SyncConfirmationPolicy.RequiresConfirmation(SyncRiskSummary.Create(_plan, left, right))
                ? new SafetyState(Tr.F("Main.SafetyWarning"), Tr.F("Main.SafetyWarningDetail"), "Warning")
                : new SafetyState(Tr.F("Main.SafetyPassed"), Tr.F("Main.SafetyAdvice"), "Success");
        if (planSafety.HasBlockingIssues && !SyncConfirmationPolicy.CanOverrideWithProfileName(planSafety)) throw new InvalidOperationException(string.Join(" ", planSafety.Issues.Select(x => x.Message)));
        _comparison = new ComparisonSnapshot
        {
            SnapshotId = Guid.NewGuid(),
            Left = new EndpointSnapshot { Endpoint = _left.Profile, Paths = _left.Capabilities.EffectivePaths, StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow, Entries = scans[0], ByPath = left },
            Right = new EndpointSnapshot { Endpoint = _right.Profile, Paths = _right.Capabilities.EffectivePaths, StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow, Entries = scans[1], ByPath = right },
            Mode = ComparisonMode.TimeAndSize, TimeTolerance = TimeSpan.FromSeconds(effective.TimeToleranceSeconds), Baseline = baseline, Plan = _plan
        };
        _snapshot = PlanSnapshot.FromComparison(_plan, _comparison); _rows.Clear(); foreach (var op in _plan.Operations) _rows.Add(new(op, left.GetValueOrDefault(op.Path), right.GetValueOrDefault(op.Path), ignoredPaths.Contains(op.Path)));
        ApplyRowFilter();
        RefreshSummary();
        UpdateHeaderForCurrentProfile();
        var status = baselineWarning ?? Tr.Format("Main.CompareDone", ModeTitle(), left.Count, right.Count, _plan.Operations.Count, ignoredPaths.Count);
        Status.Text = _safetySummary.Severity is "Warning" or "Danger" ? $"{status} · {_safetySummary.Description}" : status;
    }
    private async Task<(IEndpoint Left, IEndpoint Right)> CreateEndpointsAsync(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) throw new InvalidOperationException(Tr.F("Main.FillBothEndpoints"));
        await DisposeRcloneAsync();
        var needsRemote = IsCloud(left) || IsCloud(right);
        if (needsRemote) _rcloneClient = await AppServices.Current.RcloneHost.GetClientAsync();
        return (CreateEndpoint(left), CreateEndpoint(right));
    }
    private IEndpoint CreateEndpoint(string value)
    {
        if (!IsCloud(value)) return new LocalEndpoint(value);
        if (_rcloneClient is null) throw new InvalidOperationException(Tr.F("Main.CloudNotStarted"));
        // Deliberately the factory's endpoint, not a hand-built one: it carries the per-backend move and
        // path capabilities, and a second copy of that table here had already drifted (it claimed every
        // cloud backend could rename a directory natively, which S3, SFTP and OneDrive cannot).
        return EndpointFactory.CreateWithClient(value, _rcloneClient);
    }
    private static bool IsCloud(string value) => EndpointSchemes.IsRemote(value);
    private Task DisposeRcloneAsync() { _rcloneClient = null; return Task.CompletedTask; }
    private void AddCloudEndpoint_Click(object sender, RoutedEventArgs e) => AddCloudEndpoint((sender as FrameworkElement)?.Tag as string);
    private void AddCloudEndpoint(string? side)
    {
        side ??= "Left";
        var picker = new RemoteEndpointPickerWindow(side) { Owner = this };
        if (picker.ShowDialog() != true || picker.ResultUri is null) return;
        var resolvedSide = side == "Right" ? Tr.F("Main.SideRightShort") : Tr.F("Main.SideLeftShort");
        var pathBox = side == "Right" ? RightPath : LeftPath;
        ChangeEndpointPath(pathBox, picker.ResultUri);
        Status.Text = Tr.Format("Main.EndpointAddedToSide", resolvedSide, picker.ResultUri);
    }
    private void ManageRemoteEndpoints_Click(object sender, RoutedEventArgs e) => new RemoteEndpointManagerWindow { Owner = this }.ShowDialog();
    private void OpenBatchRun_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0) { Status.Text = Tr.F("Main.NoRunnableProfile"); return; }
        new BatchRunWindow(_profiles.ToList(), CurrentSettings.MaxConcurrentCopies,
            async (profile, cancellationToken) =>
            {
                var client = await ((App)Application.Current).RcloneHost.GetClientAsync(cancellationToken);
                return await new ProfileRunner(applicationSettings: _settings, sharedRcloneClient: client)
                    .RunAsync(profile, ct: cancellationToken);
            })
        { Owner = this }.ShowDialog();
    }
    private async Task ApplyApplicationSettingsAsync(ApplicationSettings settings)
    {
        // The sidebar splitter is owned by the main window rather than the
        // settings dialog. Preserve a resize made immediately before opening
        // Settings when the dialog applies its own edited settings snapshot.
        settings = settings with { MainWindowSidebarWidth = ClampSidebarWidth(SidebarColumn.ActualWidth) };
        await new SettingsStore().SaveAsync(settings);
        _settings = settings;
        // Before the status line and the header: they are about to be rewritten in the new language, and
        // setting them first would show the old words for one frame.
        AppLanguageContext.Current.Apply(settings.Language);
        ApplyCloudTransferSettings(settings);
        UpdateSettingsText();
        Status.Text = Tr.F("Main.SettingsApplied");
        await RestoreCloudTransfersAsync(settings);
    }

    /// <summary>
    /// Pushes the cloud-transfer settings onto the process-wide queue. Called both when settings first load
    /// and when the dialog applies a new snapshot, so the two paths cannot drift apart.
    /// </summary>
    private void ApplyCloudTransferSettings(ApplicationSettings settings)
    {
        if (AppServices.TryCurrent?.CloudTransfers is not { } queue) return;
        queue.ResumeMinimumBytes = settings.DefaultResumeMinimumBytes;
        queue.Concurrency = settings.CloudMaxConcurrentTransfers;
        queue.MaxRetries = settings.CloudRetryCount;
        if (_cloudNotifier is null)
        {
            _cloudNotifier = new CloudCompletionNotifier(queue);
            _cloudNotifier.Notified += CloudNotifier_Notified;
            Closed += (_, _) => { _cloudNotifier.Notified -= CloudNotifier_Notified; _cloudNotifier.Dispose(); };
        }
        _cloudNotifier.Enabled = settings.CloudNotifyOnCompletion;
    }

    /// <summary>
    /// Lists whatever a previous session left unfinished so 断点续传 is one click away. It deliberately does
    /// not start anything: launching the application must not begin network transfers the user never asked
    /// for. Runs once, and only when the setting is on — the transfer window can always load the list itself.
    /// </summary>
    private async Task RestoreCloudTransfersAsync(ApplicationSettings settings)
    {
        if (_cloudTransfersRestored) return;
        if (!settings.CloudResumeOnStartup) return;
        if (AppServices.TryCurrent?.CloudTransfers is not { } queue) return;
        _cloudTransfersRestored = true;
        if (queue.HasTasks) return;
        try
        {
            var restored = await queue.LoadResumableAsync();
            if (restored > 0) Status.Text = Tr.Format("Main.TransfersRestored", restored);
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Unable to restore cloud transfers: " + ex.Message); }
    }

    /// <summary>
    /// Reports a drained cloud transfer batch in the status bar. The queue raises this on a worker thread,
    /// and the status bar belongs to the dispatcher.
    /// </summary>
    private void CloudNotifier_Notified(object? sender, string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => CloudNotifier_Notified(sender, message)); return; }
        Status.Text = message;
    }

    private Task ShowSftpServerSettingsAsync()
    {
        new SftpServerSettingsWindow { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }
    private async void OpenProfileFile_Click(object s, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Feng Sync files (*.fengsync.json;*.fengsync.batch.json)|*.fengsync.json;*.fengsync.batch.json|JSON files (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        var raw = await File.ReadAllTextAsync(dialog.FileName);
        BatchJob? batch = null;
        try { if (JsonDocument.Parse(raw).RootElement.ValueKind == JsonValueKind.Object) batch = JsonSerializer.Deserialize<BatchJob>(raw); } catch (JsonException) { }
        var loaded = batch?.Profiles?.Count > 0 ? batch.Profiles : await new ProfileStore(dialog.FileName).LoadAsync();
        if (loaded.Count == 0) { Status.Text = Tr.F("Main.NoProfileInFile"); return; }
        foreach (var profile in loaded.Where(x => _profiles.All(existing => existing.Id != x.Id))) _profiles.Add(profile);
        ProfileList.SelectedItem = loaded[0];
        await PersistProfilesAsync();
        Status.Text = batch is null ? Tr.Format("Main.ProfilesOpened", loaded.Count) : Tr.Format("Main.BatchOpened", batch.Name, loaded.Count);
    }
    private async void ExportProfile_Click(object s, RoutedEventArgs e)
    {
        await SaveProfileToListAsync();
        var profile = ProfileList.SelectedItem as SyncProfile;
        if (profile is null) return;
        var dialog = new SaveFileDialog { Filter = "Feng Sync Profile (*.fengsync.json)|*.fengsync.json", FileName = profile.Name + ".fengsync.json" };
        if (dialog.ShowDialog() != true) return;
        await new ProfileStore(dialog.FileName).SaveAsync([profile]);
        Status.Text = Tr.F("Main.ProfileSavedToFile");
    }
    private async void SaveBatchJob_Click(object s, RoutedEventArgs e)
    {
        await SaveProfileToListAsync();
        var profiles = ProfileList.SelectedItems.Cast<SyncProfile>().ToList();
        if (profiles.Count == 0 && ProfileList.SelectedItem is SyncProfile profile) profiles.Add(profile);
        if (profiles.Count == 0) return;
        var dialog = new SaveFileDialog { Filter = "Feng Sync Batch Job (*.fengsync.batch.json)|*.fengsync.batch.json", FileName = "batch.fengsync.batch.json" };
        if (dialog.ShowDialog() != true) return;
        var job = new BatchJob(Path.GetFileNameWithoutExtension(dialog.FileName), profiles);
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(job));
        Status.Text = Tr.Format("Main.BatchSaved", profiles.Count);
    }
    private void ManageSchedule_Click(object s, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not SyncProfile profile) { Status.Text = Tr.F("Main.SelectAProfile"); return; }
        new ScheduleWizard(profile) { Owner = this }.ShowDialog();
    }
    private async void ShowLog_Click(object s, RoutedEventArgs e)
    {
        var jobs = await new TaskJournalStore().LoadIncompleteAsync();
        var text = jobs.Count == 0 ? Tr.F("Settings.NoIncompleteLogs") : string.Join(Environment.NewLine + Environment.NewLine, jobs.Select(job => Tr.Format("Main.JobHeader", job.JobId, job.CreatedUtc) + string.Join(Environment.NewLine, job.Items.Select(item => $"{item.State,-10} {item.Kind,-24} {item.Path}"))));
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(14) };
        new Window { Title = Tr.F("Settings.LogTitle"), Owner = this, Content = box, Width = 680, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
    private async Task SaveProfileToListAsync() { var old = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create(Tr.F("Main.UnnamedProfile"), "", ""); var current = old with { LeftPath = LeftPath.Text, RightPath = RightPath.Text, Mode = SelectedMode }; var index = _profiles.IndexOf(old); if (index >= 0) _profiles[index] = current; else _profiles.Add(current); ProfileList.SelectedItem = current; await PersistProfilesAsync(); }
    private Task PersistProfilesAsync() => _profileStore.SaveAsync(_profiles);
    private async void CheckUpdates_Click(object s, RoutedEventArgs e)
    {
        if (_updates is not null) await _updates.CheckAsync(this, manual: true, _syncInProgress || _compareInProgress);
    }
    private void About_Click(object s, RoutedEventArgs e)
        => new AboutWindow(_versionService.DisplayVersion, async () => { if (_updates is not null) await _updates.CheckAsync(this, manual: true, _syncInProgress || _compareInProgress); }) { Owner = this }.ShowDialog();
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) { base.OnClosing(e); return; }
        e.Cancel = true;
        _closing = true;
        _syncCancellation?.Cancel();
        _compareCancellation?.Cancel();
        CloseWhenReadyAsync();
    }
    private Task<int> CleanupExpiredLocalTemporaryFilesAsync()
    {
        var roots = _profiles.SelectMany(profile => new[] { profile.LeftPath, profile.RightPath })
            .Where(path => !IsCloud(path));
        return Task.FromResult(TransferTemporaryMaintenance.RemoveExpiredLocalFiles(roots, TimeSpan.FromDays(7), DateTimeOffset.UtcNow));
    }
    private async void CloseWhenReadyAsync()
    {
        try
        {
            _settings = _settings with { LastSelectedProfileId = (ProfileList.SelectedItem as SyncProfile)?.Id };
            await new SettingsStore().SaveAsync(_settings);
            await PersistProfilesAsync();
            await DisposeRcloneAsync();
        }
        catch { }
        finally { await App.CurrentApp.ShutdownAsync(); }
    }
    private async Task ExitForUpdateAsync()
    {
        Status.Text = Tr.F("Main.ExitingToInstall");
        _closing = true;
        _settings = _settings with { LastSelectedProfileId = (ProfileList.SelectedItem as SyncProfile)?.Id };
        await new SettingsStore().SaveAsync(_settings); await PersistProfilesAsync(); await DisposeRcloneAsync(); await App.CurrentApp.ShutdownAsync();
    }
    protected override void OnClosed(EventArgs e) { base.OnClosed(e); }
    private sealed record BatchJob(string Name, IReadOnlyList<SyncProfile> Profiles);
}

/// <summary>One bucket in the right-hand summary panel (e.g. "上传 12 项 / 1.8 GB").</summary>
public sealed record ChangeBucket(int Count, long? Bytes);
/// <summary>Aggregated row counts + transfer sizes derived from the current ComparisonRow collection.
/// <see cref="Total"/> counts unique operations only — the same file change must not be added
/// to both Upload and Download. long? distinguishes a known zero-byte total from "size unknown".</summary>
public sealed record ChangeSummary(ChangeBucket Upload, ChangeBucket Download, ChangeBucket Delete, ChangeBucket Conflict, ChangeBucket Total)
{
    public static ChangeSummary Empty { get; } = new(new(0, 0), new(0, 0), new(0, 0), new(0, 0), new(0, 0));
}

public sealed class ComparisonRow : INotifyPropertyChanged
{
    public ComparisonRow(SyncOperation operation, EntrySnapshot? left, EntrySnapshot? right, bool isFilterExcluded = false) { Operation = operation; Left = left; Right = right; IsFilterExcluded = isFilterExcluded; Refresh(); }
    public SyncOperation Operation { get; }
    public EntrySnapshot? Left { get; }
    public EntrySnapshot? Right { get; }
    public bool IsFilterExcluded { get; private set; }
    public bool IsIgnored { get; set; }
    public bool Selected
    {
        get => Operation.Selected;
        set { if (IsIgnored || IsFilterExcluded || Operation.Selected == value) return; Operation.Selected = value; OnPropertyChanged(); }
    }

    // Display-only projections surfaced by the redesigned comparison grid.
    public string Name { get; private set; } = "";
    public string RelativePath { get; private set; } = "";
    public string DirectionDisplay { get; private set; } = "";
    public string OperationLabel { get; private set; } = "—";
    public string OperationSeverity { get; private set; } = "Neutral";
    public FluentIcon LeftEntryIcon => Left?.Kind == EntryKind.Directory ? FluentIcon.Folder : FluentIcon.Document;
    public FluentIcon RightEntryIcon => Right?.Kind == EntryKind.Directory ? FluentIcon.Folder : FluentIcon.Document;
    public FluentIcon ActionIcon { get; private set; } = FluentIcon.ArrowRight;
    public string ActionToolTip { get; private set; } = "";
    public string? LeftToolTip => EntryToolTip(Tr.F("Profile.SideLeft"), Left);
    public string? RightToolTip => EntryToolTip(Tr.F("Profile.SideRight"), Right);
    public long? OperationSize { get; private set; }
    public DateTime? ModifiedUtc { get; private set; }

    // Legacy display properties retained so the existing grid remains readable during migration.
    public string LeftDisplay { get; private set; } = "";
    public string RightDisplay { get; private set; } = "";
    public string LeftSize { get; private set; } = "";
    public string RightSize { get; private set; } = "";
    public string ActionDisplay { get; private set; } = "";
    public Brush ActionBrush { get; private set; } = Brushes.DimGray;
    public string Reason => Operation.Reason;
    public string? ConflictToolTip => Operation.IsConflict && !IsIgnored && !IsFilterExcluded
        ? Tr.Format("Main.ConflictTooltip", Operation.Reason, Environment.NewLine, ModifiedTime(Left), Environment.NewLine, ModifiedTime(Right))
        : null;
    public void Refresh()
    {
        Name = Path.GetFileName(Operation.Path);
        var dir = Path.GetDirectoryName(Operation.Path);
        RelativePath = string.IsNullOrEmpty(dir) ? "/" : "/" + dir.Replace('\\', '/');

        DirectionDisplay = (IsIgnored || IsFilterExcluded) ? "—" : Operation.Kind switch
        {
            OperationKind.CopyLeftToRight or OperationKind.CreateRightDirectory => Tr.F("Main.DirectionLeftToRight"),
            OperationKind.CopyRightToLeft or OperationKind.CreateLeftDirectory => Tr.F("Main.DirectionRightToLeft"),
            OperationKind.DeleteLeft => Tr.F("Main.DirectionToLeft"),
            OperationKind.DeleteRight => Tr.F("Main.DirectionRightToDash"),
            OperationKind.Move when Operation.Move?.ExecuteOn == EndpointSide.Left => Tr.F("Main.DirectionMoveLeft"),
            OperationKind.Move when Operation.Move?.ExecuteOn == EndpointSide.Right => Tr.F("Main.DirectionMoveRight"),
            _ => "—"
        };

        OperationLabel = IsIgnored ? Tr.F("Main.KindIgnored") : IsFilterExcluded ? Tr.F("Main.KindFiltered") : Operation.IsConflict ? Tr.F("Main.KindConflict") : Operation.Kind switch
        {
            OperationKind.CopyLeftToRight or OperationKind.CreateRightDirectory => Tr.F("Main.KindUpload"),
            OperationKind.CopyRightToLeft or OperationKind.CreateLeftDirectory => Tr.F("Main.KindDownload"),
            OperationKind.DeleteLeft or OperationKind.DeleteRight => Tr.F("Main.KindDelete"),
            OperationKind.Move => Tr.F("Main.KindMove"),
            OperationKind.Blocked => Tr.F("Main.KindBlocked"),
            _ => Tr.F("Main.KindUnknown")
        };
        OperationSeverity = OperationLabel == Tr.F("Main.KindUpload") ? "Success" : OperationLabel == Tr.F("Main.KindDownload") ? "Info" : OperationLabel == Tr.F("Main.KindDelete") ? "Danger" : OperationLabel == Tr.F("Main.KindConflict") ? "Warning" : "Neutral";
        ActionIcon = (IsIgnored || IsFilterExcluded) ? FluentIcon.Prohibited : Operation.IsConflict ? FluentIcon.Warning : Operation.Kind switch
        {
            OperationKind.CopyLeftToRight when Right is null => Left?.Kind == EntryKind.Directory ? FluentIcon.FolderArrowRight : FluentIcon.DocumentArrowRight,
            OperationKind.CopyRightToLeft when Left is null => Right?.Kind == EntryKind.Directory ? FluentIcon.FolderArrowLeft : FluentIcon.DocumentArrowLeft,
            OperationKind.CreateRightDirectory => FluentIcon.FolderArrowRight,
            OperationKind.CreateLeftDirectory => FluentIcon.FolderArrowLeft,
            OperationKind.CopyLeftToRight => FluentIcon.ArrowRight,
            OperationKind.CopyRightToLeft => FluentIcon.ArrowLeft,
            OperationKind.Move when Operation.Move?.ExecuteOn == EndpointSide.Left => FluentIcon.ArrowLeft,
            OperationKind.Move => FluentIcon.ArrowRight,
            OperationKind.DeleteLeft or OperationKind.DeleteRight => FluentIcon.Delete,
            OperationKind.Blocked => FluentIcon.Prohibited,
            _ => FluentIcon.ArrowRight
        };
        ActionToolTip = BuildActionToolTip();

        OperationSize = Operation.Kind switch
        {
            OperationKind.CopyLeftToRight or OperationKind.CreateRightDirectory => Left?.Fingerprint?.Size,
            OperationKind.CopyRightToLeft or OperationKind.CreateLeftDirectory => Right?.Fingerprint?.Size,
            OperationKind.DeleteLeft => Left?.Fingerprint?.Size,
            OperationKind.DeleteRight => Right?.Fingerprint?.Size,
            OperationKind.Move when Operation.Move?.ChangedOn == EndpointSide.Left => Left?.Fingerprint?.Size,
            OperationKind.Move => Right?.Fingerprint?.Size,
            _ => null
        };
        ModifiedUtc = ((IsIgnored || IsFilterExcluded)
            ? (Left?.Fingerprint?.ModifiedUtc ?? Right?.Fingerprint?.ModifiedUtc)
            : (Operation.Kind switch
            {
                OperationKind.CopyRightToLeft or OperationKind.DeleteRight => Right?.Fingerprint?.ModifiedUtc,
                OperationKind.Move when Operation.Move?.ChangedOn == EndpointSide.Right => Right?.Fingerprint?.ModifiedUtc,
                _ => Left?.Fingerprint?.ModifiedUtc
            }))?.LocalDateTime;

        LeftDisplay = Describe(Left);
        RightDisplay = Describe(Right);
        LeftSize = Size(Left);
        RightSize = Size(Right);
        (ActionDisplay, ActionBrush) = (IsIgnored || IsFilterExcluded) ? ("⊘", Brushes.DimGray) : Operation.IsConflict ? ("⚠", Brushes.DarkOrange) : Operation.Kind switch
        {
            OperationKind.CopyLeftToRight => ("✚→", Brushes.ForestGreen),
            OperationKind.CopyRightToLeft => ("←✚", Brushes.ForestGreen),
            OperationKind.DeleteLeft => ("←✖", Brushes.Firebrick),
            OperationKind.DeleteRight => ("✖→", Brushes.Firebrick),
            OperationKind.CreateLeftDirectory => ("←✚", Brushes.ForestGreen),
            OperationKind.CreateRightDirectory => ("✚→", Brushes.ForestGreen),
            OperationKind.Move => ("→", Brushes.DimGray),
            OperationKind.Blocked => ("⛔", Brushes.Firebrick),
            _ => ("=", Brushes.DimGray)
        };
    }
    private string BuildActionToolTip()
    {
        var quotedPath = $"“{Operation.Path}”";
        if (IsIgnored) return Tr.Format("Main.IgnoredForThisSync", Environment.NewLine, quotedPath);
        if (IsFilterExcluded) return Tr.Format("Main.ExcludedByProfileFilter", Environment.NewLine, quotedPath);
        if (Operation.IsConflict) return Tr.Format("Main.BothChanged", Environment.NewLine, quotedPath, Environment.NewLine, Operation.Reason);
        return Operation.Kind switch
        {
            OperationKind.CopyLeftToRight or OperationKind.CreateRightDirectory => $"{(Right is null ? "复制新项目到右侧" : "用左侧项目更新右侧")}{Environment.NewLine}{quotedPath}",
            OperationKind.CopyRightToLeft or OperationKind.CreateLeftDirectory => $"{(Left is null ? "复制新项目到左侧" : "用右侧项目更新左侧")}{Environment.NewLine}{quotedPath}",
            OperationKind.DeleteLeft => Tr.Format("Main.DeleteLeftItem", Environment.NewLine, quotedPath),
            OperationKind.DeleteRight => Tr.Format("Main.DeleteRightItem", Environment.NewLine, quotedPath),
            OperationKind.Move => BuildMoveActionToolTip(Operation.Move, Operation.Selected),
            OperationKind.Blocked => Tr.Format("Main.OperationBlocked", Environment.NewLine, quotedPath, Environment.NewLine, Operation.Reason),
            _ => Tr.Format("Main.NoActionNeeded", Environment.NewLine, quotedPath)
        };
    }
    private static string BuildMoveActionToolTip(MoveDescriptor? move, bool selected)
    {
        if (move is null) return Tr.F("Main.MoveMissingPaths");
        var item = move.Kind == EntryKind.Directory ? Tr.F("Main.KindFolder") : Tr.F("Main.KindFile");
        var paths = $"“{move.FromPath}”{Environment.NewLine}→ “{move.ToPath}”";
        if (move.ChangedOn == move.ExecuteOn && move.PreferredExecution == EndpointMoveExecution.None && move.Fallback == MoveFallback.None)
            return Tr.Format("Main.MovedToSamePlace", item, Environment.NewLine, paths);
        var side = move.ExecuteOn == EndpointSide.Left ? Tr.F("Profile.SideLeft") : Tr.F("Profile.SideRight");
        var confidence = !selected
            ? Tr.Format("Main.MoveLowConfidence", Environment.NewLine, MoveConfidenceText(move.Confidence))
            : "";
        return Tr.Format("Main.MoveIn", side, item, Environment.NewLine, paths, confidence);
    }
    private static string MoveConfidenceText(MoveConfidence confidence) => confidence switch
    {
        MoveConfidence.Certain => Tr.F("Settings.Ok"),
        MoveConfidence.High => Tr.F("Main.ConfidenceHigh"),
        MoveConfidence.Medium => Tr.F("Main.ConfidenceMedium"),
        _ => Tr.F("Main.ConfidenceLow")
    };
    private static string? EntryToolTip(string side, EntrySnapshot? entry) => entry switch
    {
        null => null,
        { Kind: EntryKind.Directory } => Tr.Format("Main.EntryDirectory", side, entry.Path),
        { Fingerprint: { } fingerprint } => Tr.Format("Main.EntryFile", side, entry.Path, Environment.NewLine, fingerprint.Size, Environment.NewLine, fingerprint.ModifiedUtc.LocalDateTime),
        _ => Tr.Format("Main.EntryItem", side, entry.Path)
    };
    private static string Describe(EntrySnapshot? e) => e?.Path ?? "";
    private static string Size(EntrySnapshot? e) => e?.Fingerprint is null ? "" : e.Fingerprint.Size.ToString("N0");
    private static string ModifiedTime(EntrySnapshot? entry) => entry switch
    {
        null => Tr.F("Main.MissingOrDeleted"),
        { Kind: EntryKind.Directory } => Tr.F("Main.DirectoryNoTimestamp"),
        { Fingerprint: { } fingerprint } => fingerprint.ModifiedUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
        _ => Tr.F("Main.KindUnknown")
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
}
