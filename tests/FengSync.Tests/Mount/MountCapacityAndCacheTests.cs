using System.Text.Json;
using FengSync.Core.Mount;

namespace FengSync.Tests.Mount;

/// <summary>
/// The three mount-side facts that are read from outside the mount itself: how big the endpoint says it is,
/// how much disk the shared cache is holding, and which running mounts have stopped working.
/// </summary>
public sealed class MountCapacityAndCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-mount-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void The_endpoint_size_is_read_only_from_a_total_that_is_really_there()
    {
        Assert.Equal(1234L, MountCapacity.TotalBytes(Json("""{"free":1,"total":1234,"used":1}""")));
        // Everything else is "no answer", because rclone's sentinel for an unspecified size is -1: writing 0
        // would report a zero-byte volume and Windows would draw a full disk.
        Assert.Null(MountCapacity.TotalBytes(Json("""{"free":1,"used":1}""")));
        Assert.Null(MountCapacity.TotalBytes(Json("""{"total":0}""")));
        Assert.Null(MountCapacity.TotalBytes(Json("""{"total":-1}""")));
        Assert.Null(MountCapacity.TotalBytes(Json("""{"total":"1024"}""")));
        Assert.Null(MountCapacity.TotalBytes(Json("{}")));
        Assert.Null(MountCapacity.TotalBytes(Json("[]")));
        Assert.Null(MountCapacity.TotalBytes(default));
    }

    [Fact]
    public void The_two_outcomes_of_asking_for_the_size_say_which_one_happened()
    {
        var known = MountCapacity.Describe(5L * 1024 * 1024 * 1024);
        Assert.Contains("5G", known);
        // The limitation is part of the sentence, not a footnote: the number is captured once and a later quota
        // change will not move it.
        Assert.Contains("挂载时确定", known);

        var unknown = MountCapacity.Describe(null);
        Assert.Contains("没有报告容量", unknown);
        Assert.Contains("不会写入 0", unknown);

        // What the picker promises before anything is mounted has to say the same thing.
        Assert.Contains("挂载时", MountCapacity.DescribeUpcoming());
        Assert.Contains("不会自动更新", MountCapacity.DescribeUpcoming());
    }

    [Fact]
    public void An_absent_cache_directory_measures_as_empty_rather_than_failing()
    {
        var usage = MountCacheInspector.Measure(Path.Combine(_root, "never-created"));

        Assert.True(usage.IsEmpty);
        Assert.Equal(0, usage.Bytes);
        Assert.Equal(0, usage.Files);
    }

    [Fact]
    public void The_cache_is_measured_across_its_whole_tree()
    {
        var cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(Path.Combine(cache, "vfs", "meta"));
        File.WriteAllText(Path.Combine(cache, "vfs", "a.bin"), new string('x', 100));
        File.WriteAllText(Path.Combine(cache, "vfs", "meta", "b.bin"), new string('y', 50));

        var usage = MountCacheInspector.Measure(cache);

        Assert.Equal(150, usage.Bytes);
        Assert.Equal(2, usage.Files);
        Assert.False(usage.IsEmpty);
    }

    [Fact]
    public void Clearing_the_cache_refuses_any_root_that_is_not_inside_the_application_data_directory()
    {
        // The guard is the whole safety story: the root arrives from configuration, and a mis-set value must not
        // turn "clear the cache" into a recursive delete somewhere else on the disk.
        var dataRoot = Path.Combine(_root, "appdata");
        var inside = Path.Combine(dataRoot, "mountcache");
        var outside = Path.Combine(_root, "not-ours");
        Directory.CreateDirectory(inside);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(inside, "cached.bin"), "cached");
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "not ours to delete");

        Assert.Equal(0, MountCacheInspector.Clear(outside, dataRoot));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));

        Assert.Equal(1, MountCacheInspector.Clear(inside, dataRoot));
        Assert.False(File.Exists(Path.Combine(inside, "cached.bin")));
    }

    [Fact]
    public void A_sibling_directory_whose_name_merely_starts_with_the_root_is_still_outside_it()
    {
        // "C:\data-old" must not pass a naive StartsWith("C:\data") test.
        Assert.True(MountCacheInspector.IsInside(@"C:\data", @"C:\data"));
        Assert.True(MountCacheInspector.IsInside(@"C:\data", @"C:\data\mountcache"));
        Assert.True(MountCacheInspector.IsInside(@"C:\data\", @"C:\data\mountcache"));
        Assert.False(MountCacheInspector.IsInside(@"C:\data", @"C:\data-old"));
        Assert.False(MountCacheInspector.IsInside(@"C:\data", @"C:\other"));
        Assert.False(MountCacheInspector.IsInside(@"C:\data", @"C:\"));
    }

    [Fact]
    public void Clearing_a_cache_that_is_not_there_removes_nothing_and_does_not_throw()
    {
        var dataRoot = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(dataRoot);

        Assert.Equal(0, MountCacheInspector.Clear(Path.Combine(dataRoot, "absent"), dataRoot));
    }

    [Fact]
    public void The_cache_line_states_the_ceiling_because_the_ceiling_is_a_mount_time_setting()
    {
        var usage = new MountCacheUsage(@"C:\appdata\mountcache", 3L * 1024 * 1024 * 1024, 12);

        var withLimit = MountCacheInspector.Describe(usage, 10L * 1024 * 1024 * 1024);
        Assert.Contains("3G", withLimit);
        Assert.Contains("10G", withLimit);
        Assert.Contains("12 个文件", withLimit);

        Assert.Contains("不限", MountCacheInspector.Describe(usage, 0));
        Assert.Contains("不限", MountCacheInspector.Describe(usage, null));

        var empty = MountCacheInspector.Describe(new MountCacheUsage(@"C:\appdata\mountcache", 0, 0), 10L * 1024 * 1024 * 1024);
        Assert.Contains("当前为空", empty);
        Assert.Contains("10G", empty);
    }

    [Fact]
    public void Only_our_own_mounts_that_stopped_working_are_offered_a_reconnect()
    {
        // An external mount is not ours to touch, and a healthy one needs nothing — offering to re-create it
        // would take a working mount away for no reason.
        MountInfo[] mounts =
        [
            new(null, "nas", "SFTP", "X:", MountTargetKind.DriveLetter, DateTimeOffset.UtcNow, MountOrigin.FengSyncManaged, true),
            new(null, "nas", "SFTP", "Y:", MountTargetKind.DriveLetter, DateTimeOffset.UtcNow, MountOrigin.FengSyncManaged, false),
            new(42, "other", "rclone", "Z:", MountTargetKind.DriveLetter, DateTimeOffset.UtcNow, MountOrigin.External, false),
            new(-1, "(unreadable)", "?", "", MountTargetKind.Directory, null, MountOrigin.Unreadable, false)
        ];

        var broken = Assert.Single(MountReconnect.NeedsReconnect(mounts));
        Assert.Equal("Y:", broken.MountPoint);
    }

    [Fact]
    public void The_reconnect_lines_name_the_mount_and_say_it_is_manual()
    {
        Assert.Equal("", MountReconnect.Describe(0));
        Assert.Contains("1 个", MountReconnect.Describe(1));
        Assert.Contains("2 个", MountReconnect.Describe(2));
        // Nothing here promises an automatic recovery: the sentence has to describe what the button will do.
        Assert.Contains("重新挂载", MountReconnect.Describe(2));

        var drive = new MountInfo(null, "nas", "SFTP", "X:", MountTargetKind.DriveLetter, null, MountOrigin.FengSyncManaged, false);
        var directory = new MountInfo(null, "nas", "SFTP", @"C:\mnt\photos", MountTargetKind.Directory, null, MountOrigin.FengSyncManaged, false);
        Assert.Contains("不可用", MountReconnect.Explain(drive));
        Assert.Contains("X:", MountReconnect.Explain(drive));
        Assert.Contains("不存在", MountReconnect.Explain(directory));
        Assert.Contains("nas", MountReconnect.Explain(directory));
    }

    [Fact]
    public void The_status_line_after_a_mount_reports_the_point_rclone_answered_with()
    {
        // The ordinary case: what was asked for is what now exists.
        var plain = MountStatusLine.Describe(
            new MountTarget("nas", "SFTP", "X:", MountTargetKind.DriveLetter),
            new MountResult("X:", null));
        Assert.Contains("nas", plain);
        Assert.Contains("X:", plain);
        Assert.DoesNotContain("请求的是", plain);

        // The D6 case, and the reason this rule is not inline in the window: rclone accepts a UNC target and
        // answers with the drive letter Windows allocated — measured `\\localhost\nonexistent-share-xyz\mnt` →
        // `{"mountPoint": "Z:"}`. Saying "已挂载到 \\localhost\..." here would name a mount point that does not
        // exist, so the requested path may only appear as the thing that was *not* used.
        var share = MountStatusLine.Describe(
            new MountTarget("nas", "SFTP", @"\\localhost\nonexistent-share-xyz\mnt", MountTargetKind.NetworkShare),
            new MountResult("Z:", null));
        Assert.Contains("Z:", share);
        Assert.Contains("请求的是", share);
        Assert.Contains("Windows 改用了", share);
        Assert.Contains(@"\\localhost\nonexistent-share-xyz\mnt", share);

        // Case is not a difference: a drive letter answered in lower case is the same mount point.
        var lower = MountStatusLine.Describe(
            new MountTarget("nas", "SFTP", "X:", MountTargetKind.DriveLetter),
            new MountResult("x:", null));
        Assert.DoesNotContain("请求的是", lower);
    }

    [Fact]
    public void The_status_line_mentions_the_capacity_only_when_there_was_one()
    {
        var target = new MountTarget("nas", "SFTP", "X:", MountTargetKind.DriveLetter);

        var sized = MountStatusLine.Describe(target, new MountResult("X:", 5L * 1024 * 1024 * 1024));
        Assert.Contains("5G", sized);

        // An endpoint that reported no size must not leave a dangling "容量" clause behind.
        Assert.DoesNotContain("容量", MountStatusLine.Describe(target, new MountResult("X:", null)));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
