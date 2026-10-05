using FengSync.Core.Mount;

namespace FengSync.Tests.Mount;

/// <summary>
/// The payload Feng Sync sends to <c>mount/mount</c>, asserted key by key.
/// </summary>
/// <remarks>
/// <para>
/// This file exists because of one measured failure mode: rclone <b>silently ignores</b> an option name it
/// cannot match to a field. A request naming <c>cacheModee</c>/<c>CacheMaxSizee</c>/<c>diskSpaceTotal</c>
/// mounted fine against the bundled v1.75.0 and left <c>vfs/stats.opt</c> at its defaults — no error, no
/// warning, nothing in the log. So a typo here does not break a mount; it produces a healthy mount that
/// quietly behaves like the default, which is why the assertions below are about <i>names</i> and not only
/// about values.
/// </para>
/// <para>
/// Names being spelled as intended is not the same as names being <i>right</i>, and that half is not provable
/// from here. <c>MountOptionKeyFactTests</c> proves it against the real binary: every name below is one
/// rclone reports for the matching block, and a bad value under it is refused while the same value under a
/// typo'd name is not.
/// </para>
/// </remarks>
public sealed class MountOptionsPayloadTests
{
    /// <summary>Exactly what every mount sent before per-mount options existed.</summary>
    private static readonly string[] HistoricalVfsKeys =
        ["CacheMode", "CacheMaxSize", "CacheMaxAge", "WriteBack", "NoChecksum", "NoModTime"];

    /// <summary>Every override turned on, so a missing key is a key the builder forgot.</summary>
    private static MountOptionsSettings Everything => new()
    {
        VolumeName = "nas",
        NetworkMode = true,
        CacheMode = MountCacheMode.Full,
        CacheMaxSizeBytes = 2L * 1024 * 1024 * 1024,
        CacheMinFreeSpaceBytes = 1024L * 1024 * 1024,
        CacheMaxAge = TimeSpan.FromHours(6),
        WriteBack = TimeSpan.FromMinutes(2),
        DirCacheTime = TimeSpan.FromMinutes(10),
        PollInterval = TimeSpan.FromMinutes(1),
        DiskSpaceTotalSize = 512L * 1024 * 1024 * 1024,
        ReadOnly = true,
        NoSeek = true,
        NoChecksum = false,
        NoModTime = false
    };

