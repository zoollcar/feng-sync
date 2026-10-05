using FengSync.Core.Configuration;

namespace FengSync.Tests;

/// <summary>
/// The two settings that shape the transfer queue: the rate ceiling, and the hours transfers may run in.
/// </summary>
/// <remarks>
/// Everything asserted here is a measured property of <c>core/bwlimit</c> rather than a preference — see
/// <see cref="CloudTransferLimit"/> for the raw observations and <c>RcloneBandwidthFactTests</c> for the same
/// claims re-measured against the bundled binary.
/// </remarks>
public sealed class CloudTransferLimitTests
{
    [Fact]
    public void No_limit_is_spelled_off_and_is_not_a_rate_of_zero()
    {
        // rclone refuses "0" as a rate and reads a bare number as a binary multiple, so "off" is the only way
        // to say "no ceiling" — and it is what the queue pushes when it drains, which is the state every
        // installation was in before this feature existed.
        Assert.Equal("off", CloudTransferLimit.Rate(0, 0));
        Assert.Equal(CloudTransferLimit.Off, CloudTransferLimit.Rate(CloudTransferLimit.Unlimited, CloudTransferLimit.Unlimited));
        Assert.False(CloudTransferLimit.IsLimited(0, 0));
        Assert.Equal("不限速", CloudTransferLimit.Describe(0, 0));
    }

    [Fact]
    public void The_rate_is_always_written_in_bytes_because_a_bare_number_means_something_else()
    {
        // Measured: a bare 512 is read as 512 KiB and a bare 1048576 as 1 GiB. Only an explicit B suffix means
        // bytes, so the formatter never omits it.
        Assert.Equal("1048576B", CloudTransferLimit.Rate(1048576, 1048576));
        Assert.Equal("512B", CloudTransferLimit.Rate(512, 512));
        Assert.DoesNotContain(":", CloudTransferLimit.Rate(512, 512));
    }

    [Fact]
    public void The_two_directions_are_upload_then_download()
    {
        // Measured: "1M:100k" answers bytesPerSecondTx = 1048576 and bytesPerSecondRx = 102400, so the first
        // value is the upload. Writing them the other way round would silently swap the user's two numbers.
        Assert.Equal("1048576B:102400B", CloudTransferLimit.Rate(1048576, 102400));
        Assert.Equal("上传 1M/s · 下载 100K/s", CloudTransferLimit.Describe(1048576, 102400));
    }

    [Fact]
    public void An_unlimited_direction_is_spelled_off_rather_than_left_empty()
    {
        // Measured: ":100k" and "100k:" are both refused with "bad bwlimit: empty string", so an asymmetric
        // limit has to name both sides.
        Assert.Equal("off:102400B", CloudTransferLimit.Rate(0, 102400));
        Assert.Equal("1048576B:off", CloudTransferLimit.Rate(1048576, 0));
        Assert.Equal("上传 不限 · 下载 100K/s", CloudTransferLimit.Describe(0, 102400));
        Assert.True(CloudTransferLimit.IsLimited(0, 102400));
        Assert.True(CloudTransferLimit.IsLimited(1048576, 0));
    }

