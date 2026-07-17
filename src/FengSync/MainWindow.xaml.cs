using FengSync.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Media;

namespace FengSync;
public partial class MainWindow : Window
{
    private readonly ObservableCollection<ComparisonRow> _rows = []; private readonly ObservableCollection<SyncProfile> _profiles = []; private readonly ProfileStore _profileStore = new(); private SyncPlan? _plan; private LocalEndpoint? _left, _right; private AppSettings _settings;
    private SyncMode SelectedMode => (SyncMode)Math.Max(0, SyncModeBox?.SelectedIndex ?? 0);
    public MainWindow() { InitializeComponent(); Comparison.ItemsSource = _rows; ProfileList.ItemsSource = _profiles; _settings = LoadSettings(); Concurrency.Value = _settings.MaxConcurrentCopies; VerifyCopies.IsChecked = _settings.VerifyCopies; ShowCompleted.IsChecked = _settings.ShowCompleted; UpdateSettingsText(); Status.Text = "选择左右端点后点击“比较”。"; Loaded += async (_, _) => await LoadProfilesAsync(); }
    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        try { _left = new(LeftPath.Text); _right = new(RightPath.Text); var left = _left.Scan().ToDictionary(x => x.Path); var right = _right.Scan().ToDictionary(x => x.Path); var baseline = SelectedMode == SyncMode.TwoWay ? await new BaselineStore().LoadAsync(_left, _right) : null; _plan = new ModePlanner().Build(SelectedMode, left.Values, right.Values, baseline, _settings.Filter); _rows.Clear(); foreach (var op in _plan.Operations) _rows.Add(new(op, left.GetValueOrDefault(op.Path), right.GetValueOrDefault(op.Path))); RefreshSummary(); ProgressBar.Value = 0; ProgressText.Text = ""; CurrentFile.Text = ""; Status.Text = $"{ModeTitle()} 比较完成。勾选要执行的差异，并裁决所有冲突。"; }
        catch (Exception ex) { SyncButton.IsEnabled = false; Status.Text = ex.Message; }
    }
    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _left is null || _right is null) return;
        ProgressWindow? progressDialog = null;
        try { var operations = _rows.Select(x => x.Operation).ToList(); var current = new SyncPlan(operations); if (!current.CanExecute) { Status.Text = "请先选择操作并裁决所有冲突。"; return; } SyncButton.IsEnabled = false; var total = operations.Count(x => x.Selected && x.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft); var completed = 0; ProgressBar.Value = 0; ProgressText.Text = $"0 / {total}"; Status.Text = $"正在以 {_settings.MaxConcurrentCopies} 路并发同步…"; progressDialog = new ProgressWindow(total, !_settings.ShowCompleted) { Owner = this }; progressDialog.Show(); await new LocalExecutor().ExecuteAsync(current, _left, _right, new Progress<string>(p => { completed++; ProgressBar.Value = total == 0 ? 100 : completed * 100.0 / total; ProgressText.Text = $"{completed} / {total}"; CurrentFile.Text = p; progressDialog.Report(p); }), journals: new TaskJournalStore(), maxConcurrentCopies: _settings.MaxConcurrentCopies, verifyCopies: _settings.VerifyCopies, versioning: _settings.Versioning); if (SelectedMode == SyncMode.TwoWay) await new BaselineStore().CommitAsync(_left, _right); Status.Text = "同步完成。"; ProgressBar.Value = 100; progressDialog.Complete(true, "所有选中的操作已完成。"); }
        catch (Exception ex) { Status.Text = "同步失败：" + ex.Message; progressDialog?.Complete(false, ex.Message); }
        finally { RefreshSummary(); }
    }
    private void KeepLeft_Click(object sender, RoutedEventArgs e) => ResolveSelected(true); private void KeepRight_Click(object sender, RoutedEventArgs e) => ResolveSelected(false);
    private void ResolveSelected(bool left) { if (Comparison.SelectedItem is not ComparisonRow row || !row.Operation.IsConflict) { Status.Text = "请选择一个未裁决的冲突行。"; return; } try { row.Operation.Resolve(left); row.Refresh(); Comparison.Items.Refresh(); RefreshSummary(); } catch (Exception ex) { Status.Text = ex.Message; } }
    private void RefreshSummary() { var selected = _rows.Count(x => x.Selected); Summary.Text = $"左侧 {_rows.Count(x => x.Left is not null)} 项  ·  右侧 {_rows.Count(x => x.Right is not null)} 项  ·  { _rows.Count } 个差异/提示"; SelectedLabel.Text = selected.ToString(); SyncButton.IsEnabled = _plan is not null && new SyncPlan(_rows.Select(x => x.Operation).ToList()).CanExecute; }
    private void BrowseLeft_Click(object s, RoutedEventArgs e) => Browse(LeftPath); private void BrowseRight_Click(object s, RoutedEventArgs e) => Browse(RightPath);
    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        (LeftPath.Text, RightPath.Text) = (RightPath.Text, LeftPath.Text);
        if (_plan is not null) await RecompareAsync();
    }
    private static void Browse(System.Windows.Controls.TextBox target) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog() == true) target.Text = dialog.FolderName; }
    private void Comparison_CellEditEnding(object s, System.Windows.Controls.DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(RefreshSummary);
    private void Concurrency_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_settings is null) return; _settings.MaxConcurrentCopies = (int)Math.Round(e.NewValue); UpdateSettingsText(); }
    private void UpdateSettingsText() { if (ConcurrencyValue is not null) ConcurrencyValue.Text = _settings.MaxConcurrentCopies.ToString(); if (ConcurrencyLabel is not null) ConcurrencyLabel.Text = _settings.MaxConcurrentCopies + " 路"; }
    private void SyncMode_Changed(object s, SelectionChangedEventArgs e) { if (SyncModeCaption is not null) SyncModeCaption.Text = ModeTitle(); if (_plan is not null) Compare_Click(this, new RoutedEventArgs()); }
    private string ModeTitle() => SelectedMode switch { SyncMode.Mirror => "镜像 →", SyncMode.Update => "更新 →", SyncMode.Custom => "自定义 →", _ => "双向 ↔" };
    private void ToggleSettings_Click(object s, RoutedEventArgs e) { var column = MainLayout.ColumnDefinitions[0]; column.Width = column.Width.Value == 0 ? new GridLength(250) : new GridLength(0); }
    private void SaveSettings_Click(object s, RoutedEventArgs e) { _settings.VerifyCopies = VerifyCopies.IsChecked == true; _settings.ShowCompleted = ShowCompleted.IsChecked == true; Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings)); Status.Text = "设置已保存。"; }
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FengSync", "FengSync.local.json");
    private static AppSettings LoadSettings() { try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new() : new(); } catch { return new(); } }
    private Task RecompareAsync() { Compare_Click(this, new RoutedEventArgs()); return Task.CompletedTask; }
    private async Task LoadProfilesAsync()
    {
        _profiles.Clear(); foreach (var item in await _profileStore.LoadAsync()) _profiles.Add(item);
        if (_profiles.Count == 0) _profiles.Add(SyncProfile.Create("未命名配置", "", ""));
        ProfileList.SelectedIndex = 0;
    }
    private void NewProfile_Click(object s, RoutedEventArgs e)
    {
        var profile = SyncProfile.Create("未命名配置 " + (_profiles.Count + 1), "", ""); _profiles.Add(profile); ProfileList.SelectedItem = profile; Status.Text = "已新建配置档案。";
    }
    private void LoadProfile_Click(object s, RoutedEventArgs e) { if (ProfileList.SelectedItem is SyncProfile profile) ApplyProfile(profile); else Status.Text = "请先选择一个配置档案。"; }
    private async void SaveProfile_Click(object s, RoutedEventArgs e)
    {
        var old = ProfileList.SelectedItem as SyncProfile ?? SyncProfile.Create("未命名配置", "", "");
        var current = old with { LeftPath = LeftPath.Text, RightPath = RightPath.Text, Mode = SelectedMode, MaxConcurrentCopies = _settings.MaxConcurrentCopies, VerifyCopies = _settings.VerifyCopies, Filter = _settings.Filter, Versioning = _settings.Versioning };
        var index = _profiles.IndexOf(old); if (index >= 0) _profiles[index] = current; else _profiles.Add(current); await _profileStore.SaveAsync(_profiles); ProfileList.SelectedItem = current; Status.Text = "配置档案已保存（凭据不会写入档案）。";
    }
    private async void RunProfile_Click(object s, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not SyncProfile profile || string.IsNullOrWhiteSpace(profile.LeftPath) || string.IsNullOrWhiteSpace(profile.RightPath)) { Status.Text = "请先保存具有两个本地路径的配置档案。"; return; }
        try { Status.Text = "正在执行批处理配置…"; var result = await new ProfileRunner().RunAsync(profile); Status.Text = $"批处理完成：计划 {result.Planned} 项，执行 {result.Executed} 项。"; }
        catch (Exception ex) { Status.Text = "批处理失败：" + ex.Message; }
    }
    private void ProfileList_SelectionChanged(object s, SelectionChangedEventArgs e) { if (ProfileList.SelectedItem is SyncProfile profile) ApplyProfile(profile); }
    private void ApplyProfile(SyncProfile profile)
    {
        LeftPath.Text = profile.LeftPath; RightPath.Text = profile.RightPath; SyncModeBox.SelectedIndex = (int)profile.Mode; Concurrency.Value = profile.MaxConcurrentCopies; VerifyCopies.IsChecked = profile.VerifyCopies; _settings.Filter = profile.Filter ?? SyncFilter.Empty; _settings.Versioning = profile.Versioning ?? new(); Status.Text = $"已载入配置：{profile.Name}";
    }
    private sealed class AppSettings { public int MaxConcurrentCopies { get; set; } = 3; public bool VerifyCopies { get; set; } = true; public bool ShowCompleted { get; set; } = true; public SyncFilter? Filter { get; set; } = SyncFilter.Empty; public VersioningPolicy? Versioning { get; set; } = new(); }
}
public sealed class ComparisonRow
{
    public ComparisonRow(SyncOperation operation, EntrySnapshot? left, EntrySnapshot? right) { Operation = operation; Left = left; Right = right; Refresh(); }
    public SyncOperation Operation { get; } public EntrySnapshot? Left { get; } public EntrySnapshot? Right { get; } public bool Selected { get => Operation.Selected; set => Operation.Selected = value; } public string LeftDisplay { get; private set; } = ""; public string RightDisplay { get; private set; } = ""; public string LeftSize { get; private set; } = ""; public string RightSize { get; private set; } = ""; public string ActionDisplay { get; private set; } = ""; public Brush ActionBrush { get; private set; } = Brushes.DimGray; public string Reason => Operation.Reason;
    public void Refresh() { LeftDisplay = Describe(Left); RightDisplay = Describe(Right); LeftSize = Size(Left); RightSize = Size(Right); (ActionDisplay, ActionBrush) = Operation.IsConflict ? ("⚠", Brushes.DarkOrange) : Operation.Kind switch { OperationKind.CopyLeftToRight => ("✚→", Brushes.ForestGreen), OperationKind.CopyRightToLeft => ("←✚", Brushes.ForestGreen), OperationKind.DeleteLeft => ("←✖", Brushes.Firebrick), OperationKind.DeleteRight => ("✖→", Brushes.Firebrick), OperationKind.CreateLeftDirectory => ("←✚", Brushes.ForestGreen), OperationKind.CreateRightDirectory => ("✚→", Brushes.ForestGreen), OperationKind.Blocked => ("⛔", Brushes.Firebrick), _ => ("=", Brushes.DimGray) }; }
    private static string Describe(EntrySnapshot? e) => e is null ? "" : e.Kind == EntryKind.Directory ? "▰ " + e.Path : "▱ " + e.Path;
    private static string Size(EntrySnapshot? e) => e?.Fingerprint is null ? "" : e.Fingerprint.Size.ToString("N0");
}
