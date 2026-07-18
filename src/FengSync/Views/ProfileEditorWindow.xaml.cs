using System.Windows;
using System.Windows.Controls;
using System.Net.Sockets;
using System.IO;
using System.Collections.ObjectModel;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.ViewModels;

namespace FengSync.Views;

public partial class ProfileEditorWindow : Window
{
    private readonly ProfileEditorViewModel _viewModel;
    private readonly ObservableCollection<EditableFilterRule> _rules = [];
    public SyncProfile? SavedProfile { get; private set; }
    public ProfileEditorWindow(SyncProfile profile)
    {
        InitializeComponent(); _viewModel = new(profile); DataContext = _viewModel;
        NameBox.Text = profile.Name; DescriptionBox.Text = profile.Description; LeftPathBox.Text = profile.LeftPath; RightPathBox.Text = profile.RightPath; ModeBox.SelectedIndex = (int)profile.Mode; Enabled.IsChecked = profile.Enabled;
        var setting = profile.Settings;
        foreach (var rule in (setting?.Filter ?? profile.Filter ?? SyncFilter.Empty).ToRules()) _rules.Add(new(rule));
        RuleGrid.ItemsSource = _rules;
        UseDefaultCopies.IsChecked = setting?.VerifyCopies is null; VerifyCopies.IsChecked = setting?.VerifyCopies ?? profile.VerifyCopies;
        UseDefaultConcurrency.IsChecked = setting?.MaxConcurrentCopies is null; ConcurrencyBox.Text = (setting?.MaxConcurrentCopies ?? profile.MaxConcurrentCopies).ToString();
        TimeToleranceBox.Text = (setting?.TimeToleranceSeconds ?? 2).ToString();
        var versioning = setting?.Versioning ?? profile.Versioning ?? new(); VersioningBox.SelectedIndex = versioning.Mode == VersioningMode.TimestampedArchive ? 1 : versioning.Mode == VersioningMode.RecycleBin ? 2 : 0; ArchiveBox.Text = versioning.ArchiveDirectory; KeepDaysBox.Text = versioning.KeepDays?.ToString() ?? ""; MaxVersionsBox.Text = versioning.MaxVersionsPerFile?.ToString() ?? ""; MaxTotalMbBox.Text = versioning.MaxTotalBytes is long bytes ? (bytes / 1024d / 1024d).ToString("0.##") : "";
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
        foreach (var pattern in common.Where(pattern => _rules.All(rule => !rule.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase)))) _rules.Add(new(FilterRuleKind.Exclude, pattern, "常用排除"));
        FilterTestResult.Text = "已添加常用临时文件、版本控制目录和系统文件排除规则。";
    }
    private void TestFilter_Click(object sender, RoutedEventArgs e)
    {
        var path = FilterTestPathBox.Text.Trim();
        if (!TryLong(FilterTestSizeBox.Text, out var size, "测试文件大小")) return;
        var engine = new SyncFilter(Rules: Rules()).CreateEngine();
        var decision = engine.Evaluate(path, new FilterEntryAttributes(size, IsHidden: FilterTestHiddenBox.IsChecked == true, IsSymbolicLink: FilterTestSymlinkBox.IsChecked == true));
        FilterTestResult.Text = decision.Included ? "包含：" + decision.Reason : "排除：" + decision.Reason;
    }
    private void Save(bool newId)
    {
        if (!int.TryParse(ConcurrencyBox.Text, out var concurrency) || concurrency is < 1 or > 64) { MessageBox.Show("最大并发数必须在 1 到 64 之间。", "Profile 设置", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!int.TryParse(TimeToleranceBox.Text, out var tolerance) || tolerance is < 0 or > 86400) { Sections.SelectedIndex = 1; MessageBox.Show("时间容差必须在 0 到 86400 秒之间。", "Profile 设置", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!TryNullableInt(KeepDaysBox.Text, out var keepDays, "保留天数") || !TryNullableInt(MaxVersionsBox.Text, out var maxVersions, "每文件版本数") || !TryTotalBytes(MaxTotalMbBox.Text, out var maxTotalBytes)) { Sections.SelectedIndex = 4; return; }
        var filter = new SyncFilter(Rules: Rules());
        var mode = (SyncMode)Math.Max(0, ModeBox.SelectedIndex);
        var versioning = VersioningBox.SelectedIndex switch { 1 => new VersioningPolicy(VersioningMode.TimestampedArchive, ArchiveBox.Text, keepDays, maxVersions, maxTotalBytes), 2 => new VersioningPolicy(VersioningMode.RecycleBin), _ => new VersioningPolicy() };
        var settings = new ProfileSettings(UseDefaultConcurrency.IsChecked == true ? null : concurrency, UseDefaultCopies.IsChecked == true ? null : VerifyCopies.IsChecked == true, filter, versioning, tolerance);
        _viewModel.Update(p => p with { Name = NameBox.Text.Trim(), Description = DescriptionBox.Text, LeftPath = LeftPathBox.Text.Trim(), RightPath = RightPathBox.Text.Trim(), Mode = mode, Enabled = Enabled.IsChecked == true, Settings = settings });
        var validation = ProfileValidator.Validate(_viewModel.Profile);
        if (!validation.IsValid) { Sections.SelectedIndex = validation.Errors.Any(x => x.Contains("端点") || x.Contains("名称")) ? 0 : 4; MessageBox.Show(string.Join(Environment.NewLine, validation.Errors), "无法保存 Profile", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        SavedProfile = newId ? _viewModel.SaveAsNew() : _viewModel.Profile; DialogResult = true;
    }
    private void RefreshState() { CompatibilityText.Text = _viewModel.Compatibility.CanRun ? "" : "需要修复：" + _viewModel.Compatibility.Summary; DirtyLabel.Text = "编辑副本仅在保存后生效"; }
    private void UseDefaultCopies_Changed(object sender, RoutedEventArgs e) => VerifyCopies.IsEnabled = UseDefaultCopies.IsChecked != true;
    private void UseDefaultConcurrency_Changed(object sender, RoutedEventArgs e) => ConcurrencyBox.IsEnabled = UseDefaultConcurrency.IsChecked != true;
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
        if (RuleGrid.SelectedItem is not EditableFilterRule rule) { FilterTestResult.Text = "请先选择一条规则。"; return; }
        if (!TryLong(RuleMinSizeBox.Text, out var min, "最小字节") || !TryLong(RuleMaxSizeBox.Text, out var max, "最大字节") || !TryDate(RuleAfterBox.Text, out var after, "修改时间下限") || !TryDate(RuleBeforeBox.Text, out var before, "修改时间上限")) return;
        rule.MinimumSizeBytes = min; rule.MaximumSizeBytes = max; rule.ModifiedAfter = after; rule.ModifiedBefore = before; rule.Hidden = RuleHiddenBox.IsChecked == true ? true : null; rule.SymbolicLink = RuleSymlinkBox.IsChecked == true ? true : null; RuleGrid.Items.Refresh();
    }
    private async void PreviewRetention_Click(object sender, RoutedEventArgs e) => await ShowRetentionAsync(cleanup: false);
    private async void CleanupRetention_Click(object sender, RoutedEventArgs e) => await ShowRetentionAsync(cleanup: true);
    private async Task ShowRetentionAsync(bool cleanup)
    {
        try { if (!TryNullableInt(KeepDaysBox.Text, out var days, "保留天数") || !TryNullableInt(MaxVersionsBox.Text, out var versions, "每文件版本数") || !TryTotalBytes(MaxTotalMbBox.Text, out var bytes)) return; var service = new RetentionCleanupService(); var policy = new RetentionPolicy(days, versions, bytes); if (cleanup) { var count = await service.CleanupAsync(ArchiveBox.Text.Trim(), policy); RetentionResult.Text = $"已清理 {count} 个归档版本。"; } else { var candidates = await service.PreviewAsync(ArchiveBox.Text.Trim(), policy); RetentionResult.Text = candidates.Count == 0 ? "没有需要清理的归档版本。" : $"预计清理 {candidates.Count} 项：" + string.Join("；", candidates.Take(3).Select(x => Path.GetFileName(x.Path) + "（" + x.Reason + "）")); } }
        catch (Exception ex) { RetentionResult.Text = "保留策略无效：" + ex.Message; }
    }
    private void SwapEndpoints_Click(object sender, RoutedEventArgs e) => (LeftPathBox.Text, RightPathBox.Text) = (RightPathBox.Text, LeftPathBox.Text);
    private void BrowseLeft_Click(object sender, RoutedEventArgs e) => Browse(LeftPathBox);
    private void BrowseRight_Click(object sender, RoutedEventArgs e) => Browse(RightPathBox);
    private static void Browse(TextBox target)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog() == true) target.Text = dialog.FolderName;
    }
    private async void TestLeft_Click(object sender, RoutedEventArgs e) => await TestEndpointAsync("左侧", LeftPathBox.Text);
    private async void TestRight_Click(object sender, RoutedEventArgs e) => await TestEndpointAsync("右侧", RightPathBox.Text);
    private async Task TestEndpointAsync(string side, string value)
    {
        try
        {
            var endpoint = value.Trim();
            if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("端点不能为空。");
            if (!endpoint.Contains("://", StringComparison.Ordinal))
            {
                if (!Directory.Exists(endpoint)) throw new DirectoryNotFoundException("本地目录不存在或不可访问。");
                EndpointTestResult.Text = $"{side}端点可访问：{endpoint}"; return;
            }
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)) throw new InvalidOperationException("远端地址或端口无效。");
            if (!uri.Scheme.Equals("sftp", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("当前编辑器仅可直接测试本地目录和 SFTP TCP 连通性。请在主窗口验证其他云端连接。");
            using var client = new TcpClient(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(uri.Host, uri.IsDefaultPort ? 22 : uri.Port, timeout.Token);
            EndpointTestResult.Text = $"{side} SFTP 主机 {uri.Host}:{(uri.IsDefaultPort ? 22 : uri.Port)} 可连接。凭据会在实际同步时验证。";
        }
        catch (Exception ex) { EndpointTestResult.Text = $"{side}端点测试失败：{ex.Message}"; }
    }
    private IReadOnlyList<FilterRule> Rules() => _rules.Where(x => !string.IsNullOrWhiteSpace(x.Pattern)).Select(x => x.ToRule()).ToList();
    private bool TryLong(string text, out long? value, string title) { if (string.IsNullOrWhiteSpace(text)) { value = null; return true; } if (long.TryParse(text, out var parsed) && parsed >= 0) { value = parsed; return true; } value = null; FilterTestResult.Text = title + "必须是非负整数。"; return false; }
    private bool TryNullableInt(string text, out int? value, string title) { if (string.IsNullOrWhiteSpace(text)) { value = null; return true; } if (int.TryParse(text, out var parsed) && parsed >= 0) { value = parsed; return true; } value = null; RetentionResult.Text = title + "必须是非负整数。"; return false; }
    private bool TryTotalBytes(string text, out long? value) { if (string.IsNullOrWhiteSpace(text)) { value = null; return true; } if (double.TryParse(text, out var mb) && mb >= 0 && mb <= long.MaxValue / 1024d / 1024d) { value = (long)(mb * 1024 * 1024); return true; } value = null; RetentionResult.Text = "总容量 MB 必须是非负数字。"; return false; }
    private bool TryDate(string text, out DateTimeOffset? value, string title) { if (string.IsNullOrWhiteSpace(text)) { value = null; return true; } if (DateTimeOffset.TryParse(text, out var parsed)) { value = parsed; return true; } value = null; FilterTestResult.Text = title + "必须是有效日期时间。"; return false; }
    private sealed class EditableFilterRule(FilterRuleKind kind, string pattern, string? comment)
    {
        public FilterRuleKind Kind { get; } = kind; public string Pattern { get; set; } = pattern; public string? Comment { get; set; } = comment; public bool Enabled { get; set; } = true; public long? MinimumSizeBytes { get; set; } public long? MaximumSizeBytes { get; set; } public DateTimeOffset? ModifiedAfter { get; set; } public DateTimeOffset? ModifiedBefore { get; set; } public bool? Hidden { get; set; } public bool? SymbolicLink { get; set; }
        public EditableFilterRule(FilterRule rule) : this(rule.Kind, rule.Pattern, rule.Comment) { Enabled = rule.Enabled; MinimumSizeBytes = rule.MinimumSizeBytes; MaximumSizeBytes = rule.MaximumSizeBytes; ModifiedAfter = rule.ModifiedAfter; ModifiedBefore = rule.ModifiedBefore; Hidden = rule.Hidden; SymbolicLink = rule.SymbolicLink; }
        public FilterRule ToRule() => new(Kind, Pattern.Trim(), Comment, Enabled, MinimumSizeBytes, MaximumSizeBytes, ModifiedAfter, ModifiedBefore, Hidden, SymbolicLink);
    }
}
