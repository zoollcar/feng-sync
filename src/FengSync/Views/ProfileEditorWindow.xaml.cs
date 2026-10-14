using System.Windows;
using System.Windows.Controls;
using System.Net.Sockets;
using System.IO;
using System.Collections.ObjectModel;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Localization;
using FengSync.Services;
using FengSync.ViewModels;

namespace FengSync.Views;

public partial class ProfileEditorWindow : Window
{
    private readonly ProfileEditorViewModel _viewModel;
    private readonly ObservableCollection<EditableFilterRule> _rules = [];
    private readonly ApplicationSettings _application;
    private bool _backgroundActionInProgress;
    public SyncProfile? SavedProfile { get; private set; }
    public ProfileEditorWindow(SyncProfile profile, ApplicationSettings? application = null)
    {
        InitializeComponent(); _application = application ?? new(); _viewModel = new(profile, _application); DataContext = _viewModel;
        NameBox.Text = profile.Name; DescriptionBox.Text = profile.Description; LeftPathBox.Text = profile.LeftPath; RightPathBox.Text = profile.RightPath; ModeBox.SelectedIndex = (int)profile.Mode; Enabled.IsChecked = profile.Enabled;
        var setting = profile.Settings;
        foreach (var rule in (setting?.Filter ?? profile.Filter ?? SyncFilter.Empty).ToRules()) _rules.Add(new(rule));
        RuleGrid.ItemsSource = _rules;
        UseDefaultCopies.IsChecked = setting?.VerifyCopies is null; VerifyCopies.IsChecked = setting?.VerifyCopies ?? profile.VerifyCopies; MaxDeletesBox.Text = profile.MaxDeletes.ToString(); MaxDeleteRatioBox.Text = profile.MaxDeleteRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        UseDefaultConcurrency.IsChecked = setting?.MaxConcurrentCopies is null; ConcurrencyBox.Text = (setting?.MaxConcurrentCopies ?? profile.MaxConcurrentCopies).ToString();
        UseDefaultResume.IsChecked = setting?.ResumeMinimumBytes is null; ResumeMinimumBox.Text = ByteSize.Format(setting?.ResumeMinimumBytes ?? _application.DefaultResumeMinimumBytes);
        TimeToleranceBox.Text = (setting?.TimeToleranceSeconds ?? 2).ToString();
        // Resolved the same way the runner resolves it, global default included: showing "delete permanently"
        // for a profile that actually inherits the recycle bin would both contradict the compatibility banner
        // above and, on save, silently turn the inherited policy into an explicit permanent delete.
        var versioning = setting?.Versioning ?? profile.Versioning ?? _application.DefaultVersioning; VersioningBox.SelectedIndex = versioning.Mode == VersioningMode.TimestampedArchive ? 1 : versioning.Mode == VersioningMode.RecycleBin ? 2 : 0; ArchiveBox.Text = versioning.ArchiveDirectory; KeepDaysBox.Text = versioning.KeepDays?.ToString() ?? ""; MaxVersionsBox.Text = versioning.MaxVersionsPerFile?.ToString() ?? ""; MaxTotalMbBox.Text = versioning.MaxTotalBytes is long bytes ? (bytes / 1024d / 1024d).ToString("0.##") : "";
        RefreshState();
    }
    private void Section_Changed(object sender, SelectionChangedEventArgs e)
    {
        // The initial ListBox selection can fire while XAML is still constructing the
        // content pages; defer until all named panels exist.
        if (Sections.SelectedIndex < 0 || GeneralPage is null) return;
        var pages = new[] { GeneralPage, ComparisonPage, FilterPage, SyncPage, VersioningPage, PerformancePage };
        for (var index = 0; index < pages.Length; index++) pages[index].Visibility = index == Sections.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Save(false);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => Save(true);
    private void ApplyCommonExcludes_Click(object sender, RoutedEventArgs e)
    {
        var common = new[] { "**/*.tmp", "**/*.partial", ".git/**", ".svn/**", "Thumbs.db", ".DS_Store" };
        foreach (var pattern in common.Where(pattern => _rules.All(rule => !rule.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase)))) _rules.Add(new(FilterRuleKind.Exclude, pattern, Tr.F("Profile.CommonExcludes")));
        FilterTestResult.Text = Tr.F("Profile.CommonExcludesAdded");
    }
    private void TestFilter_Click(object sender, RoutedEventArgs e)
    {
        var path = FilterTestPathBox.Text.Trim();
        if (!TryLong(FilterTestSizeBox.Text, out var size, Tr.F("Profile.TestFileSize"))) return;
        var engine = new SyncFilter(Rules: Rules()).CreateEngine();
        var decision = engine.Evaluate(path, new FilterEntryAttributes(size, IsHidden: FilterTestHiddenBox.IsChecked == true, IsSymbolicLink: FilterTestSymlinkBox.IsChecked == true));
        FilterTestResult.Text = decision.Included
            ? Tr.Format("Profile.ResultIncluded", decision.Reason)
            : Tr.Format("Profile.ResultExcluded", decision.Reason);
    }
    private void Save(bool newId)
    {
        if (!int.TryParse(ConcurrencyBox.Text, out var concurrency) || concurrency is < 1 or > 64) { MessageBox.Show(Tr.F("Profile.ErrConcurrency"), Tr.F("Profile.WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!int.TryParse(MaxDeletesBox.Text, out var maxDeletes) || maxDeletes < 0) { Sections.SelectedIndex = 5; MessageBox.Show(Tr.F("Profile.ErrMaxDeletes"), Tr.F("Profile.WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!double.TryParse(MaxDeleteRatioBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var maxDeleteRatio) || maxDeleteRatio is < 0 or > 1) { Sections.SelectedIndex = 5; MessageBox.Show(Tr.F("Profile.ErrMaxDeleteRatio"), Tr.F("Profile.WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!int.TryParse(TimeToleranceBox.Text, out var tolerance) || tolerance is < 0 or > 86400) { Sections.SelectedIndex = 1; MessageBox.Show(Tr.F("Profile.ErrTimeTolerance"), Tr.F("Profile.WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!TryNullableInt(KeepDaysBox.Text, out var keepDays, Tr.F("Profile.KeepDays")) || !TryNullableInt(MaxVersionsBox.Text, out var maxVersions, Tr.F("Profile.MaxVersions")) || !TryTotalBytes(MaxTotalMbBox.Text, out var maxTotalBytes)) { Sections.SelectedIndex = 4; return; }
        long? resumeMinimum = null;
        if (UseDefaultResume.IsChecked != true)
        {
            if (!ByteSize.TryParse(ResumeMinimumBox.Text, out var parsedResume)) { Sections.SelectedIndex = 5; MessageBox.Show(Tr.F("Profile.ErrResumeMinimum"), Tr.F("Profile.WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            resumeMinimum = parsedResume;
        }
        var filter = new SyncFilter(Rules: Rules());
        var mode = (SyncMode)Math.Max(0, ModeBox.SelectedIndex);
        var versioning = VersioningBox.SelectedIndex switch { 1 => new VersioningPolicy(VersioningMode.TimestampedArchive, ArchiveBox.Text, keepDays, maxVersions, maxTotalBytes), 2 => new VersioningPolicy(VersioningMode.RecycleBin), _ => new VersioningPolicy() };
        var settings = new ProfileSettings(UseDefaultConcurrency.IsChecked == true ? null : concurrency, UseDefaultCopies.IsChecked == true ? null : VerifyCopies.IsChecked == true, filter, versioning, tolerance, resumeMinimum);
        _viewModel.Update(p => p with { Name = NameBox.Text.Trim(), Description = DescriptionBox.Text, LeftPath = LeftPathBox.Text.Trim(), RightPath = RightPathBox.Text.Trim(), Mode = mode, Enabled = Enabled.IsChecked == true, MaxDeletes = maxDeletes, MaxDeleteRatio = maxDeleteRatio, Settings = settings });
        var validation = ProfileValidator.Validate(_viewModel.Profile);
        // Which page to open is decided by the field the refusal is about, never by looking for a word inside
        // its sentence — that word changes with the interface language.
        if (!validation.IsValid) { Sections.SelectedIndex = validation.Errors.Any(x => x.Field is ProfileValidationField.Identity or ProfileValidationField.Endpoints) ? 0 : 4; MessageBox.Show(string.Join(Environment.NewLine, validation.Messages), Tr.F("Profile.CannotSave"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        SavedProfile = newId ? _viewModel.SaveAsNew() : _viewModel.Profile; DialogResult = true;
    }
    private void RefreshState() { CompatibilityText.Text = _viewModel.Compatibility.CanRun ? "" : Tr.Format("Profile.NeedsFix", _viewModel.Compatibility.Summary); DirtyLabel.Text = Tr.F("Profile.EditCopyNote"); }
    private void UseDefaultCopies_Changed(object sender, RoutedEventArgs e) => VerifyCopies.IsEnabled = UseDefaultCopies.IsChecked != true;
    private void UseDefaultConcurrency_Changed(object sender, RoutedEventArgs e) => ConcurrencyBox.IsEnabled = UseDefaultConcurrency.IsChecked != true;
    private void UseDefaultResume_Changed(object sender, RoutedEventArgs e) => ResumeMinimumBox.IsEnabled = UseDefaultResume.IsChecked != true;
    private void AddIncludeRule_Click(object sender, RoutedEventArgs e) => AddRule(FilterRuleKind.Include);
    private void AddExcludeRule_Click(object sender, RoutedEventArgs e) => AddRule(FilterRuleKind.Exclude);
    private void AddRule(FilterRuleKind kind) { var rule = new EditableFilterRule(kind, "**", ""); _rules.Add(rule); RuleGrid.SelectedItem = rule; }
    private void RemoveRule_Click(object sender, RoutedEventArgs e) { if (RuleGrid.SelectedItem is EditableFilterRule rule) _rules.Remove(rule); }
    private void MoveRuleUp_Click(object sender, RoutedEventArgs e) => MoveRule(-1);
    private void MoveRuleDown_Click(object sender, RoutedEventArgs e) => MoveRule(1);
    private void MoveRule(int delta) { if (RuleGrid.SelectedItem is not EditableFilterRule rule) return; var index = _rules.IndexOf(rule); var destination = index + delta; if (destination < 0 || destination >= _rules.Count) return; _rules.Move(index, destination); RuleGrid.SelectedItem = rule; }
    private void RuleGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RuleGrid.SelectedItem is not EditableFilterRule rule) return;
        RuleMinSizeBox.Text = rule.MinimumSizeBytes?.ToString() ?? ""; RuleMaxSizeBox.Text = rule.MaximumSizeBytes?.ToString() ?? ""; RuleAfterBox.Text = rule.ModifiedAfter?.ToString("O") ?? ""; RuleBeforeBox.Text = rule.ModifiedBefore?.ToString("O") ?? ""; RuleHiddenBox.IsChecked = rule.Hidden == true; RuleSymlinkBox.IsChecked = rule.SymbolicLink == true;
    }
    private void ApplyRuleAttributes_Click(object sender, RoutedEventArgs e)
    {
        if (RuleGrid.SelectedItem is not EditableFilterRule rule) { FilterTestResult.Text = Tr.F("Profile.SelectRuleFirst"); return; }
        if (!TryLong(RuleMinSizeBox.Text, out var min, Tr.F("Profile.MinBytes")) || !TryLong(RuleMaxSizeBox.Text, out var max, Tr.F("Profile.MaxBytes")) || !TryDate(RuleAfterBox.Text, out var after, Tr.F("Profile.ModifiedAfterLimit")) || !TryDate(RuleBeforeBox.Text, out var before, Tr.F("Profile.ModifiedBeforeLimit"))) return;
        rule.MinimumSizeBytes = min; rule.MaximumSizeBytes = max; rule.ModifiedAfter = after; rule.ModifiedBefore = before; rule.Hidden = RuleHiddenBox.IsChecked == true ? true : null; rule.SymbolicLink = RuleSymlinkBox.IsChecked == true ? true : null; RuleGrid.Items.Refresh();
    }
    private async void PreviewRetention_Click(object sender, RoutedEventArgs e) => await RunBusyAsync((Button)sender, Tr.F("Profile.Previewing"), () => ShowRetentionAsync(cleanup: false));
    private async void CleanupRetention_Click(object sender, RoutedEventArgs e) => await RunBusyAsync((Button)sender, Tr.F("Profile.Cleaning"), () => ShowRetentionAsync(cleanup: true));
    private async Task ShowRetentionAsync(bool cleanup)
    {
        try { if (!TryNullableInt(KeepDaysBox.Text, out var days, Tr.F("Profile.KeepDays")) || !TryNullableInt(MaxVersionsBox.Text, out var versions, Tr.F("Profile.MaxVersions")) || !TryTotalBytes(MaxTotalMbBox.Text, out var bytes)) return; var service = new RetentionCleanupService(); var policy = new RetentionPolicy(days, versions, bytes); if (cleanup) { var count = await service.CleanupAsync(ArchiveBox.Text.Trim(), policy); RetentionResult.Text = Tr.Format("Profile.CleanedVersions", count); } else { var candidates = await service.PreviewAsync(ArchiveBox.Text.Trim(), policy); RetentionResult.Text = candidates.Count == 0 ? Tr.F("Profile.NothingToClean") : Tr.Format("Profile.WillClean", candidates.Count) + string.Join(Tr.F("Profile.CleanListSeparator"), candidates.Take(3).Select(x => Tr.Format("Profile.CleanItem", Path.GetFileName(x.Path), x.Reason))); } }
        catch (Exception ex) { RetentionResult.Text = Tr.Format("Profile.InvalidRetention", ex.Message); }
    }
    private void SwapEndpoints_Click(object sender, RoutedEventArgs e) => (LeftPathBox.Text, RightPathBox.Text) = (RightPathBox.Text, LeftPathBox.Text);
    private void BrowseLeft_Click(object sender, RoutedEventArgs e) => Browse(LeftPathBox);
    private void BrowseRight_Click(object sender, RoutedEventArgs e) => Browse(RightPathBox);
    private static void Browse(TextBox target)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog() == true) target.Text = dialog.FolderName;
    }
    private async void TestLeft_Click(object sender, RoutedEventArgs e) => await RunBusyAsync((Button)sender, Tr.F("Profile.Testing"), () => TestEndpointAsync(Tr.F("Profile.SideLeft"), LeftPathBox.Text));
    private async void TestRight_Click(object sender, RoutedEventArgs e) => await RunBusyAsync((Button)sender, Tr.F("Profile.Testing"), () => TestEndpointAsync(Tr.F("Profile.SideRight"), RightPathBox.Text));
    private async Task TestEndpointAsync(string side, string value)
    {
        try
        {
            var endpoint = value.Trim();
            if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException(Tr.F("Profile.ErrEndpointEmpty"));
            if (!endpoint.Contains("://", StringComparison.Ordinal))
            {
                if (!Directory.Exists(endpoint)) throw new DirectoryNotFoundException(Tr.F("Profile.ErrLocalMissing"));
                EndpointTestResult.Text = Tr.Format("Profile.EndpointAccessible", side, endpoint); return;
            }
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)) throw new InvalidOperationException(Tr.F("Profile.ErrRemoteInvalid"));
            if (!uri.Scheme.Equals("sftp", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException(Tr.F("Profile.ErrOnlySftp"));
            using var client = new TcpClient(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(uri.Host, uri.IsDefaultPort ? 22 : uri.Port, timeout.Token);
            EndpointTestResult.Text = Tr.Format("Profile.SftpConnectable", side, uri.Host, uri.IsDefaultPort ? 22 : uri.Port);
        }
        catch (Exception ex) { EndpointTestResult.Text = Tr.Format("Profile.EndpointTestFailed", side, RcloneUiError.Describe(ex, "profile-endpoint-test")); }
    }
    private async Task RunBusyAsync(Button button, string busyText, Func<Task> action)
    {
        if (_backgroundActionInProgress) return;
        _backgroundActionInProgress = true;
        var original = button.Content;
        try { button.IsEnabled = false; button.Content = busyText; await action(); }
        finally { button.IsEnabled = true; button.Content = original; _backgroundActionInProgress = false; }
    }
    private IReadOnlyList<FilterRule> Rules() => _rules.Where(x => !string.IsNullOrWhiteSpace(x.Pattern)).Select(x => x.ToRule()).ToList();
    private bool TryLong(string text, out long? value, string title) { if (string.IsNullOrWhiteSpace(text)) { value = null; return true; } if (long.TryParse(text, out var parsed) && parsed >= 0) { value = parsed; return true; } value = null; FilterTestResult.Text = title + Tr.F("Profile.ErrNonNegativeInt"); return false; }
    private bool TryNullableInt(string text, out int? value, string title) { if (string.IsNullOrWhiteSpace(text)) { value = null; return true; } if (int.TryParse(text, out var parsed) && parsed >= 0) { value = parsed; return true; } value = null; RetentionResult.Text = title + Tr.F("Profile.ErrNonNegativeInt"); return false; }
    private bool TryTotalBytes(string text, out long? value) { if (string.IsNullOrWhiteSpace(text)) { value = null; return true; } if (double.TryParse(text, out var mb) && mb >= 0 && mb <= long.MaxValue / 1024d / 1024d) { value = (long)(mb * 1024 * 1024); return true; } value = null; RetentionResult.Text = Tr.F("Profile.ErrMaxTotalMb"); return false; }
    private bool TryDate(string text, out DateTimeOffset? value, string title) { if (string.IsNullOrWhiteSpace(text)) { value = null; return true; } if (DateTimeOffset.TryParse(text, out var parsed)) { value = parsed; return true; } value = null; FilterTestResult.Text = title + Tr.F("Profile.ErrValidDateTime"); return false; }
    private sealed class EditableFilterRule(FilterRuleKind kind, string pattern, string? comment)
    {
        public FilterRuleKind Kind { get; } = kind; public string Pattern { get; set; } = pattern; public string? Comment { get; set; } = comment; public bool Enabled { get; set; } = true; public long? MinimumSizeBytes { get; set; } public long? MaximumSizeBytes { get; set; } public DateTimeOffset? ModifiedAfter { get; set; } public DateTimeOffset? ModifiedBefore { get; set; } public bool? Hidden { get; set; } public bool? SymbolicLink { get; set; }
        public EditableFilterRule(FilterRule rule) : this(rule.Kind, rule.Pattern, rule.Comment) { Enabled = rule.Enabled; MinimumSizeBytes = rule.MinimumSizeBytes; MaximumSizeBytes = rule.MaximumSizeBytes; ModifiedAfter = rule.ModifiedAfter; ModifiedBefore = rule.ModifiedBefore; Hidden = rule.Hidden; SymbolicLink = rule.SymbolicLink; }
        public FilterRule ToRule() => new(Kind, Pattern.Trim(), Comment, Enabled, MinimumSizeBytes, MaximumSizeBytes, ModifiedAfter, ModifiedBefore, Hidden, SymbolicLink);
    }
}
