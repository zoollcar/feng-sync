using System.IO;
using System.Windows;
using System.Windows.Controls;
using FengSync.Core.Mount;

namespace FengSync.Views;

/// <summary>
/// Modal "Mount to…" dialog. The user picks an unused drive letter or supplies an absolute directory
/// path that does not yet exist (Feng Sync never creates the directory). Validation is live so the OK
/// button only enables when the target is genuinely free.
/// </summary>
public partial class MountPickerDialog : Window
{
    private readonly string _remoteName;
    private readonly string _provider;
    private readonly string _rootPath;
    private readonly IReadOnlyList<string> _occupiedMountPoints;
    private MountPickerMode _mode = MountPickerMode.DriveLetter;

    public MountTarget? SelectedTarget { get; private set; }

    /// <param name="rootPath">Path handed to rclone; empty for an alias-backed endpoint, whose root already is the prefix.</param>
    /// <param name="displayRoot">
    /// What to show the user instead, when the mount root and the visible prefix differ. Without it an
    /// enforced-prefix endpoint would read as <c>name:/</c> and hide which prefix it is confined to.
    /// </param>
    public MountPickerDialog(string remoteName, string provider, IReadOnlyList<string> occupiedMountPoints, string rootPath = "", string? displayRoot = null)
    {
        InitializeComponent();
        _remoteName = remoteName;
        _provider = provider;
        _rootPath = rootPath.Trim('/');
        _occupiedMountPoints = occupiedMountPoints;
        HeaderText.Text = $"挂载 {_remoteName}";
        var shown = string.IsNullOrWhiteSpace(displayRoot) ? _rootPath : displayRoot.Trim().Trim('/');
        SubHeaderText.Text = $"选择本地盘符或目录，将 {_remoteName}:/{shown} 挂载到 Feng Sync 可访问的位置。";
        DriveLetterList.ItemsSource = MountPointInspector.EnumerateDriveLetters();
        UpdateStatus();
    }

    private void ModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _mode = ModeTabs.SelectedIndex == 1 ? MountPickerMode.Directory : MountPickerMode.DriveLetter;
        UpdateStatus();
    }

    private void DriveLetterList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStatus();

    private void DirectoryPathBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateStatus();

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

    private void UpdateStatus()
    {
        if (_mode == MountPickerMode.DriveLetter)
        {
            DriveHint.Visibility = Visibility.Visible;
            if (DriveLetterList.SelectedItem is not MountPointInspector.DriveLetterOption option)
            {
                StatusText.Text = "请选择一个盘符。";
                OkButton.IsEnabled = false;
                return;
            }
            // Deliberately the same rule the directory branch uses, rather than reading IsAvailable alone:
            // a letter already claimed by an existing mount has to be refused while the user is choosing,
            // not after they press OK.
            var validation = MountPointInspector.Validate(option.Letter, MountTargetKind.DriveLetter, _occupiedMountPoints);
            StatusText.Text = validation.IsValid ? $"将挂载到盘符 {option.Letter}。" : validation.Error!;
            OkButton.IsEnabled = validation.IsValid;
        }
        else
        {
            DirectoryHint.Visibility = Visibility.Visible;
            var path = DirectoryPathBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(path)) { StatusText.Text = "请输入目录绝对路径。"; OkButton.IsEnabled = false; return; }
            var validation = MountPointInspector.Validate(path, MountTargetKind.Directory, _occupiedMountPoints);
            StatusText.Text = validation.IsValid ? "目录合法，挂载后由 rclone 创建。" : validation.Error;
            OkButton.IsEnabled = validation.IsValid;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == MountPickerMode.DriveLetter)
        {
            if (DriveLetterList.SelectedItem is not MountPointInspector.DriveLetterOption option) return;
            var validation = MountPointInspector.Validate(option.Letter, MountTargetKind.DriveLetter, _occupiedMountPoints);
            if (!validation.IsValid) { StatusText.Text = validation.Error; return; }
            SelectedTarget = new MountTarget(_remoteName, _provider, option.Letter, MountTargetKind.DriveLetter, _rootPath);
        }
        else
        {
            var path = DirectoryPathBox.Text?.Trim() ?? "";
            var validation = MountPointInspector.Validate(path, MountTargetKind.Directory, _occupiedMountPoints);
            if (!validation.IsValid) { StatusText.Text = validation.Error; return; }
            SelectedTarget = new MountTarget(_remoteName, _provider, Path.GetFullPath(path), MountTargetKind.Directory, _rootPath);
        }
        DialogResult = SelectedTarget is not null;
    }

    private enum MountPickerMode { DriveLetter, Directory }
}
