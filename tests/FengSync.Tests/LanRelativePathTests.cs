using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers <see cref="LanRelativePath"/>, the only thing standing between a path a peer named on its own
/// filesystem and a directory this application is willing to write into.
/// </summary>
/// <remarks>
/// The unsafe cases matter more than the safe ones, and each demonstrates the behaviour under test rather
/// than asserting a repair: a traversal that got flattened into a bare name would still publish a file — in
/// the wrong place, with no error anybody would ever see. Refusal is the contract.
/// </remarks>
public sealed class LanRelativePathTests
{
    [Theory]
    [InlineData("a.txt")]
    [InlineData("docs/a.txt")]
    [InlineData("相册/照片/a.txt")]
    [InlineData("docs/deep/nest/er/a.txt")]
    [InlineData("a b+c.txt")]
    [InlineData("a.txt.bak")]
    public void A_relative_path_is_kept_segment_for_segment(string wire)
    {
        Assert.True(LanRelativePath.TryResolve(wire, out var safe, out var refusal));
        Assert.Equal(wire, safe);
        Assert.Equal(LanRefusal.None, refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/absolute/a.txt")]
    [InlineData("C:/windows/a.txt")]
    [InlineData("docs/../a.txt")]
    [InlineData("../../a.txt")]
    [InlineData("docs/..")]
    [InlineData("./a.txt")]
    [InlineData("a//b.txt")]
    [InlineData("a/b\t.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("stream:name")]
    [InlineData("a/")]
    [InlineData("a/b.")]
    [InlineData("a/b ")]
    public void An_unsafe_or_malformed_path_is_refused(string? wire)
    {
        Assert.False(LanRelativePath.TryResolve(wire, out var safe, out var refusal));
        // A refusal that still handed back a name is the bug this line exists to catch.
        Assert.Equal("", safe);
        Assert.NotEqual(LanRefusal.None, refusal);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("docs/PRN/data.txt")]
    [InlineData("LPT1")]
    [InlineData("COM9")]
    public void A_windows_reserved_device_name_is_named_as_such(string wire)
    {
        Assert.False(LanRelativePath.TryResolve(wire, out _, out var refusal));
        Assert.Equal(LanRefusal.NameReserved, refusal);
    }

    [Theory]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("LPT0")]
    [InlineData("CONsole")]
    [InlineData("myCON.txt")]
    public void A_name_that_only_looks_reserved_is_accepted(string wire)
    {
        // COM0 and COM10 are not devices; refusing them would reject files the user really has.
        Assert.True(LanRelativePath.TryResolve(wire, out _, out _));
    }

    [Fact]
    public void A_name_over_what_any_filesystem_can_hold_is_refused_rather_than_truncated()
    {
        // The composed path against the real receive directory is measured by LanReceiveSink, which is the
        // only place that knows the root; here the one bound that holds is the per-name limit.
        var wire = new string('x', LanProtocol.MaximumSegmentLength + 1);
        Assert.False(LanRelativePath.TryResolve(wire, out var safe, out var refusal));
        Assert.Equal(LanRefusal.PathTooLong, refusal);
        Assert.Equal("", safe);
    }

    [Fact]
    public void A_long_name_inside_a_nested_path_is_still_measured_per_segment()
    {
        var wire = "docs/" + new string('y', LanProtocol.MaximumSegmentLength + 1) + ".txt";
        Assert.False(LanRelativePath.TryResolve(wire, out _, out var refusal));
        Assert.Equal(LanRefusal.PathTooLong, refusal);
    }

    [Fact]
    public void The_name_and_the_parent_are_split_on_the_wire_separator_only()
    {
        Assert.Equal("a.txt", LanRelativePath.NameOf("docs/deep/a.txt"));
        Assert.Equal("docs/deep", LanRelativePath.ParentOf("docs/deep/a.txt"));
        Assert.Equal("a.txt", LanRelativePath.NameOf("a.txt"));
        // A bare name has no parent; the receive directory is not a path this type gets to invent.
        Assert.Equal("", LanRelativePath.ParentOf("a.txt"));
    }

    [Fact]
    public void A_chinese_path_survives_the_percent_encoding_a_header_requires()
    {
        // The phone page sends encodeURIComponent because HTTP header values must be ASCII, and this is the
        // round trip the server does on the other side. WebUtility.UrlDecode would turn the escaped plus sign
        // back into a space and silently rename the file — the reason the sink decodes with
        // Uri.UnescapeDataString and nothing else.
        const string original = "相册/带 空格+加号.txt";
        var wire = Uri.EscapeDataString(original);
        Assert.DoesNotContain(' ', wire);
        Assert.DoesNotContain('+', wire);
        Assert.True(LanRelativePath.TryResolve(Uri.UnescapeDataString(wire), out var safe, out _));
        Assert.Equal(original, safe);
    }
}
