using FengSync.Core.Mount;

namespace FengSync.Tests.Mount;

public sealed class RcloneCommandLineParserTests
{
    [Fact]
    public void TryParse_recognizes_a_typical_mount_command()
    {
        const string cmd = "rclone.exe mount remote:/ X: --config C:\\rclone.conf --vfs-cache-mode writes";
        Assert.True(RcloneCommandLineParser.TryParse(cmd, out var parsed));
        Assert.Equal("remote:/", parsed.RemoteSpec);
        Assert.Equal("X:", parsed.MountPoint);
    }

    [Fact]
    public void TryParse_recognizes_cmount_variant()
    {
        const string cmd = "rclone.exe cmount bucket:/ C:\\mounts\\bucket --cache-dir C:\\cache";
        Assert.True(RcloneCommandLineParser.TryParse(cmd, out var parsed));
        Assert.Equal("bucket:/", parsed.RemoteSpec);
        Assert.Equal("C:\\mounts\\bucket", parsed.MountPoint);
    }

    [Fact]
    public void TryParse_handles_quoted_mount_points_with_spaces()
    {
        const string cmd = "rclone.exe mount remote:/ \"C:\\Program Files\\Mount\" --config C:\\rclone.conf";
        Assert.True(RcloneCommandLineParser.TryParse(cmd, out var parsed));
        Assert.Equal("C:\\Program Files\\Mount", parsed.MountPoint);
    }

    [Fact]
    public void TryParse_preserves_remote_name_with_colons()
    {
        const string cmd = "rclone.exe mount \"SFTP 连接:资料\" Y: --config C:\\rclone.conf";
        Assert.True(RcloneCommandLineParser.TryParse(cmd, out var parsed));
        Assert.Equal("SFTP 连接:资料", parsed.RemoteSpec);
    }

    [Fact]
    public void TryParse_treats_flags_that_take_a_value_correctly()
    {
        const string cmd = "rclone.exe mount remote:/ Z: --vfs-cache-mode writes --cache-dir C:\\cache --log-level DEBUG";
        Assert.True(RcloneCommandLineParser.TryParse(cmd, out var parsed));
        Assert.Equal("Z:", parsed.MountPoint);
    }

    [Fact]
    public void TryParse_rejects_non_mount_commands()
    {
        Assert.False(RcloneCommandLineParser.TryParse("rclone.exe sync source:/ dest:/", out _));
        Assert.False(RcloneCommandLineParser.TryParse("rclone.exe version", out _));
    }

    [Fact]
    public void TryParse_rejects_empty_or_short_command_lines()
    {
        Assert.False(RcloneCommandLineParser.TryParse(null, out _));
        Assert.False(RcloneCommandLineParser.TryParse("", out _));
        Assert.False(RcloneCommandLineParser.TryParse("rclone.exe mount", out _));
        Assert.False(RcloneCommandLineParser.TryParse("rclone.exe mount remote", out _));
    }

    [Fact]
    public void Tokenize_splits_respecting_double_quotes()
    {
        var tokens = RcloneCommandLineParser.Tokenize("a \"b c\" d");
        Assert.Equal(["a", "b c", "d"], tokens);
    }

    [Fact]
    public void Tokenize_keeps_embedded_double_quote_when_doubled()
    {
        var tokens = RcloneCommandLineParser.Tokenize("a \"b\"\"c\" d");
        Assert.Equal(["a", "b\"c", "d"], tokens);
    }
}