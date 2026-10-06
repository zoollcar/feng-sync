using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FengSync.Core.Configuration;
using FengSync.Core.Mount;
using FengSync.Localization;

namespace FengSync.Views;

/// <summary>
/// Modal "挂载到…" dialog. The user picks an unused drive letter, supplies an absolute directory path that
/// does not yet exist (Feng Sync never creates it), or names a UNC network share; validation is live so the OK
/// button only enables when the target is genuinely usable. The 高级选项 section collects this one mount's
/// <see cref="MountOptionsSettings"/>.
/// </summary>
/// <remarks>
/// <para>
/// A UNC target is deliberately its own tab rather than "a directory with a different spelling": rclone hands
/// the path to Windows, which allocates a drive letter and answers with it, so the mount point that ends up
/// existing is <b>not</b> the one that was asked for. The hint says so instead of promising the path back.
/// </para>
/// <para>
/// The advanced values are parsed here rather than left to rclone. rclone ignores an option it cannot match to
/// a field, so a typo in "2G" would otherwise produce a perfectly healthy mount running with the wrong cache
/// ceiling — see <see cref="MountOptionsSettings"/> for the measurements behind that claim.
/// </para>
/// </remarks>
public partial class MountPickerDialog : Window
{
    private readonly string _remoteName;
    private readonly string _provider;
    private readonly string _rootPath;
    private readonly IReadOnlyList<string> _occupiedMountPoints;
    /// <summary>The application-wide cache ceiling this dialog was seeded with, and what an empty box means.</summary>
    private readonly long _defaultCacheMaxSizeBytes;
    private MountPickerMode _mode = MountPickerMode.DriveLetter;

    public MountTarget? SelectedTarget { get; private set; }

    /// <summary>
    /// The options this mount should be created with. Never <see langword="null"/> once the dialog is
    /// confirmed: the picker resolves the application-wide cache ceiling into a concrete number, so a mount
    /// created here keeps the settings it was created under even if the application setting changes later.
    /// </summary>
    public MountOptionsSettings? SelectedOptions { get; private set; }

    /// <param name="rootPath">Path handed to rclone; empty for an alias-backed endpoint, whose root already is the prefix.</param>
    /// <param name="displayRoot">
    /// What to show the user instead, when the mount root and the visible prefix differ. Without it an
    /// enforced-prefix endpoint would read as <c>name:/</c> and hide which prefix it is confined to.
    /// </param>
    /// <param name="defaultCacheMaxSizeBytes">
    /// The cache ceiling from the application settings, used to seed the advanced box and as the meaning of an
    /// empty one. 0 means "no ceiling", which the builder expresses by omitting the key.
    /// </param>
    public MountPickerDialog(
        string remoteName,
        string provider,
        IReadOnlyList<string> occupiedMountPoints,
        string rootPath = "",
        string? displayRoot = null,
        long defaultCacheMaxSizeBytes = MountOptions.DefaultCacheMaxSizeBytes)
    {
        InitializeComponent();
        _remoteName = remoteName;
        _provider = provider;
        _rootPath = rootPath.Trim('/');
        _occupiedMountPoints = occupiedMountPoints;
        _defaultCacheMaxSizeBytes = defaultCacheMaxSizeBytes;
        HeaderText.Text = Tr.Format("Mount.Header", _remoteName);
        var shown = string.IsNullOrWhiteSpace(displayRoot) ? _rootPath : displayRoot.Trim().Trim('/');
        SubHeaderText.Text = Tr.Format("Mount.SubHeader", _remoteName, shown);
        DriveLetterList.ItemsSource = MountPointInspector.EnumerateDriveLetters();
        DriveHint.Text = Tr.F("Mount.DriveHint");
        DirectoryHint.Text = Tr.F("Mount.DirectoryHint");
        UncHint.Text = Tr.F("Mount.UncHint");
        CapacityHint.Text = MountCapacity.DescribeUpcoming();
        BuildCacheModeOptions();
        SeedAdvancedDefaults();
        UpdateStatus();
    }