    [Fact]
    public void A_window_that_ends_before_it_starts_runs_across_midnight()
    {
        // 22:00–06:00 is one continuous window, not an empty one: the second reading is the only one anybody
        // means by those two numbers.
        var overnight = new CloudTransferTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));

        Assert.True(overnight.Contains(new TimeOnly(23, 0)));
        Assert.True(overnight.Contains(new TimeOnly(2, 0)));
        Assert.True(overnight.Contains(new TimeOnly(22, 0)));
        Assert.False(overnight.Contains(new TimeOnly(21, 59)));
        Assert.False(overnight.Contains(new TimeOnly(6, 0)));
    }

    [Fact]
    public void A_window_inside_one_day_is_half_open_so_the_ends_do_not_overlap()
    {
        var office = new CloudTransferTimeWindow(new TimeOnly(9, 0), new TimeOnly(18, 0));

        Assert.False(office.Contains(new TimeOnly(8, 59)));
        Assert.True(office.Contains(new TimeOnly(9, 0)));
        Assert.True(office.Contains(new TimeOnly(17, 59)));
        Assert.False(office.Contains(new TimeOnly(18, 0)));
    }

    [Fact]
    public void Equal_ends_mean_the_whole_day_rather_than_nothing()
    {
        // The reading that matters for the queue: a window of 09:00–09:00 must not park every transfer forever.
        // The settings page refuses that input for exactly this reason — see ConfigurationValidatorTests.
        var whole = new CloudTransferTimeWindow(new TimeOnly(9, 0), new TimeOnly(9, 0));

        Assert.True(whole.Contains(new TimeOnly(0, 0)));
        Assert.True(whole.Contains(new TimeOnly(12, 0)));
        Assert.True(whole.Contains(new TimeOnly(23, 59)));
    }

    [Fact]
    public void The_delay_until_the_window_opens_is_the_time_until_its_start()
    {
        var overnight = new CloudTransferTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));

        Assert.Equal(TimeSpan.Zero, overnight.DelayUntilOpen(new TimeOnly(23, 0)));
        // 06:30 is outside the window, and the next opening is tonight.
        Assert.Equal(TimeSpan.FromHours(15.5), overnight.DelayUntilOpen(new TimeOnly(6, 30)));
        // 21:30 is outside the window, and the next opening is half an hour away.
        Assert.Equal(TimeSpan.FromMinutes(30), overnight.DelayUntilOpen(new TimeOnly(21, 30)));
    }

    [Fact]
    public void The_window_reads_back_the_way_the_user_typed_it()
    {
        Assert.Equal("22:00–06:00", new CloudTransferTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0)).Describe());
        Assert.Equal("09:05–18:00", new CloudTransferTimeWindow(new TimeOnly(9, 5), new TimeOnly(18, 0)).Describe());
    }

    [Theory]
    [InlineData("22:00", "06:00", 22, 0, 6, 0)]
    [InlineData("9:00", "18:00", 9, 0, 18, 0)]
    [InlineData(" 08:30 ", " 17:45 ", 8, 30, 17, 45)]
    public void A_window_is_parsed_from_the_two_text_boxes(string start, string end, int startHour, int startMinute, int endHour, int endMinute)
    {
        var window = CloudTransferTimeWindow.Parse(start, end);

        Assert.NotNull(window);
        Assert.Equal(new TimeOnly(startHour, startMinute), window!.Start);
        Assert.Equal(new TimeOnly(endHour, endMinute), window.End);
    }

    [Theory]
    [InlineData("", "06:00")]
    [InlineData("22:00", "")]
    [InlineData(null, null)]
    [InlineData("25:00", "06:00")]
    [InlineData("22:00", "6pm")]
    [InlineData("22:00:00", "06:00")]
    public void An_unreadable_half_is_reported_as_unreadable_rather_than_guessed(string? start, string? end)
    {
        Assert.Null(CloudTransferTimeWindow.Parse(start, end));
    }

    [Fact]
    public void Equal_ends_are_parsed_rather_than_refused_so_the_page_can_name_that_problem()
    {
        // Kept apart from "unreadable" on purpose: the two need different messages, and only the settings page
        // knows which box the user typed into.
        var window = CloudTransferTimeWindow.Parse("09:00", "09:00");

        Assert.NotNull(window);
        Assert.Equal(window!.Start, window.End);
    }

    [Fact]
    public void The_validator_refuses_the_two_settings_the_queue_could_not_honour()
    {
        // Both are refused where the user can still see why, instead of becoming a queue that never starts a
        // transfer or a daemon held to a rate nobody can explain.
        var equalEnds = new ApplicationSettings
        {
            CloudTransferTimeWindow = new CloudTransferTimeWindow(new TimeOnly(9, 0), new TimeOnly(9, 0))
        };
        Assert.Contains(ConfigurationValidator.Validate(equalEnds), error => error.Contains("全天可传输"));

        var negativeLimit = new ApplicationSettings { CloudUploadLimitBytesPerSecond = -1 };
        Assert.Contains(ConfigurationValidator.Validate(negativeLimit), error => error.Contains("上传限速"));

        var hugeLimit = new ApplicationSettings { CloudDownloadLimitBytesPerSecond = (1L << 50) + 1 };
        Assert.Contains(ConfigurationValidator.Validate(hugeLimit), error => error.Contains("下载限速"));

        // 0 is a real choice in both places: no ceiling, and no window at all.
        var valid = new ApplicationSettings
        {
            CloudUploadLimitBytesPerSecond = 0,
            CloudDownloadLimitBytesPerSecond = 1024,
            CloudTransferTimeWindow = new CloudTransferTimeWindow(new TimeOnly(22, 0), new TimeOnly(6, 0))
        };
        Assert.Empty(ConfigurationValidator.Validate(valid));
    }

    [Fact]
    public void The_post_transfer_actions_keep_their_numbers_because_they_are_persisted_as_one()
    {
        // The settings file stores the choice as an integer, so renumbering a member would silently repaint
        // somebody's saved choice as a different action — the same rule the mount cache mode follows.
        Assert.Equal(0, (int)CloudAfterTransferAction.None);
        Assert.Equal(1, (int)CloudAfterTransferAction.Exit);
        Assert.Equal(2, (int)CloudAfterTransferAction.Sleep);
        Assert.Equal(3, (int)CloudAfterTransferAction.Shutdown);
    }
}
