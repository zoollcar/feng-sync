using FengSync.Core.Mount;

namespace FengSync.Tests.Mount;

public sealed class WinFspDetectorTests
{
    [Fact]
    public void Detect_returns_a_status_with_a_helpful_summary()
    {
        // We deliberately don't assert Installed==true because CI machines rarely have WinFsp.
        // Instead we check the contract: the result has a non-empty summary in both branches.
        var status = WinFspDetector.Detect();
        Assert.NotNull(status);
        Assert.NotNull(status.Summary);
        Assert.False(string.IsNullOrWhiteSpace(status.Summary));
    }
}