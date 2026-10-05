using FengSync.Core.Mount;

namespace FengSync.Tests.Mount;

public sealed class MountPointInspectorTests
{
    [Fact]
    public void EnumerateDriveLetters_reports_twenty_six_options()
    {
        var letters = MountPointInspector.EnumerateDriveLetters();
        Assert.Equal(26, letters.Count);
        Assert.Contains(letters, l => l.Letter == "A:");
        Assert.Contains(letters, l => l.Letter == "Z:");
    }

    [Fact]
    public void Validate_rejects_invalid_drive_letter_format()
    {
        var result = MountPointInspector.Validate("X", MountTargetKind.DriveLetter, []);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_drive_letter_already_used_by_another_mount()
    {
        var result = MountPointInspector.Validate("X:", MountTargetKind.DriveLetter, ["X:"]);
        Assert.False(result.IsValid);
        Assert.Contains("已被", result.Error);
    }

    [Fact]
    public void Validate_rejects_directory_that_already_exists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "fengsync-mount-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = MountPointInspector.Validate(tempDir, MountTargetKind.Directory, []);
            Assert.False(result.IsValid);
            Assert.Contains("已存在", result.Error);
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }

    [Fact]
    public void Validate_rejects_directory_without_existing_parent()
    {
        var missing = Path.Combine(Path.GetTempPath(), "fengsync-no-parent-" + Guid.NewGuid().ToString("N"), "deep", "child");
        var result = MountPointInspector.Validate(missing, MountTargetKind.Directory, []);
        Assert.False(result.IsValid);
        Assert.Contains("父目录不存在", result.Error);
    }

    [Fact]
    public void Validate_accepts_a_directory_with_existing_parent()
    {
        var parent = Path.Combine(Path.GetTempPath(), "fengsync-parent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var target = Path.Combine(parent, "child");
            var result = MountPointInspector.Validate(target, MountTargetKind.Directory, []);
            Assert.True(result.IsValid, result.Error);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void Validate_rejects_relative_directory_paths()
    {
        var result = MountPointInspector.Validate("foo\\bar", MountTargetKind.Directory, []);
        Assert.False(result.IsValid);
        Assert.Contains("绝对路径", result.Error);
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\sub\directory")]
    [InlineData(@"\\192.168.1.10\media")]
    [InlineData(@"\\server\share\")]
    public void Validate_accepts_a_well_formed_network_share(string mountPoint)
    {
        // Shape only, deliberately. Measured against the bundled v1.75.0: rclone accepts a UNC mount point that
        // does not exist, mounts it, and answers with the drive letter Windows allocated for it — so "is this
        // share reachable" and "is this path free" are not questions this side can answer, and asking them would
        // only invent failures.
        var result = MountPointInspector.Validate(mountPoint, MountTargetKind.NetworkShare, []);
        Assert.True(result.IsValid, result.Error);
    }

    [Theory]
    [InlineData(@"\\server")]
    [InlineData(@"\\")]
    [InlineData(@"\\server\")]
    [InlineData(@"\\ser ver\share")]
    [InlineData(@"\\server\sha:re")]
    [InlineData(@"\\server\share\..\other")]
    [InlineData(@"\\server\.")]
    [InlineData(@"\\server\sh*are")]
    public void Validate_rejects_a_malformed_network_share(string mountPoint)
    {
        var result = MountPointInspector.Validate(mountPoint, MountTargetKind.NetworkShare, []);
        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void A_UNC_path_is_recognised_by_its_own_predicate_and_routed_to_the_share_rules()
    {
        // The two are the same decision: a UNC path is not a directory mount with a different spelling, so the
        // validator has to reach the share branch even when the caller passed the directory kind.
        Assert.True(MountPointInspector.IsNetworkShare(@"\\server\share"));
        Assert.False(MountPointInspector.IsNetworkShare(@"C:\mnt\photos"));
        Assert.False(MountPointInspector.IsNetworkShare("X:"));

        var routed = MountPointInspector.Validate(@"\\server", MountTargetKind.Directory, []);
        Assert.False(routed.IsValid);
        Assert.Contains("共享名", routed.Error);
    }

    [Fact]
    public void An_occupied_mount_point_never_blocks_a_network_share_because_Windows_picks_the_letter()
    {
        // The requested path is not the mount point that ends up existing, so it cannot be the thing that
        // collides. Two shares may even be asked for with the same path and land on different letters.
        var result = MountPointInspector.Validate(@"\\server\share", MountTargetKind.NetworkShare, [@"\\server\share", "Z:"]);

        Assert.True(result.IsValid, result.Error);
    }
}