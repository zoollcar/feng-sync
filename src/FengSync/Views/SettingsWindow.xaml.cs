using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Localization;
using FengSync.Services;
using Microsoft.Win32;

namespace FengSync.Views;

/// <summary>
/// Settings Center: left-hand navigation + right-hand content. The "General" page is where
/// application settings are actually edited; history and journal logs remain available here.
/// Endpoint, schedule, batch, and about tools live in the main sidebar.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// The pages whose controls are editable and therefore take part in Apply/OK. These are stable ids
    /// rather than the words on the nav buttons: a nav button's text changes with the interface language,
    /// and "which page is on screen" must not.
    /// </summary>
    internal const string GeneralPage = "general";
    internal const string CloudPage = "cloud";
    internal const string HistoryPage = "history";
    internal const string LogsPage = "logs";
    private static readonly string[] EditablePages = [GeneralPage, CloudPage];

    private readonly Func<ApplicationSettings, Task> _apply;
    private readonly Func<Task> _configureSftp;
    private readonly Func<Task<int>> _cleanupTemporaryFiles;
    private readonly SyncProfile? _currentProfile;
    private ApplicationSettings _initial;
    private bool _applying;
    /// <summary>Which page is on screen, so "restore defaults" rebuilds the page the user is looking at.</summary>
    private string _page = GeneralPage;
    /// <summary>Held so the folder picker can write into it without walking the visual tree.</summary>
    private TextBox? _downloadDirectoryBox;

    // A small surface for the tests that drive this window for real: the language test needs to read a live
    // element's Content after a switch, and to move the language combo. Everything else stays private.
    /// <summary>The cancel button — a live XAML binding, and the cheapest proof that a switch repaints.</summary>
    internal Button CancelButton => CancelButtonField;

    /// <summary>The apply button, so a test can press the real one.</summary>
    internal Button ApplyButtonForTest => ApplyButton;

    /// <summary>The language combo on the general page, or null on any other page.</summary>
    internal ComboBox? LanguageCombo => FindVisualChildren<ComboBox>(ContentHost)
        .FirstOrDefault(combo => Equals(AutomationProperties.GetAutomationId(combo), "SettingsLanguage"));

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
        NavigateTo(GeneralPage);
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedIndex is applied while InitializeComponent is still constructing
        // the visual tree. At that point ContentHost and the footer buttons have
        // not necessarily been assigned yet; the constructor navigates once the
        // window has finished loading.
        if (ContentHost is null || ApplyButton is null || OkButton is null || DirtyLabel is null) return;
        if (Navigation?.SelectedItem is not ListBoxItem item) return;
        NavigateTo(item.Tag as string ?? GeneralPage);
    }

    private void NavigateTo(string page)
    {
        _page = page;
        ContentHost.Children.Clear();
        _downloadDirectoryBox = null;
        var editable = EditablePages.Contains(page, StringComparer.Ordinal);
        ApplyButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        switch (page)
        {
            case GeneralPage: BuildGeneralPage(); break;
            case CloudPage: BuildCloudPage(); break;
            case HistoryPage:
                if (_currentProfile is null)
                    BuildActionPage(Tr.F("Settings.PageHistory"), Tr.F("Settings.HistoryOpenCurrent"), null,
                        Tr.F("Settings.HistoryNoProfile"));
                else
                    BuildActionPage(Tr.F("Settings.PageHistory"), Tr.Format("Settings.HistoryForProfile", _currentProfile.Name),
                        () => new RunHistoryWindow(_currentProfile.Id) { Owner = this }.ShowDialog());
                break;
            case LogsPage:
                BuildActionPage(Tr.F("Settings.PageLogs"), Tr.F("Settings.LogsOpenIncomplete"), () => ShowLogFromOwner());
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
            ? Tr.F("Settings.NoIncompleteLogs")
            : string.Join(Environment.NewLine + Environment.NewLine, jobs.Select(job =>
                Tr.Format("Settings.LogEntry", job.JobId, job.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                    string.Join(Environment.NewLine, job.Items.Select(item => $"{item.State,-10} {item.Kind,-24} {item.Path}")))));
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(14)
        };
        new Window { Title = Tr.F("Settings.LogTitle"), Owner = this, Content = box, Width = 680, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner }
            .ShowDialog();
    }

    private void BuildGeneralPage()
    {
        var sp = new StackPanel { Margin = new Thickness(0) };
        var caption = (Style)Application.Current.Resources["CaptionTextStyle"];
        var section = (Style)Application.Current.Resources["SectionTitleTextStyle"];

        sp.Children.Add(NewHeader(Tr.F("Settings.SectionGeneral"), section));
        sp.Children.Add(NewCheckbox("SettingsAutoCheckUpdates", Tr.F("Settings.AutoCheckUpdates"), _initial.AutoCheckForUpdates));
        sp.Children.Add(NewCheckbox("SettingsShowCompleted", Tr.F("Settings.ShowCompleted"), _initial.ShowCompleted));
        sp.Children.Add(NewCheckbox("SettingsStartWithWindows", Tr.F("Settings.StartWithWindows"), _initial.StartWithWindows));

        sp.Children.Add(NewHeader(Tr.F("Settings.SectionDefaults"), section));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.DefaultConcurrency"), NewTextBox("SettingsConcurrency", _initial.DefaultMaxConcurrentCopies.ToString()), caption));
        sp.Children.Add(NewCheckbox("SettingsVerifyCopies", Tr.F("Settings.VerifyCopies"), _initial.DefaultVerifyCopies));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.ResumeMinimum"),
            NewTextBox("SettingsResumeMinimum", ByteSize.Format(_initial.DefaultResumeMinimumBytes)), caption));
        sp.Children.Add(new TextBlock
        {
            Text = Tr.F("Settings.ResumeMinimumHint"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.TimeTolerance"), NewTextBox("SettingsTimeTolerance", _initial.DefaultTimeToleranceSeconds.ToString()), caption));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.IncludeRules"), NewTextBox("SettingsIncludeRules",
            string.Join(Environment.NewLine, _initial.DefaultFilter.Include ?? []), height: 60, multiline: true), caption));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.ExcludeRules"), NewTextBox("SettingsExcludeRules",
            string.Join(Environment.NewLine, _initial.DefaultFilter.Exclude ?? []), height: 60, multiline: true), caption));

        // 0 = permanent delete, 1 = version directory, 2 = recycle bin. The order is shared by the item list here
        // and the save mapping below, so a mode added to one has to be added to the other.
        var versioningCb = NewComboBox("SettingsVersioningMode", 220, _initial.DefaultVersioning.Mode switch
        {
            FengSync.Core.VersioningMode.TimestampedArchive => 1,
            FengSync.Core.VersioningMode.RecycleBin => 2,
            _ => 0
        },
            [Tr.F("Settings.VersioningNone"), Tr.F("Settings.VersioningArchive"), Tr.F("Settings.VersioningRecycle")]);
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.DefaultVersioning"), versioningCb, caption));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.ArchiveDirectory"), NewTextBox("SettingsArchiveDirectory", _initial.DefaultVersioning.ArchiveDirectory ?? ""), caption));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.KeepDays"), NewTextBox("SettingsKeepDays", (_initial.DefaultVersioning.KeepDays ?? 30).ToString()), caption));

        sp.Children.Add(NewHeader(Tr.F("Settings.SectionNotifications"), section));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.LogRetention"), NewTextBox("SettingsLogRetention", _initial.LogRetentionDays.ToString()), caption));
        sp.Children.Add(NewCheckbox("SettingsNotifyOnCompletion", Tr.F("Settings.NotifyOnCompletion"), _initial.NotifyOnCompletion));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.NetworkRetry"), NewTextBox("SettingsNetworkRetry", _initial.NetworkRetryCount.ToString()), caption));

        sp.Children.Add(NewHeader(Tr.F("Settings.SectionAppearance"), section));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.Language"),
            NewComboBox("SettingsLanguage", 220, LanguageIndex(_initial.Language),
                [.. AppLanguageContext.Choices().Select(choice => choice.Label)]), caption));
        sp.Children.Add(new TextBlock
        {
            Text = Tr.F("Settings.LanguageHint"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        sp.Children.Add(NewHeader(Tr.F("Settings.SectionMaintenance"), section));
        var sftpBtn = NewButton("SftpServerSettingsButton", Tr.F("Settings.SftpServer"), SecondaryStyle(), 180);
        sftpBtn.Click += async (_, _) => await _configureSftp();
        sp.Children.Add(sftpBtn);
        var cleanupBtn = NewButton(null, Tr.F("Settings.CleanupTemporary"), SecondaryStyle(), 200);
        cleanupBtn.Click += CleanupTemporaryFiles_Click;
        sp.Children.Add(cleanupBtn);
        sp.Children.Add(new TextBlock
        {
            Text = Tr.Format("Settings.SettingsFile", Path.Combine(AppDataPaths.Root, "FengSync.local.json")),
            Style = caption,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        ContentHost.Children.Add(sp);
    }

    /// <summary>
    /// The cloud manager's own settings. Kept apart from the general page on purpose: these govern an
    /// interactive upload/download queue the user watches, while the general page's concurrency and resume
    /// threshold govern a scheduled Profile run, and tuning one must not silently retune the other.
    /// </summary>
    private void BuildCloudPage()
    {
        var sp = new StackPanel { Margin = new Thickness(0) };
        var caption = (Style)Application.Current.Resources["CaptionTextStyle"];
        var section = (Style)Application.Current.Resources["SectionTitleTextStyle"];

        sp.Children.Add(NewHeader(Tr.F("Settings.PageCloud"), section));
        sp.Children.Add(new TextBlock
        {
            Text = Tr.F("Settings.CloudIntro"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap
        });
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.CloudConcurrency"), NewTextBox("SettingsCloudConcurrency", _initial.CloudMaxConcurrentTransfers.ToString()), caption));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.CloudRetry"), NewTextBox("SettingsCloudRetry", _initial.CloudRetryCount.ToString()), caption));
        sp.Children.Add(new TextBlock
        {
            Text = Tr.F("Settings.CloudConcurrencyHint"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        sp.Children.Add(NewHeader(Tr.F("Settings.SectionRateAndWindow"), section));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.UploadLimit"), NewTextBox("SettingsCloudUploadLimit",
            _initial.CloudUploadLimitBytesPerSecond > 0 ? ByteSize.Format(_initial.CloudUploadLimitBytesPerSecond) : ""), caption));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.DownloadLimit"), NewTextBox("SettingsCloudDownloadLimit",
            _initial.CloudDownloadLimitBytesPerSecond > 0 ? ByteSize.Format(_initial.CloudDownloadLimitBytesPerSecond) : ""), caption));
        sp.Children.Add(new TextBlock
        {
            Text = Tr.F("Settings.RateLimitHint"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.WindowStart"), NewTextBox("SettingsCloudWindowStart",
            _initial.CloudTransferTimeWindow?.Start.ToString("HH\\:mm") ?? ""), caption));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.WindowEnd"), NewTextBox("SettingsCloudWindowEnd",
            _initial.CloudTransferTimeWindow?.End.ToString("HH\\:mm") ?? ""), caption));
        sp.Children.Add(new TextBlock
        {
            Text = Tr.F("Settings.WindowHint"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        var afterTransfer = NewComboBox("SettingsCloudAfterTransfer", 220, (int)_initial.CloudAfterTransfer,
            [.. Enum.GetValues<CloudAfterTransferAction>().Select(CloudPostTransferAction.Describe)]);
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.AfterTransfer"), afterTransfer, caption));
        sp.Children.Add(new TextBlock
        {
            Text = Tr.F("Settings.AfterTransferHint"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        sp.Children.Add(NewHeader(Tr.F("Settings.SectionProxy"), section));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.ProxyServer"), NewTextBox("SettingsCloudProxy", _initial.RcloneProxy ?? ""), caption));
        sp.Children.Add(new TextBlock
        {
            Text = Tr.F("Settings.ProxyHint"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        sp.Children.Add(NewHeader(Tr.F("Settings.SectionDownload"), section));
        var folder = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        folder.Children.Add(new TextBlock { Text = Tr.F("Settings.DefaultDownloadDirectory"), Style = caption, Margin = new Thickness(0, 0, 0, 6) });
        var dock = new DockPanel();
        var browse = NewButton("SettingsCloudDownloadBrowse", Tr.F("Settings.Browse"), SecondaryStyle(), 90);
        browse.Click += BrowseDownloadDirectory_Click;
        DockPanel.SetDock(browse, Dock.Right);
        dock.Children.Add(browse);
        _downloadDirectoryBox = NewTextBox("SettingsCloudDownloadDirectory", _initial.CloudDownloadDirectory ?? "");
        dock.Children.Add(_downloadDirectoryBox);
        folder.Children.Add(dock);
        folder.Children.Add(new TextBlock
        {
            Text = Tr.F("Settings.DownloadHint"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        sp.Children.Add(folder);

        sp.Children.Add(NewHeader(Tr.F("Settings.SectionUpload"), section));
        var conflict = NewComboBox("SettingsCloudConflict", 220, ConflictIndex(_initial.CloudUploadConflict),
            [Tr.F("Settings.ConflictAsk"), Tr.F("Settings.ConflictOverwrite"), Tr.F("Settings.ConflictSkip"), Tr.F("Settings.ConflictKeepBoth")]);
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.ConflictTarget"), conflict, caption));

        sp.Children.Add(NewHeader(Tr.F("Settings.SectionBehaviour"), section));
        sp.Children.Add(NewCheckbox("SettingsCloudNotify", Tr.F("Settings.CloudNotify"), _initial.CloudNotifyOnCompletion));
        sp.Children.Add(NewCheckbox("SettingsCloudResumeOnStartup", Tr.F("Settings.CloudResumeOnStartup"), _initial.CloudResumeOnStartup));

        sp.Children.Add(NewHeader(Tr.F("Settings.SectionMount"), section));
        sp.Children.Add(NewLabeledRow(Tr.F("Settings.MountCacheLimit"), NewTextBox("SettingsMountCacheLimit",
            _initial.MountCacheMaxSizeBytes > 0 ? ByteSize.Format(_initial.MountCacheMaxSizeBytes) : ""), caption));
        sp.Children.Add(new TextBlock
        {
            Text = Tr.F("Settings.MountCacheHint"),
            Style = caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        ContentHost.Children.Add(sp);
    }

    private void BrowseDownloadDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Tr.F("Settings.PickDownloadDirectory") };
        if (dialog.ShowDialog(this) != true) return;
        if (_downloadDirectoryBox is not null) _downloadDirectoryBox.Text = dialog.FolderName;
    }

    internal static int ConflictIndex(CloudUploadConflictPolicy policy) => policy switch
    {
        CloudUploadConflictPolicy.Overwrite => 1,
        CloudUploadConflictPolicy.Skip => 2,
        CloudUploadConflictPolicy.KeepBoth => 3,
        _ => 0
    };

    internal static CloudUploadConflictPolicy ConflictPolicy(int index) => index switch
    {
        1 => CloudUploadConflictPolicy.Overwrite,
        2 => CloudUploadConflictPolicy.Skip,
        3 => CloudUploadConflictPolicy.KeepBoth,
        _ => CloudUploadConflictPolicy.Ask
    };

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
            var launch = NewButton(null, Tr.Format("Settings.OpenAction", title), (Style)Application.Current.Resources["PrimaryButtonStyle"], 140);
            AutomationProperties.SetAutomationId(launch, title == Tr.F("Settings.PageHistory") ? "OpenRunHistory" : "OpenLogViewer");
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

    private Button NewButton(string? id, string text, Style style, double minWidth)
    {
        var btn = new Button { Content = text, Style = style, MinWidth = minWidth, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        if (id is not null) btn.SetValue(AutomationProperties.AutomationIdProperty, id);
        return btn;
    }

    private void MarkDirty(object sender, RoutedEventArgs e) => SetDirty(true);
    private void SetDirty(bool dirty) => DirtyLabel.Text = dirty ? Tr.F("Settings.Dirty") : "";

    private async Task<bool> ApplyAsync()
    {
        if (_applying) return false;
        _applying = true;
        try
        {
            var value = BuildSettings();
            await _apply(value);
            // This page's text was built by Tr.F when the page was built, so a language change means the
            // page has to be built again — rebuilt on the page the user is on, not on the first one.
            var languageChanged = value.Language != _initial.Language;
            _initial = value;
            SetDirty(false);
            if (languageChanged) NavigateTo(_page);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Tr.F("Settings.MessageTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    "SettingsCloudNotify" => value with { CloudNotifyOnCompletion = cb.IsChecked == true },
                    "SettingsCloudResumeOnStartup" => value with { CloudResumeOnStartup = cb.IsChecked == true },
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
                        // The archive directory and keep-days survive a switch to another mode, so toggling to the
                        // recycle bin and back does not quietly discard the folder the user configured.
                        DefaultVersioning = new VersioningPolicy(co.SelectedIndex switch
                        {
                            1 => FengSync.Core.VersioningMode.TimestampedArchive,
                            2 => FengSync.Core.VersioningMode.RecycleBin,
                            _ => FengSync.Core.VersioningMode.None
                        }, value.DefaultVersioning.ArchiveDirectory, value.DefaultVersioning.KeepDays)
                    },
                    "SettingsCloudConflict" => value with { CloudUploadConflict = ConflictPolicy(co.SelectedIndex) },
                    "SettingsCloudAfterTransfer" => value with { CloudAfterTransfer = AfterTransferPolicy(co.SelectedIndex) },
                    "SettingsLanguage" => value with { Language = LanguagePolicy(co.SelectedIndex) },
                    _ => value
                };
        // The transfer window is parsed as a pair rather than as two independent boxes: half a window is not a
        // window, and reading them separately would let 09:00 + (empty) become "any time" silently.
        value = ApplyTransferWindow(value);
        var errors = ConfigurationValidator.Validate(value);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        return value;
    }

    /// <summary>
    /// Reads the two time boxes. Both empty means "any time"; one empty or an unreadable value is an error the
    /// user has to fix, rather than a window quietly reinterpreted as something they did not ask for.
    /// </summary>
    private ApplicationSettings ApplyTransferWindow(ApplicationSettings value)
    {
        var start = FindText("SettingsCloudWindowStart")?.Trim() ?? "";
        var end = FindText("SettingsCloudWindowEnd")?.Trim() ?? "";
        if (start.Length == 0 && end.Length == 0) return value with { CloudTransferTimeWindow = null };
        var window = CloudTransferTimeWindow.Parse(start, end);
        if (window is null) throw new InvalidOperationException(Tr.F("Settings.TransferWindowFormat"));
        return value with { CloudTransferTimeWindow = window };
    }

    private string? FindText(string id)
    {
        foreach (var box in FindVisualChildren<TextBox>(ContentHost))
            if (box.GetValue(AutomationProperties.AutomationIdProperty) is string found && found == id) return box.Text;
        return null;
    }

    /// <summary>
    /// The combo's selected index, for the two languages. Out of range lands on the default rather than
    /// throwing: a stale index is a display problem, and the validator is what reports a bad stored value.
    /// </summary>
    internal static int LanguageIndex(AppLanguage language) =>
        language == AppLanguage.English ? 1 : 0;

    internal static AppLanguage LanguagePolicy(int index) =>
        index == 1 ? AppLanguage.English : AppLanguage.Chinese;

    internal static CloudAfterTransferAction AfterTransferPolicy(int index) =>
        Enum.IsDefined(typeof(CloudAfterTransferAction), index) ? (CloudAfterTransferAction)index : CloudAfterTransferAction.None;

    private ApplicationSettings ApplyTextValue(ApplicationSettings value, string id, string text)
    {
        try
        {
            return id switch
            {
                "SettingsConcurrency" => int.TryParse(text, out var c) ? value with { DefaultMaxConcurrentCopies = c } : value,
                // An unreadable size becomes a sentinel so ConfigurationValidator reports it, rather
                // than silently keeping the previous value while the box shows something else.
                "SettingsResumeMinimum" => value with { DefaultResumeMinimumBytes = ByteSize.TryParse(text, out var resume) ? resume : -1 },
                "SettingsTimeTolerance" => int.TryParse(text, out var t) ? value with { DefaultTimeToleranceSeconds = t } : value,
                "SettingsKeepDays" => int.TryParse(text, out var k) ? value with { DefaultVersioning = value.DefaultVersioning with { KeepDays = k } } : value,
                "SettingsLogRetention" => int.TryParse(text, out var l) ? value with { LogRetentionDays = l } : value,
                "SettingsNetworkRetry" => int.TryParse(text, out var n) ? value with { NetworkRetryCount = n } : value,
                "SettingsCloudConcurrency" => int.TryParse(text, out var cloudCopies) ? value with { CloudMaxConcurrentTransfers = cloudCopies } : value,
                "SettingsCloudRetry" => int.TryParse(text, out var cloudRetries) ? value with { CloudRetryCount = cloudRetries } : value,
                "SettingsCloudDownloadDirectory" => value with { CloudDownloadDirectory = text.Trim() },
                // An empty box means "no limit", which is not the same as an unreadable one: only the latter
                // becomes the sentinel the validator reports.
                "SettingsCloudUploadLimit" => value with { CloudUploadLimitBytesPerSecond = ParseLimit(text) },
                "SettingsCloudDownloadLimit" => value with { CloudDownloadLimitBytesPerSecond = ParseLimit(text) },
                "SettingsMountCacheLimit" => value with { MountCacheMaxSizeBytes = ParseLimit(text) },
                "SettingsCloudProxy" => value with { RcloneProxy = text.Trim() },
                "SettingsIncludeRules" => value with { DefaultFilter = value.DefaultFilter with { Include = Lines(text) } },
                "SettingsExcludeRules" => value with { DefaultFilter = value.DefaultFilter with { Exclude = Lines(text) } },
                "SettingsArchiveDirectory" => value with { DefaultVersioning = value.DefaultVersioning with { ArchiveDirectory = text } },
                _ => value
            };
        }
        catch { return value; }
    }

    private static IReadOnlyList<string> Lines(string text) => text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Reads a byte-rate or byte-size box. Empty, "0" and "off" all mean no limit; anything unreadable becomes
    /// -1 so <see cref="ConfigurationValidator"/> reports it instead of the box showing one thing and the file
    /// holding another.
    /// </summary>
    internal static long ParseLimit(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0 || value == "0" || value.Equals("off", StringComparison.OrdinalIgnoreCase)) return 0;
        return ByteSize.TryParse(value, out var bytes) ? bytes : -1;
    }

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
            MessageBox.Show(Tr.Format("Settings.CleanupDone", removed), Tr.F("Settings.MaintenanceTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, Tr.F("Settings.MaintenanceTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ResetPage_Click(object sender, RoutedEventArgs e)
    {
        _initial = new ApplicationSettings();
        // Rebuild the page the user is on: resetting a page must not navigate them away from it.
        NavigateTo(_page);
    }
}
