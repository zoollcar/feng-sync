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
}