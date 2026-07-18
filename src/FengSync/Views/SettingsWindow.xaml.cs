using System.Windows;
using System.Windows.Controls;
using System.IO;
using FengSync.Core;
using FengSync.Core.Configuration;

namespace FengSync.Views;

/// <summary>Edits an isolated copy of application settings; only Apply/OK invokes the owner callback.</summary>
public partial class SettingsWindow : Window
{
    private readonly Func<ApplicationSettings, Task> _apply;
    private readonly Func<Task> _configureSftp;
    private ApplicationSettings _initial;
    private bool _loading;

    public SettingsWindow(ApplicationSettings initial, Func<ApplicationSettings, Task> apply, Func<Task> configureSftp)
    {
        InitializeComponent();
        _initial = initial; _apply = apply; _configureSftp = configureSftp;
        LoadValues(initial);
        StoragePath.Text = "设置文件：" + Path.Combine(AppDataPaths.Root, "FengSync.local.json");
    }

    private void LoadValues(ApplicationSettings value)
    {
        _loading = true;
        ShowCompleted.IsChecked = value.ShowCompleted; StartWithWindows.IsChecked = value.StartWithWindows;
        Concurrency.Text = value.DefaultMaxConcurrentCopies.ToString(); VerifyCopies.IsChecked = value.DefaultVerifyCopies;
        TimeTolerance.Text = value.DefaultTimeToleranceSeconds.ToString();
        IncludeRules.Text = string.Join(Environment.NewLine, value.DefaultFilter.Include ?? []);
        ExcludeRules.Text = string.Join(Environment.NewLine, value.DefaultFilter.Exclude ?? []);
        VersioningMode.SelectedIndex = value.DefaultVersioning.Mode == FengSync.Core.VersioningMode.TimestampedArchive ? 1 : 0;
        ArchiveDirectory.Text = value.DefaultVersioning.ArchiveDirectory ?? ""; KeepDays.Text = value.DefaultVersioning.KeepDays.ToString();
        LogRetention.Text = value.LogRetentionDays.ToString(); NotifyOnCompletion.IsChecked = value.NotifyOnCompletion;
        NetworkRetry.Text = value.NetworkRetryCount.ToString(); _loading = false; SetDirty(false);
    }

    private ApplicationSettings BuildSettings()
    {
        if (!int.TryParse(Concurrency.Text, out var concurrency) || !int.TryParse(TimeTolerance.Text, out var tolerance)
            || !int.TryParse(KeepDays.Text, out var keepDays) || !int.TryParse(LogRetention.Text, out var retention)
            || !int.TryParse(NetworkRetry.Text, out var retries)) throw new InvalidOperationException("并发、时间容差、保留天数和重试次数必须为整数。");
        var versioning = VersioningMode.SelectedIndex == 1
            ? new VersioningPolicy(FengSync.Core.VersioningMode.TimestampedArchive, ArchiveDirectory.Text.Trim(), keepDays)
            : new VersioningPolicy(FengSync.Core.VersioningMode.None, null, keepDays);
        var result = _initial with
        {
            ShowCompleted = ShowCompleted.IsChecked == true, StartWithWindows = StartWithWindows.IsChecked == true,
            DefaultMaxConcurrentCopies = concurrency, DefaultVerifyCopies = VerifyCopies.IsChecked == true,
            DefaultTimeToleranceSeconds = tolerance,
            DefaultFilter = new SyncFilter(Lines(IncludeRules.Text), Lines(ExcludeRules.Text)), DefaultVersioning = versioning,
            LogRetentionDays = retention, NotifyOnCompletion = NotifyOnCompletion.IsChecked == true, NetworkRetryCount = retries
        };
        var errors = ConfigurationValidator.Validate(result);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        if (versioning.Mode == FengSync.Core.VersioningMode.TimestampedArchive && string.IsNullOrWhiteSpace(versioning.ArchiveDirectory))
            throw new InvalidOperationException("默认版本策略为“版本目录”时必须填写版本目录。");
        return result;
    }

    private async Task<bool> ApplyAsync()
    {
        try { var value = BuildSettings(); await _apply(value); _initial = value; SetDirty(false); return true; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "程序设置", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyAsync();
    private async void Ok_Click(object sender, RoutedEventArgs e) { if (await ApplyAsync()) DialogResult = true; }
    private async void ConfigureSftp_Click(object sender, RoutedEventArgs e) => await _configureSftp();
    private void ResetPage_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new ApplicationSettings();
        _loading = true;
        switch (Pages.SelectedIndex)
        {
            case 0: ShowCompleted.IsChecked = defaults.ShowCompleted; StartWithWindows.IsChecked = defaults.StartWithWindows; break;
            case 1: Concurrency.Text = defaults.DefaultMaxConcurrentCopies.ToString(); VerifyCopies.IsChecked = defaults.DefaultVerifyCopies; TimeTolerance.Text = defaults.DefaultTimeToleranceSeconds.ToString(); IncludeRules.Text = ""; ExcludeRules.Text = ""; VersioningMode.SelectedIndex = 0; ArchiveDirectory.Text = ""; KeepDays.Text = defaults.DefaultVersioning.KeepDays.ToString(); break;
            case 2: LogRetention.Text = defaults.LogRetentionDays.ToString(); NotifyOnCompletion.IsChecked = defaults.NotifyOnCompletion; break;
            case 3: NetworkRetry.Text = defaults.NetworkRetryCount.ToString(); break;
        }
        _loading = false; SetDirty(true);
    }
    private void PageChanged(object sender, SelectionChangedEventArgs e) { if (e.Source == Pages) { /* Reset applies only to the active category. */ } }
    private void MarkDirty(object sender, RoutedEventArgs e) { if (!_loading) SetDirty(true); }
    private void SetDirty(bool dirty) => DirtyLabel.Text = dirty ? "有未应用的更改" : "";
    private static IReadOnlyList<string> Lines(string text) => text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
