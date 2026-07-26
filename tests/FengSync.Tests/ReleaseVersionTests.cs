using FengSync.Core.Updates;

namespace FengSync.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.2.3+build.9", "1.2.3")]
    [InlineData("0.0.0", "0.0.0")]
    public void Parses_official_versions_and_discards_build_metadata(string text, string expected) => Assert.Equal(expected, ReleaseVersion.Parse(text).ToString());

    [Fact]
    public void Prerelease_is_identified_so_release_clients_can_reject_it() { Assert.True(ReleaseVersion.TryParse("1.2.3-preview", out var parsed)); Assert.True(parsed.IsPrerelease); }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.-2.3")]
    [InlineData("")]
    public void Rejects_invalid_values(string text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void Compares_three_numeric_components() { var v = ReleaseVersion.Parse("1.2.3"); Assert.True(v > ReleaseVersion.Parse("1.2.2")); Assert.Equal(0, v.CompareTo(ReleaseVersion.Parse("1.2.3"))); Assert.True(v < ReleaseVersion.Parse("1.3.0")); }
}
