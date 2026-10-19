using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// The rate a transfer row prints.
/// </summary>
/// <remarks>
/// Split from the window because the arithmetic is where the interesting mistakes are — a rate is a division of a
/// measurement, and the units have to change under the number as it grows — while the row itself only has to put
/// the result on screen.
/// </remarks>
public sealed class LanTransferSpeedTests
{
    [Theory]
    [InlineData(0d, "")]
    [InlineData(-1d, "")]
    [InlineData(double.NaN, "")]
    public void A_rate_nobody_measured_is_not_written(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, LanTransferSpeed.Format(bytesPerSecond));

    [Theory]
    [InlineData(512d, "512 B/s")]
    [InlineData(1024d, "1.0 KB/s")]
    [InlineData(1536d, "1.5 KB/s")]
    [InlineData(12.4 * 1024 * 1024, "12.4 MB/s")]
    // Above a hundred the decimal is the digit that changes ten times a second, so it goes: a rate is read at a
    // glance or not at all, and two extra characters of noise is what stops it being a glance.
    [InlineData(100d * 1024 * 1024, "100 MB/s")]
    [InlineData(2d * 1024 * 1024 * 1024, "2.0 GB/s")]
    public void A_rate_is_written_in_the_unit_it_reached(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, LanTransferSpeed.Format(bytesPerSecond));

    [Fact]
    public void The_units_are_binary_because_every_other_size_in_this_application_is()
    {
        // 1000 B/s is under a kilobyte here, and saying "1.0 KB/s" would make a transfer look faster than it is
        // beside a row that reports its length in the same binary units.
        Assert.Equal("1000 B/s", LanTransferSpeed.Format(1000));
        Assert.Equal("1.0 KB/s", LanTransferSpeed.Format(1024));
    }
}