    [Fact]
    public void An_unspecified_mount_sends_exactly_the_payload_every_mount_sent_before()
    {
        // The compatibility half of the whole feature: no options must mean no change, or every existing mount
        // would silently acquire a different cache configuration on the next start.
        var vfs = MountOptions.CreateVfsOptions();

        Assert.Equal(HistoricalVfsKeys.OrderBy(x => x, StringComparer.Ordinal), vfs.Keys.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("writes", vfs["CacheMode"]);
        Assert.Equal(MountOptions.DefaultCacheMaxSizeBytes, vfs["CacheMaxSize"]);
        Assert.Equal("3600s", vfs["CacheMaxAge"]);
        Assert.Equal("5s", vfs["WriteBack"]);
        Assert.Equal(true, vfs["NoChecksum"]);
        Assert.Equal(true, vfs["NoModTime"]);

        // An all-null record has to build the same payload as no record at all, because that is what "this
        // member is an override" means.
        var fromEmptyRecord = MountOptions.CreateVfsOptions(new MountOptionsSettings());
        Assert.Equal(vfs.Keys.OrderBy(x => x, StringComparer.Ordinal), fromEmptyRecord.Keys.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(vfs["CacheMaxSize"], fromEmptyRecord["CacheMaxSize"]);
    }

    [Fact]
    public void An_empty_mount_block_is_empty_because_Windows_picked_the_volume_label_itself()
    {
        Assert.Empty(MountOptions.CreateMountOptions());
        Assert.Empty(MountOptions.CreateMountOptions(new MountOptionsSettings()));
    }

    [Fact]
    public void Every_override_reaches_the_block_rclone_reads_it_from()
    {
        var vfs = MountOptions.CreateVfsOptions(Everything);
        var mount = MountOptions.CreateMountOptions(Everything);

        // The measured trap: readOnly belongs to vfsOpt while volumeName/networkMode belong to mountOpt.
        // Putting readOnly in mountOpt would be silently ignored and the mount would accept writes.
        Assert.Equal(true, vfs["ReadOnly"]);
        Assert.DoesNotContain("ReadOnly", mount.Keys);
        Assert.Equal("nas", mount["VolumeName"]);
        Assert.Equal(true, mount["NetworkMode"]);
        Assert.DoesNotContain("VolumeName", vfs.Keys);
        Assert.DoesNotContain("NetworkMode", vfs.Keys);

        Assert.Equal("full", vfs["CacheMode"]);
        Assert.Equal(2L * 1024 * 1024 * 1024, vfs["CacheMaxSize"]);
        Assert.Equal(1024L * 1024 * 1024, vfs["CacheMinFreeSpace"]);
        Assert.Equal("21600s", vfs["CacheMaxAge"]);
        Assert.Equal("120s", vfs["WriteBack"]);
        Assert.Equal("600s", vfs["DirCacheTime"]);
        Assert.Equal("60s", vfs["PollInterval"]);
        Assert.Equal(512L * 1024 * 1024 * 1024, vfs["DiskSpaceTotalSize"]);
        Assert.Equal(true, vfs["NoSeek"]);
        Assert.Equal(false, vfs["NoChecksum"]);
        Assert.Equal(false, vfs["NoModTime"]);

        Assert.Equal(
            ["CacheMaxAge", "CacheMaxSize", "CacheMinFreeSpace", "CacheMode", "DirCacheTime", "DiskSpaceTotalSize",
             "NoChecksum", "NoModTime", "NoSeek", "PollInterval", "ReadOnly", "WriteBack"],
            vfs.Keys.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void A_ceiling_that_means_unlimited_is_omitted_rather_than_sent_as_zero()
    {
        // The mistake this rule exists to prevent: rclone's sentinel for "unset" is -1, so a literal 0 is read
        // as "a cache of zero bytes" rather than as "no limit". The key is therefore left out entirely.
        var unlimited = MountOptions.CreateVfsOptions(new MountOptionsSettings { CacheMaxSizeBytes = 0 });

        Assert.DoesNotContain("CacheMaxSize", unlimited.Keys);
        // Everything else is untouched, so this is a ceiling choice and not a wholesale fallback.
        Assert.Equal("writes", unlimited["CacheMode"]);
    }

    [Fact]
    public void Sizes_rclone_reads_as_unset_are_never_sent_as_zero()
    {
        var settings = new MountOptionsSettings { CacheMinFreeSpaceBytes = 0, DiskSpaceTotalSize = 0 };
        var vfs = MountOptions.CreateVfsOptions(settings);

        Assert.DoesNotContain("CacheMinFreeSpace", vfs.Keys);
        Assert.DoesNotContain("DiskSpaceTotalSize", vfs.Keys);
    }

    [Fact]
    public void A_blank_volume_name_is_not_sent()
    {
        // A blank string is not a volume name, and sending one would ask Windows to label the drive "".
        Assert.DoesNotContain("VolumeName", MountOptions.CreateMountOptions(new MountOptionsSettings { VolumeName = "   " }).Keys);
        Assert.Equal("nas", MountOptions.CreateMountOptions(new MountOptionsSettings { VolumeName = "  nas  " })["VolumeName"]);
    }

    [Fact]
    public void The_default_ceiling_is_the_historical_ten_gibibytes()
    {
        // The number every mount used before it was configurable. Changing it silently would change the disk
        // usage of every existing installation on its next start.
        Assert.Equal(10L * 1024 * 1024 * 1024, MountOptions.DefaultCacheMaxSizeBytes);
        Assert.Equal(MountOptions.DefaultCacheMaxSizeBytes, MountOptions.CreateVfsOptions()["CacheMaxSize"]);
    }

    [Fact]
    public void The_options_rclone_cannot_honour_on_Windows_are_never_sent()
    {
        // Deliberately absent from MountOptionsSettings: rclone's options/info documents all four as "not
        // supported on Windows" and this application only runs on Windows, so a switch for any of them would be
        // a switch that mounts successfully and changes nothing. MountOptionKeyFactTests pins that rclone still
        // documents them that way.
        var vfs = MountOptions.CreateVfsOptions(Everything).Keys;
        var mount = MountOptions.CreateMountOptions(Everything).Keys;

        foreach (var absent in new[] { "Umask", "AllowNonEmpty", "AllowOther", "DefaultPermissions" })
        {
            Assert.DoesNotContain(absent, vfs);
            Assert.DoesNotContain(absent, mount);
        }
    }

    [Theory]
    [InlineData(MountCacheMode.Off, "off")]
    [InlineData(MountCacheMode.Minimal, "minimal")]
    [InlineData(MountCacheMode.Writes, "writes")]
    [InlineData(MountCacheMode.Full, "full")]
    public void Cache_mode_names_are_the_four_rclone_accepts(MountCacheMode mode, string expected)
    {
        // rclone answers a fifth spelling with `invalid choice "x" from: off, minimal, writes, full`, so this
        // list is not a style choice — it is the entire accepted vocabulary.
        Assert.Equal(expected, MountOptions.WireCacheMode(mode));
        Assert.Equal(expected, MountOptions.CreateVfsOptions(new MountOptionsSettings { CacheMode = mode })["CacheMode"]);
    }

    [Fact]
    public void Every_cache_mode_says_what_it_costs()
    {
        // The modes are only a real choice if the picker says which one spends disk and which one spends time.
        foreach (var mode in Enum.GetValues<MountCacheMode>())
        {
            var description = MountOptions.DescribeCacheMode(mode);
            Assert.False(string.IsNullOrWhiteSpace(description));
        }
        Assert.Contains("走网络", MountOptions.DescribeCacheMode(MountCacheMode.Off));
        Assert.Contains("占满", MountOptions.DescribeCacheMode(MountCacheMode.Full));
    }

    [Fact]
    public void Durations_are_sent_as_whole_seconds()
    {
        // Seconds rather than Go's "1h0m0s" form: exact for every value, no per-unit branching, and measured to
        // land unchanged in vfs/stats.opt ("3600s" -> 3600000000000 ns).
        Assert.Equal("3600s", MountOptions.Seconds(TimeSpan.FromHours(1)));
        Assert.Equal("5s", MountOptions.Seconds(TimeSpan.FromSeconds(5)));
        Assert.Equal("0s", MountOptions.Seconds(TimeSpan.Zero));
        // Sub-second precision is dropped rather than rounded up: rclone's own default for writeBack is 5s, and
        // a mount must never end up polling more often than the user asked for.
        Assert.Equal("1s", MountOptions.Seconds(TimeSpan.FromMilliseconds(1999)));
    }

    [Fact]
    public void A_poll_interval_that_outlives_the_directory_cache_is_warned_about_not_refused()
    {
        // Measured: rclone accepts PollInterval=600s beside DirCacheTime=60s and lands both values unchanged.
        // Only options/info states the rule, and it states it as a "must" — so the user is told, and the mount
        // is still theirs to make. A refusal here would be a rule this application invented.
        var warned = MountOptions.Warnings(new MountOptionsSettings
        {
            PollInterval = TimeSpan.FromMinutes(10),
            DirCacheTime = TimeSpan.FromMinutes(1)
        });

        Assert.Single(warned);
        Assert.Contains("轮询间隔", warned[0]);
        Assert.Contains("600 秒", warned[0]);
        Assert.Contains("60 秒", warned[0]);
    }

    [Fact]
    public void The_default_combination_and_a_shorter_poll_interval_produce_no_warning()
    {
        // The defaults are 1m polling against a 5m directory cache, which satisfies the rule.
        Assert.Empty(MountOptions.Warnings(new MountOptionsSettings()));
        Assert.Empty(MountOptions.Warnings(null));
        Assert.Empty(MountOptions.Warnings(new MountOptionsSettings
        {
            PollInterval = TimeSpan.FromSeconds(30),
            DirCacheTime = TimeSpan.FromMinutes(5)
        }));
    }

    [Fact]
    public void The_warning_compares_against_the_default_when_only_one_side_was_given()
    {
        // Only a poll interval was configured, and 10 minutes outlives the default 5-minute directory cache.
        var warned = MountOptions.Warnings(new MountOptionsSettings { PollInterval = TimeSpan.FromMinutes(10) });

        Assert.Single(warned);
        Assert.Contains("300 秒", warned[0]);
    }
}