    private void ModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _mode = ModeTabs.SelectedIndex switch
        {
            1 => MountPickerMode.Directory,
            2 => MountPickerMode.NetworkShare,
            _ => MountPickerMode.DriveLetter
        };
        UpdateStatus();
    }

    private void DriveLetterList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStatus();

    private void DirectoryPathBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateStatus();

    private void UncPathBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateStatus();

    /// <summary>Every advanced control routes here, so the summary line and the OK button never lag the input.</summary>
    private void Advanced_Changed(object sender, RoutedEventArgs e) => UpdateStatus();

    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        // Win32 OpenFolderDialog only lets the user pick an existing folder; the rclone mount target
        // must NOT exist yet. We show the parent and let the user type the missing folder name.
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Tr.F("Mount.PickParentTitle"),
        };
        if (!string.IsNullOrWhiteSpace(DirectoryPathBox.Text))
        {
            try
            {
                var existing = Directory.Exists(DirectoryPathBox.Text) ? DirectoryPathBox.Text : Path.GetDirectoryName(DirectoryPathBox.Text);
                if (!string.IsNullOrEmpty(existing) && Directory.Exists(existing)) dialog.InitialDirectory = existing;
            }
            catch { /* fall back to default */ }
        }
        if (dialog.ShowDialog(this) == true)
        {
            DirectoryPathBox.Text = Path.Combine(dialog.FolderName, _remoteName + "_mount");
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        MountTarget target;
        switch (_mode)
        {
            case MountPickerMode.DriveLetter:
            {
                if (DriveLetterList.SelectedItem is not MountPointInspector.DriveLetterOption option) return;
                var validation = MountPointInspector.Validate(option.Letter, MountTargetKind.DriveLetter, _occupiedMountPoints);
                if (!validation.IsValid) { StatusText.Text = validation.Error!; return; }
                target = new MountTarget(_remoteName, _provider, option.Letter, MountTargetKind.DriveLetter, _rootPath);
                break;
            }
            case MountPickerMode.Directory:
            {
                var path = DirectoryPathBox.Text?.Trim() ?? "";
                var validation = MountPointInspector.Validate(path, MountTargetKind.Directory, _occupiedMountPoints);
                if (!validation.IsValid) { StatusText.Text = validation.Error!; return; }
                target = new MountTarget(_remoteName, _provider, Path.GetFullPath(path), MountTargetKind.Directory, _rootPath);
                break;
            }
            default:
            {
                var path = UncPathBox.Text?.Trim() ?? "";
                var validation = MountPointInspector.Validate(path, MountTargetKind.NetworkShare, _occupiedMountPoints);
                if (!validation.IsValid) { StatusText.Text = validation.Error!; return; }
                // The requested path is kept as typed: it is the UNC share, and Windows picks the drive letter.
                target = new MountTarget(_remoteName, _provider, path, MountTargetKind.NetworkShare, _rootPath);
                break;
            }
        }
        SelectedTarget = target;
        SelectedOptions = BuildOptions();
        DialogResult = true;
    }

    private void UpdateStatus()
    {
        var options = BuildOptions();
        AdvancedSummary.Text = Describe(options);
        CacheModeHint.Text = MountOptions.DescribeCacheMode(options.CacheMode ?? MountOptions.DefaultCacheMode);

        // An unreadable advanced value stops the dialog here instead of being dropped: see the class remarks.
        if (DescribeAdvancedProblem() is { } problem)
        {
            StatusText.Text = problem;
            OkButton.IsEnabled = false;
            return;
        }

        switch (_mode)
        {
            case MountPickerMode.DriveLetter:
            {
                if (DriveLetterList.SelectedItem is not MountPointInspector.DriveLetterOption option)
                {
                    StatusText.Text = Tr.F("Mount.StatusPickDrive");
                    OkButton.IsEnabled = false;
                    return;
                }
                // Deliberately the same rule the other branches use, rather than reading IsAvailable alone:
                // a letter already claimed by an existing mount has to be refused while the user is choosing,
                // not after they press OK.
                var validation = MountPointInspector.Validate(option.Letter, MountTargetKind.DriveLetter, _occupiedMountPoints);
                StatusText.Text = validation.IsValid ? Tr.Format("Mount.StatusWillMountDrive", option.Letter) : validation.Error!;
                OkButton.IsEnabled = validation.IsValid;
                return;
            }
            case MountPickerMode.Directory:
            {
                var path = DirectoryPathBox.Text?.Trim() ?? "";
                if (path.Length == 0) { StatusText.Text = Tr.F("Mount.StatusEnterDirectory"); OkButton.IsEnabled = false; return; }
                var validation = MountPointInspector.Validate(path, MountTargetKind.Directory, _occupiedMountPoints);
                StatusText.Text = validation.IsValid ? Tr.F("Mount.StatusDirectoryValid") : validation.Error!;
                OkButton.IsEnabled = validation.IsValid;
                return;
            }
            default:
            {
                var path = UncPathBox.Text?.Trim() ?? "";
                if (path.Length == 0)
                {
                    StatusText.Text = Tr.F("Mount.StatusEnterUnc");
                    OkButton.IsEnabled = false;
                    return;
                }
                var validation = MountPointInspector.Validate(path, MountTargetKind.NetworkShare, _occupiedMountPoints);
                StatusText.Text = validation.IsValid
                    ? Tr.F("Mount.StatusUncValid")
                    : validation.Error!;
                OkButton.IsEnabled = validation.IsValid;
                return;
            }
        }
    }

    private void BuildCacheModeOptions()
    {
        CacheModeOption[] options =
        [
            new(MountCacheMode.Off, Tr.F("Mount.CacheModeOff")),
            new(MountCacheMode.Minimal, Tr.F("Mount.CacheModeMinimal")),
            new(MountCacheMode.Writes, Tr.F("Mount.CacheModeWrites")),
            new(MountCacheMode.Full, Tr.F("Mount.CacheModeFull"))
        ];
        CacheModeBox.ItemsSource = options;
        CacheModeBox.DisplayMemberPath = nameof(CacheModeOption.Label);
        CacheModeBox.SelectedIndex = Array.FindIndex(options, x => x.Mode == MountOptions.DefaultCacheMode);
        CacheModeHint.Text = MountOptions.DescribeCacheMode(MountOptions.DefaultCacheMode);
    }

    private void SeedAdvancedDefaults()
    {
        // An unset application default is 0, i.e. "no ceiling", and an empty box means exactly that. A real
        // ceiling is shown formatted so the user can see the number they are inheriting and edit it.
        CacheMaxSizeBox.Text = _defaultCacheMaxSizeBytes > 0 ? ByteSize.Format(_defaultCacheMaxSizeBytes) : "";
    }

    /// <summary>The options this dialog would send right now. Also what the summary line describes.</summary>
    private MountOptionsSettings BuildOptions() => new()
    {
        VolumeName = Text(VolumeNameBox.Text),
        NetworkMode = NetworkModeBox.IsChecked == true,
        CacheMode = (CacheModeBox.SelectedItem as CacheModeOption)?.Mode ?? MountOptions.DefaultCacheMode,
        // An empty box means "whatever the settings page says", which is what the picker seeded it with.
        CacheMaxSizeBytes = string.IsNullOrWhiteSpace(CacheMaxSizeBox.Text)
            ? _defaultCacheMaxSizeBytes
            : ParseBytes(CacheMaxSizeBox.Text),
        CacheMaxAge = ParseSeconds(CacheMaxAgeBox.Text),
        WriteBack = ParseSeconds(WriteBackBox.Text),
        DirCacheTime = ParseSeconds(DirCacheTimeBox.Text),
        PollInterval = ParseSeconds(PollIntervalBox.Text),
        ReadOnly = ReadOnlyBox.IsChecked == true,
        NoChecksum = NoChecksumBox.IsChecked == true,
        NoModTime = NoModTimeBox.IsChecked == true,
        NoSeek = NoSeekBox.IsChecked == true
    };

    /// <summary>
    /// The line that says out loud what is about to be sent. It exists because the whole option set is
    /// invisible otherwise — the mount succeeds either way, including when a name is wrong.
    /// </summary>
    private static string Describe(MountOptionsSettings options)
    {
        var parts = new List<string>();
        if (options.VolumeName is { } volume) parts.Add(Tr.Format("Mount.SummaryVolume", volume));
        if (options.NetworkMode) parts.Add(Tr.F("Mount.SummaryNetworkMode"));
        // The cache mode is named here by its rclone flag, not by the combo box's label: this line says what
        // is about to be sent, and the label only says what the user picked from.
        parts.Add(Tr.Format("Mount.SummaryCacheMode", MountOptions.WireCacheMode(options.CacheMode ?? MountOptions.DefaultCacheMode)));
        parts.Add(options.CacheMaxSizeBytes is > 0
            ? Tr.Format("Mount.SummaryCacheMaxSize", ByteSize.Format(options.CacheMaxSizeBytes.Value))
            : Tr.F("Mount.SummaryCacheMaxSizeNone"));
        if (options.CacheMaxAge is { } age) parts.Add(Tr.Format("Mount.SummaryCacheMaxAge", age.TotalSeconds.ToString("0")));
        if (options.WriteBack is { } writeBack) parts.Add(Tr.Format("Mount.SummaryWriteBack", writeBack.TotalSeconds.ToString("0")));
        if (options.DirCacheTime is { } dirCache) parts.Add(Tr.Format("Mount.SummaryDirCacheTime", dirCache.TotalSeconds.ToString("0")));
        if (options.PollInterval is { } poll) parts.Add(Tr.Format("Mount.SummaryPollInterval", poll.TotalSeconds.ToString("0")));
        if (options.ReadOnly) parts.Add(Tr.F("Mount.SummaryReadOnly"));
        if (!options.NoChecksum) parts.Add(Tr.F("Mount.SummaryCompareChecksum"));
        if (!options.NoModTime) parts.Add(Tr.F("Mount.SummaryModTime"));
        if (options.NoSeek) parts.Add(Tr.F("Mount.SummaryNoSeek"));

        var text = Tr.Format("Mount.Summary", string.Join(Tr.F("Mount.SummarySeparator"), parts));
        // Warnings, not refusals: rclone accepts these combinations and enforces nothing.
        var warnings = MountOptions.Warnings(options);
        return warnings.Count == 0 ? text : text + Tr.F("Mount.SummaryWarning") + string.Join(" ", warnings);
    }

    /// <summary>
    /// Why the advanced section cannot be used as typed, or <see langword="null"/> when it can. rclone would
    /// answer a malformed value with an error, but only after the user pressed OK and the dialog had closed —
    /// and it would answer a malformed <i>name</i> with a successful mount, which is worse.
    /// </summary>
    private string? DescribeAdvancedProblem()
    {
        if (!TryParseBytes(CacheMaxSizeBox.Text, out _, out var bytesError)) return Tr.Format("Mount.ProblemCacheMaxSize", bytesError);
        foreach (var (label, box) in new (string Label, TextBox Box)[]
        {
            (Tr.F("Mount.LabelCacheMaxAge"), CacheMaxAgeBox),
            (Tr.F("Mount.LabelWriteBack"), WriteBackBox),
            (Tr.F("Mount.LabelDirCacheTime"), DirCacheTimeBox),
            (Tr.F("Mount.LabelPollInterval"), PollIntervalBox)
        })
        {
            if (!TryParseSeconds(box.Text, out _, out var error)) return Tr.Format("Mount.ProblemField", label, error);
        }
        return null;
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A byte ceiling as typed: empty keeps the seeded default, 0 means no ceiling, else a size.</summary>
    private static long ParseBytes(string? text) => TryParseBytes(text, out var bytes, out _) ? bytes!.Value : 0;

    /// <summary>A duration as typed: empty or 0 means "use rclone's default", else a positive number of seconds.</summary>
    private static TimeSpan? ParseSeconds(string? text) => TryParseSeconds(text, out var span, out _) ? span : null;

    private static bool TryParseBytes(string? text, out long? bytes, out string? error)
    {
        bytes = null;
        error = null;
        var value = (text ?? "").Trim();
        if (value.Length == 0) return true;
        // 0 is a real choice — no ceiling — and is expressed by leaving the key out entirely.
        if (value == "0") { bytes = 0; return true; }
        if (ByteSize.TryParse(value, out var parsed) && parsed > 0) { bytes = parsed; return true; }
        error = Tr.Format("Mount.ErrorBadSize", value);
        return false;
    }

    private static bool TryParseSeconds(string? text, out TimeSpan? span, out string? error)
    {
        span = null;
        error = null;
        var value = (text ?? "").Trim();
        if (value.Length == 0 || value == "0") return true;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0 && seconds <= (long)TimeSpan.MaxValue.TotalSeconds)
        {
            span = TimeSpan.FromSeconds(seconds);
            return true;
        }
        error = Tr.Format("Mount.ErrorBadSeconds", value);
        return false;
    }

    private sealed record CacheModeOption(MountCacheMode Mode, string Label);

    private enum MountPickerMode { DriveLetter, Directory, NetworkShare }
}
