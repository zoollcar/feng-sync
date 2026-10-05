using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FengSync.Core.Configuration;
using FengSync.Core.Mount;

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
        HeaderText.Text = $"挂载 {_remoteName}";
        var shown = string.IsNullOrWhiteSpace(displayRoot) ? _rootPath : displayRoot.Trim().Trim('/');
        SubHeaderText.Text = $"选择本地盘符、目录或网络共享，将 {_remoteName}:/{shown} 挂载到 Feng Sync 可访问的位置。";
        DriveLetterList.ItemsSource = MountPointInspector.EnumerateDriveLetters();
        DriveHint.Text = "选一个既未被 Windows 使用、也不被现有挂载占用的盘符。";
        DirectoryHint.Text = "目录必须尚不存在（rclone 会创建它），父目录必须存在。Feng Sync 不会替你创建目录。";
        UncHint.Text = @"形如 \\服务器\共享名。Windows 会为它分配一个盘符，挂载完成后实际挂载点是那个盘符，而不是你输入的路径。";
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
            Title = "选择挂载点的父目录",
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
                    StatusText.Text = "请选择一个盘符。";
                    OkButton.IsEnabled = false;
                    return;
                }
                // Deliberately the same rule the other branches use, rather than reading IsAvailable alone:
                // a letter already claimed by an existing mount has to be refused while the user is choosing,
                // not after they press OK.
                var validation = MountPointInspector.Validate(option.Letter, MountTargetKind.DriveLetter, _occupiedMountPoints);
                StatusText.Text = validation.IsValid ? $"将挂载到盘符 {option.Letter}。" : validation.Error!;
                OkButton.IsEnabled = validation.IsValid;
                return;
            }
            case MountPickerMode.Directory:
            {
                var path = DirectoryPathBox.Text?.Trim() ?? "";
                if (path.Length == 0) { StatusText.Text = "请输入目录绝对路径。"; OkButton.IsEnabled = false; return; }
                var validation = MountPointInspector.Validate(path, MountTargetKind.Directory, _occupiedMountPoints);
                StatusText.Text = validation.IsValid ? "目录合法，挂载后由 rclone 创建。" : validation.Error!;
                OkButton.IsEnabled = validation.IsValid;
                return;
            }
            default:
            {
                var path = UncPathBox.Text?.Trim() ?? "";
                if (path.Length == 0)
                {
                    StatusText.Text = @"请输入网络共享路径，例如 \\server\share。";
                    OkButton.IsEnabled = false;
                    return;
                }
                var validation = MountPointInspector.Validate(path, MountTargetKind.NetworkShare, _occupiedMountPoints);
                StatusText.Text = validation.IsValid
                    ? "路径合法。挂载完成后实际挂载点是 Windows 分配的盘符，会在状态里报告。"
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
            new(MountCacheMode.Off, "不缓存，每次读取都走网络"),
            new(MountCacheMode.Minimal, "只缓存读到的内容"),
            new(MountCacheMode.Writes, "缓存读取并缓冲写入（默认）"),
            new(MountCacheMode.Full, "全部缓存，含正在写入的整个文件")
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
        if (options.VolumeName is { } volume) parts.Add($"卷标 {volume}");
        if (options.NetworkMode) parts.Add("作为网络驱动器");
        parts.Add($"缓存模式 {MountOptions.WireCacheMode(options.CacheMode ?? MountOptions.DefaultCacheMode)}");
        parts.Add(options.CacheMaxSizeBytes is > 0
            ? $"缓存上限 {ByteSize.Format(options.CacheMaxSizeBytes.Value)}"
            : "缓存上限 不限");
        if (options.CacheMaxAge is { } age) parts.Add($"缓存保留 {age.TotalSeconds:0} 秒");
        if (options.WriteBack is { } writeBack) parts.Add($"写回延迟 {writeBack.TotalSeconds:0} 秒");
        if (options.DirCacheTime is { } dirCache) parts.Add($"目录缓存 {dirCache.TotalSeconds:0} 秒");
        if (options.PollInterval is { } poll) parts.Add($"轮询间隔 {poll.TotalSeconds:0} 秒");
        if (options.ReadOnly) parts.Add("只读");
        if (!options.NoChecksum) parts.Add("比较校验值");
        if (!options.NoModTime) parts.Add("读写修改时间");
        if (options.NoSeek) parts.Add("禁止定位");

        var text = "将发送：" + string.Join("；", parts) + "。";
        // Warnings, not refusals: rclone accepts these combinations and enforces nothing.
        var warnings = MountOptions.Warnings(options);
        return warnings.Count == 0 ? text : text + " 注意：" + string.Join(" ", warnings);
    }

    /// <summary>
    /// Why the advanced section cannot be used as typed, or <see langword="null"/> when it can. rclone would
    /// answer a malformed value with an error, but only after the user pressed OK and the dialog had closed —
    /// and it would answer a malformed <i>name</i> with a successful mount, which is worse.
    /// </summary>
    private string? DescribeAdvancedProblem()
    {
        if (!TryParseBytes(CacheMaxSizeBox.Text, out _, out var bytesError)) return "缓存上限：" + bytesError;
        foreach (var (label, box) in new (string Label, TextBox Box)[]
        {
            ("缓存保留时间", CacheMaxAgeBox),
            ("写回延迟", WriteBackBox),
            ("目录缓存时间", DirCacheTimeBox),
            ("轮询间隔", PollIntervalBox)
        })
        {
            if (!TryParseSeconds(box.Text, out _, out var error)) return $"{label}：{error}";
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
        error = $"无法识别“{value}”。请填写形如 10G、512M 的大小；留空表示沿用设置里的默认上限，0 表示不限。";
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
        error = $"无法识别“{value}”。请填写正整数秒；留空或 0 表示使用默认值。";
        return false;
    }

    private sealed record CacheModeOption(MountCacheMode Mode, string Label);

    private enum MountPickerMode { DriveLetter, Directory, NetworkShare }
}
