using FengSync.Core;

namespace FengSync.Tests;

/// <summary>
/// Guards the daemon's cache root. rclone resolves its VFS cache directory once when the process starts, so a
/// per-call <c>_config.CacheDir</c> on <c>mount/mount</c> is silently ignored. Passing <c>--cache-dir</c> at
/// startup is therefore the only thing that keeps the mount cache inside app data; if this flag ever goes
/// missing the cache silently returns to the shared per-user rclone directory and app-data cleanup does nothing.
/// </summary>
public sealed class RcloneDaemonArgumentsTests
{
    [Fact]
    public void Daemon_arguments_pin_the_cache_directory_inside_app_data()
    {
        var arguments = RcloneDaemon.BuildArguments("C:\\cfg\\rclone.conf", 45678, AppDataPaths.MountCache);

        Assert.Contains("--cache-dir", arguments);
        Assert.Equal(AppDataPaths.MountCache, arguments[arguments.ToList().IndexOf("--cache-dir") + 1]);
        Assert.StartsWith(AppDataPaths.Root, AppDataPaths.MountCache, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Daemon_arguments_keep_the_loopback_control_endpoint_and_object_serving()
    {
        // --rc-serve is what exposes byte-offset reads (Range -> 206) and therefore resumable downloads.
        var arguments = RcloneDaemon.BuildArguments("C:\\cfg\\rclone.conf", 45678, AppDataPaths.MountCache);

        Assert.Equal("rcd", arguments[0]);
        Assert.Contains("127.0.0.1:45678", arguments);
        Assert.Contains("--rc-serve", arguments);
        Assert.Contains("--config", arguments);
        Assert.Equal("C:\\cfg\\rclone.conf", arguments[arguments.ToList().IndexOf("--config") + 1]);
    }

    [Fact]
    public void Every_daemon_flag_that_takes_a_value_is_followed_by_one()
    {
        var arguments = RcloneDaemon.BuildArguments("C:\\cfg\\rclone.conf", 45678, AppDataPaths.MountCache);

        // A value-taking flag placed last would swallow nothing and rclone would silently use a default.
        Assert.DoesNotContain(arguments[^1], new[] { "--rc-addr", "--config", "--cache-dir" });
    }
}
