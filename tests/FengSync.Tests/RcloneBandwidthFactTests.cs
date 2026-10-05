using FengSync.Core;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Tests;

/// <summary>
/// What the bundled rclone v1.75.0 really does with <c>core/bwlimit</c>, measured against the binary itself.
/// </summary>
/// <remarks>
/// <para>
/// Every rule <see cref="Core.Configuration.CloudTransferLimit"/> encodes was taken from this endpoint, and
/// every one of them is a place a plausible-looking value is read as something else:
/// </para>
/// <list type="bullet">
/// <item>it is <b>global</b> — there is no per-endpoint limit, and an extra <c>fs</c> field is accepted and
/// ignored while the global rate is set, which is the same shape as the ignored <c>remote</c> field on
/// <c>operations/size</c>;</item>
/// <item>a bare number is a binary multiple, not bytes;</item>
/// <item>the pair is upload first, download second;</item>
/// <item>an unlimited direction must be spelled <c>off</c>, because an empty half is refused.</item>
/// </list>
/// <para>
/// No mount is involved, so this needs no WinFsp driver and no mount point — only a daemon.
/// </para>
/// </remarks>
public sealed class RcloneBandwidthFactTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-bwlimit-" + Guid.NewGuid().ToString("N"));

    private RcloneDaemon? _daemon;

    private RcloneRcClient Client => _daemon!.Client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "rclone.conf");
        await File.WriteAllTextAsync(config, "[endpoint]\ntype = alias\nremote = " + _root.Replace('\\', '/') + "\n");
        _daemon = await RcloneDaemon.StartAsync(RclonePath(), config);
    }

    public async Task DisposeAsync()
    {
        if (_daemon is not null) await _daemon.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task A_rate_is_a_binary_multiple_and_only_an_explicit_B_means_bytes()
    {
        // The trap the formatter exists for: "512" is 512 KiB, not 512 bytes, and "1048576" is a whole GiB.
        // An application that sent a bare byte count would be off by 1024x without any error to show for it.
        Assert.Equal(2L * 1024 * 1024, (await Client.SetBandwidthLimitAsync("2M")).BytesPerSecond);
        Assert.Equal(512L * 1024, (await Client.SetBandwidthLimitAsync("512")).BytesPerSecond);
        Assert.Equal(1024L * 1024 * 1024, (await Client.SetBandwidthLimitAsync("1048576")).BytesPerSecond);
        Assert.Equal(1048576L, (await Client.SetBandwidthLimitAsync("1048576B")).BytesPerSecond);
        Assert.Equal(512L, (await Client.SetBandwidthLimitAsync("512B")).BytesPerSecond);
        // A fractional multiple is accepted, which is why the formatter does not need to round to a unit.
        Assert.Equal((long)(1.5 * 1024 * 1024), (await Client.SetBandwidthLimitAsync("1.5M")).BytesPerSecond);
    }

    [Fact]
    public async Task The_two_values_are_upload_then_download()
    {
        // Writing the user's two numbers the other way round would silently swap them, so the order is pinned.
        var state = await Client.SetBandwidthLimitAsync("1M:100k");

        Assert.Equal(1024L * 1024, state.BytesPerSecondTx);
        Assert.Equal(102400L, state.BytesPerSecondRx);
        Assert.False(state.IsUnlimited);
    }

    [Fact]
    public async Task Unlimited_is_off_and_reads_back_as_minus_one_on_both_sides()
    {
        // -1 rather than 0: rclone's own "unset" sentinel, and the reason a reply with a missing field is read
        // as -1 by RcloneRcClient.ParseBandwidth. 0 read as a rate would look like "no bytes per second".
        var off = await Client.SetBandwidthLimitAsync("off");

        Assert.Equal(-1, off.BytesPerSecondTx);
        Assert.Equal(-1, off.BytesPerSecondRx);
        Assert.True(off.IsUnlimited);

        // The read-back call answers the same shape, which is what lets a caller tell "already set" from "not
        // yet" instead of pushing the same rate before every transfer.
        var readBack = await Client.GetBandwidthLimitAsync();
        Assert.Equal(off.BytesPerSecondTx, readBack.BytesPerSecondTx);
        Assert.Equal(off.BytesPerSecondRx, readBack.BytesPerSecondRx);
    }

    [Theory]
    [InlineData("nas:1M", "bad bwlimit")]
    [InlineData(":100k", "bad bwlimit")]
    [InlineData("100k:", "bad bwlimit")]
    [InlineData("-1", "bad bwlimit")]
    [InlineData("abc", "bad bwlimit")]
    // The --bwlimit time-table form fails differently, which is the point: rclone parses it as a schedule and
    // then refuses the shape rather than the value.
    [InlineData("08:00,1M 18:00,off", "need exactly 1 bandwidth setting")]
    public async Task The_forms_rclone_refuses_are_refused(string rate, string expected)
    {
        // The first four are how a per-endpoint limit would have to be written, and the last is the time-table
        // form: all of them are errors, which is why "only transfer in this window" had to become the queue
        // holding work back rather than a rate schedule.
        var error = await Assert.ThrowsAsync<RcloneException>(() => Client.SetBandwidthLimitAsync(rate));

        Assert.Contains(expected, error.Failure.TechnicalMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_extra_fs_field_is_ignored_and_the_global_rate_is_set_anyway()
    {
        // The measurement that decided C1's design: there is no per-endpoint limiting to build on. A request
        // that looks like it limits one remote sets the limit for the whole daemon and reports success, so a UI
        // offering "limit this endpoint" would be offering something rclone cannot do.
        await Client.SetBandwidthLimitAsync("off");
        var answer = await Client.CallAsync("core/bwlimit", new { rate = "3M", fs = "endpoint:" });

        Assert.Equal(3L * 1024 * 1024, answer.GetProperty("bytesPerSecond").GetInt64());
        // And it really is global: reading back without naming any remote shows the same rate.
        Assert.Equal(3L * 1024 * 1024, (await Client.GetBandwidthLimitAsync()).BytesPerSecond);
    }

    [Fact]
    public async Task A_limit_survives_until_it_is_replaced_which_is_why_the_queue_releases_it()
    {
        // Nothing resets this on its own. If the queue pushed a rate and never pushed "off", the limit would
        // outlive the transfers that needed it and silently throttle every later browse of the same daemon.
        await Client.SetBandwidthLimitAsync("1M");
        Assert.Equal(1024L * 1024, (await Client.GetBandwidthLimitAsync()).BytesPerSecond);

        await Client.SetBandwidthLimitAsync("off");
        Assert.True((await Client.GetBandwidthLimitAsync()).IsUnlimited);
    }

    private static string RclonePath()
    {
        try { return BundledRclone.ExecutablePath; }
        catch (FileNotFoundException) { }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "FengSync", "Assets", "rclone", "rclone.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("找不到内置 rclone.exe，无法运行限速实证测试。");
    }
}
