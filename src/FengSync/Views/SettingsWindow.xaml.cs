using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FengSync.Core;
using FengSync.Core.Configuration;

namespace FengSync.Views;

/// <summary>
/// Settings Center: left-hand navigation + right-hand content. The "General" page is where
/// application settings are actually edited; history and journal logs remain available here.
/// Endpoint, schedule, batch, and about tools live in the main sidebar.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Func<ApplicationSettings, Task> _apply;
    private readonly Func<Task> _configureSftp;
    private readonly Func<Task<int>> _cleanupTemporaryFiles;
    private readonly SyncProfile? _currentProfile;
    private ApplicationSettings _initial;
    private bool _loading;
    private bool _applying;

    public SettingsWindow(
        ApplicationSettings initial,
        Func<ApplicationSettings, Task> apply,
        Func<Task> configureSftp,
        Func<Task<int>> cleanupTemporaryFiles,
        SyncProfile? currentProfile)
    {
        InitializeComponent();
        _initial = initial;
        _apply = apply;
        _configureSftp = configureSftp;
        _cleanupTemporaryFiles = cleanupTemporaryFiles;
        _currentProfile = currentProfile;
        NavigateTo("常规");
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedIndex is applied while InitializeComponent is still constructing
        // the visual tree. At that point ContentHost and the footer buttons have
        // not necessarily been assigned yet; the constructor navigates once the
        // window has finished loading.
        if (ContentHost is null || ApplyButton is null || OkButton is null || DirtyLabel is null) return;
        if (Navigation?.SelectedItem is not ListBoxItem item) return;
        NavigateTo((string)item.Content);
    }

    private void NavigateTo(string page)
    {
        ContentHost.Children.Clear();
        var editable = page == "常规";
        ApplyButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        switch (page)
        {
            case "常规": BuildGeneralPage(); break;
            case "运行历史":
                if (_currentProfile is null)
                    BuildActionPage("运行历史", "打开当前 Profile 的运行历史窗口。", null,
                        "请先在主窗口选择 Profile 后再查看运行历史。");
                else
                    BuildActionPage("运行历史", $"查看 Profile “{_currentProfile.Name}” 的运行历史。",
                        () => new RunHistoryWindow(_currentProfile.Id) { Owner = this }.ShowDialog());
                break;
            case "查看日志":
                BuildActionPage("查看日志", "显示最近未完成的同步作业日志。", () => ShowLogFromOwner());
                break;
        }
        DirtyLabel.Text = "";
    }

    // Stand-alone log viewer mirroring the original ShowLog_Click but driven by the
    // settings center instead of the main window menu.
    private async void ShowLogFromOwner()
    {
        var jobs = await new TaskJournalStore().LoadIncompleteAsync();
        var text = jobs.Count == 0
            ? "没有未完成的同步作业日志。"
            : string.Join(Environment.NewLine + Environment.NewLine, jobs.Select(job =>
                $"作业 {job.JobId}\n开始：{job.CreatedUtc:yyyy-MM-dd HH:mm:ss}\n" +
                string.Join(Environment.NewLine, job.Items.Select(item => $"{item.State,-10} {item.Kind,-24} {item.Path}"))));
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(14)
        };
        new Window { Title = "同步日志", Owner = this, Content = box, Width = 680, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner }
            .ShowDialog();
    }

    private void BuildGeneralPage()
    {
        var sp = new StackPanel { Margin = new Thickness(0) };
        var caption = (Style)Application.Current.Resources["CaptionTextStyle"];
        var section = (Style)Application.Current.Resources["SectionTitleTextStyle"];

        sp.Children.Add(NewHeader("常规", section));
        sp.Children.Add(NewCheckbox("SettingsAutoCheckUpdates", "自动检查更新", _initial.AutoCheckForUpdates));
        sp.Children.Add(NewCheckbox("SettingsShowCompleted", "同步完成后保留进度窗口", _initial.ShowCompleted));
        sp.Children.Add(NewCheckbox("SettingsStartWithWindows", "随 Windows 启动 Feng Sync", _initial.StartWithWindows));

        sp.Children.Add(NewHeader("默认值", section));
        sp.Children.Add(NewLabeledRow("默认最大并发传输数", NewTextBox("SettingsConcurrency", _initial.DefaultMaxConcurrentCopies.ToString()), caption));
        sp.Children.Add(NewCheckbox("SettingsVerifyCopies", "默认在复制后验证文件大小", _initial.DefaultVerifyCopies));
        sp.Children.Add(NewLabeledRow("默认时间容差（秒）", NewTextBox("SettingsTimeTolerance", _initial.DefaultTimeToleranceSeconds.ToString()), caption));
        sp.Children.Add(NewLabeledRow("默认包含规则（每行一条）", NewTextBox("SettingsIncludeRules",
            string.Join(Environment.NewLine, _initial.DefaultFilter.Include ?? []), height: 60, multiline: true), caption));
        sp.Children.Add(NewLabeledRow("默认排除规则（每行一条）", NewTextBox("SettingsExcludeRules",
            string.Join(Environment.NewLine, _initial.DefaultFilter.Exclude ?? []), height: 60, multiline: true), caption));

        var versioningCb = NewComboBox("SettingsVersioningMode", 220, _initial.DefaultVersioning.Mode == FengSync.Core.VersioningMode.TimestampedArchive ? 1 : 0,
            new[] { "永久删除", "版本目录" });
        sp.Children.Add(NewLabeledRow("默认版本策略", versioningCb, caption));
        sp.Children.Add(NewLabeledRow("版本目录", NewTextBox("SettingsArchiveDirectory", _initial.DefaultVersioning.ArchiveDirectory ?? ""), caption));
        sp.Children.Add(NewLabeledRow("保留天数", NewTextBox("SettingsKeepDays", (_initial.DefaultVersioning.KeepDays ?? 30).ToString()), caption));

        sp.Children.Add(NewHeader("通知与日志", section));
        sp.Children.Add(NewLabeledRow("日志保留天数", NewTextBox("SettingsLogRetention", _initial.LogRetentionDays.ToString()), caption));
        sp.Children.Add(NewCheckbox("SettingsNotifyOnCompletion", "同步完成时显示通知", _initial.NotifyOnCompletion));
        sp.Children.Add(NewLabeledRow("网络失败重试次数", NewTextBox("SettingsNetworkRetry", _initial.NetworkRetryCount.ToString()), caption));

        sp.Children.Add(NewHeader("维护", section));
        var sftpBtn = NewButton("SftpServerSettingsButton", "SFTP 服务器设置…", SecondaryStyle(), 36, 180);
        sftpBtn.Click += async (_, _) => await _configureSftp();
        sp.Children.Add(sftpBtn);
        var cleanupBtn = NewButton(null, "清理过期本地临时文件", SecondaryStyle(), 36, 200);
        cleanupBtn.Click += CleanupTemporaryFiles_Click;
        sp.Children.Add(cleanupBtn);
        sp.Children.Add(new TextBlock
        {
            Text = "设置文件：" + Path.Combine(AppDataPaths.Root, "FengSync.local.json"),
            Style = caption,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        ContentHost.Children.Add(sp);
    }

    private void BuildActionPage(string title, string description, Action? onLaunch, string? emptyMessage = null)
    {
        var sp = new StackPanel { MaxWidth = 720 };
        var section = (Style)Application.Current.Resources["SectionTitleTextStyle"];
        var caption = (Style)Application.Current.Resources["CaptionTextStyle"];

        sp.Children.Add(new TextBlock { Text = title, Style = section });
        sp.Children.Add(new TextBlock { Text = description, Style = caption, Margin = new Thickness(0, 8, 0, 16), TextWrapping = TextWrapping.Wrap });

        if (emptyMessage is not null)
        {
            var callout = (Style)Application.Current.Resources["SafetyCalloutStyle"];
            var empty = new Border { Style = callout, Margin = new Thickness(0, 8, 0, 0) };
            empty.Child = new TextBlock { Text = emptyMessage, TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(empty);
        }
        if (onLaunch is not null)
        {
            var launch = NewButton(null, $"打开 {title}", (Style)Application.Current.Resources["PrimaryButtonStyle"], 40, 140);
            launch.Click += (_, _) => onLaunch();
            sp.Children.Add(launch);
        }
        ContentHost.Children.Add(sp);
    }

    private TextBlock NewHeader(string text, Style style) => new() { Text = text, Style = style, Margin = new Thickness(0, 24, 0, 12) };

    private FrameworkElement NewLabeledRow(string label, FrameworkElement input, Style caption)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, Style = caption, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(input);
        return panel;
    }

    private CheckBox NewCheckbox(string id, string text, bool isChecked)
    {
        var cb = new CheckBox { Content = text, Margin = new Thickness(0, 12, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        cb.SetValue(AutomationProperties.AutomationIdProperty, id);
        cb.IsChecked = isChecked;
        cb.Checked += MarkDirty;
        cb.Unchecked += MarkDirty;
        return cb;
    }

    private TextBox NewTextBox(string id, string text, double height = 32, bool multiline = false)
    {
        var tb = new TextBox { Text = text, Height = height, Padding = new Thickness(8, 6, 8, 6), VerticalContentAlignment = VerticalAlignment.Center, AcceptsReturn = multiline };
        tb.SetValue(AutomationProperties.AutomationIdProperty, id);
        tb.TextChanged += MarkDirty;
        return tb;
    }

    private ComboBox NewComboBox(string id, double width, int selectedIndex, string[] items)
    {
        var cb = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        cb.SetValue(AutomationProperties.AutomationIdProperty, id);
        foreach (var item in items) cb.Items.Add(new ComboBoxItem { Content = item });
        cb.SelectedIndex = selectedIndex;
        cb.SelectionChanged += MarkDirty;
        return cb;
    }

    private Style SecondaryStyle() => (Style)Application.Current.Resources["SecondaryButtonStyle"];

    private Button NewButton(string? id, string text, Style style, double height, double minWidth)
    {
        var btn = new Button { Content = text, Style = style, Height = height, MinWidth = minWidth, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        if (id is not null) btn.SetValue(AutomationProperties.AutomationIdProperty, id);
        return btn;
    }

    private void MarkDirty(object sender, RoutedEventArgs e) { if (!_loading) SetDirty(true); }
    private void SetDirty(bool dirty) => DirtyLabel.Text = dirty ? "有未应用的更改" : "";

    private async Task<bool> ApplyAsync()
    {
        if (_applying) return false;
        _applying = true;
        try
        {
            var value = BuildSettings();
            await _apply(value);
            _initial = value;
            SetDirty(false);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "程序设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { _applying = false; }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplyAsync();
    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (await ApplyAsync())
        {
            DialogResult = true;
            Close();
        }
    }

    private ApplicationSettings BuildSettings()
    {
        ApplicationSettings value = _initial;
        foreach (var cb in FindVisualChildren<CheckBox>(ContentHost))
        {
            if (cb.GetValue(AutomationProperties.AutomationIdProperty) is string id)
                value = id switch
                {
                    "SettingsAutoCheckUpdates" => value with { AutoCheckForUpdates = cb.IsChecked == true },
                    "SettingsShowCompleted" => value with { ShowCompleted = cb.IsChecked == true },
                    "SettingsStartWithWindows" => value with { StartWithWindows = cb.IsChecked == true },
                    "SettingsVerifyCopies" => value with { DefaultVerifyCopies = cb.IsChecked == true },
                    "SettingsNotifyOnCompletion" => value with { NotifyOnCompletion = cb.IsChecked == true },
                    _ => value
                };
        }
        foreach (var tb in FindVisualChildren<TextBox>(ContentHost))
            if (tb.GetValue(AutomationProperties.AutomationIdProperty) is string id) value = ApplyTextValue(value, id, tb.Text);
        foreach (var co in FindVisualChildren<ComboBox>(ContentHost))
            if (co.GetValue(AutomationProperties.AutomationIdProperty) is string id)
                value = id switch
                {
                    "SettingsVersioningMode" => value with
                    {
                        DefaultVersioning = co.SelectedIndex == 1
                            ? new VersioningPolicy(FengSync.Core.VersioningMode.TimestampedArchive, value.DefaultVersioning.ArchiveDirectory, value.DefaultVersioning.KeepDays)
                            : new VersioningPolicy(FengSync.Core.VersioningMode.None, value.DefaultVersioning.ArchiveDirectory, value.DefaultVersioning.KeepDays)
                    },
                    _ => value
                };
        var errors = ConfigurationValidator.Validate(value);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        return value;
    }

    private ApplicationSettings ApplyTextValue(ApplicationSettings value, string id, string text)
    {
        try
        {
            return id switch
            {
                "SettingsConcurrency" => int.TryParse(text, out var c) ? value with { DefaultMaxConcurrentCopies = c } : value,
                "SettingsTimeTolerance" => int.TryParse(text, out var t) ? value with { DefaultTimeToleranceSeconds = t } : value,
                "SettingsKeepDays" => int.TryParse(text, out var k) ? value with { DefaultVersioning = value.DefaultVersioning with { KeepDays = k } } : value,
                "SettingsLogRetention" => int.TryParse(text, out var l) ? value with { LogRetentionDays = l } : value,
                "SettingsNetworkRetry" => int.TryParse(text, out var n) ? value with { NetworkRetryCount = n } : value,
                "SettingsIncludeRules" => value with { DefaultFilter = value.DefaultFilter with { Include = Lines(text) } },
                "SettingsExcludeRules" => value with { DefaultFilter = value.DefaultFilter with { Exclude = Lines(text) } },
                "SettingsArchiveDirectory" => value with { DefaultVersioning = value.DefaultVersioning with { ArchiveDirectory = text } },
                _ => value
            };
        }
        catch { return value; }
    }

    private static IReadOnlyList<string> Lines(string text) => text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var grand in FindVisualChildren<T>(child)) yield return grand;
        }
    }

    private async void CleanupTemporaryFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var removed = await _cleanupTemporaryFiles();
            MessageBox.Show($"已清理 {removed} 个超过 7 天的本地临时文件。", "维护", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "维护", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ResetPage_Click(object sender, RoutedEventArgs e)
    {
        _initial = new ApplicationSettings();
        NavigateTo("常规");
    }
}
