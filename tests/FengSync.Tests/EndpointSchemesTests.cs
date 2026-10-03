using FengSync.Core;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers <see cref="EndpointSchemes"/>, the single map between a URI scheme, an rclone backend type and
/// an <see cref="EndpointType"/>. The reason it needs its own tests is that a missing row does not fail to
/// compile anywhere: a cloud URI whose scheme is unknown is silently treated as a local filesystem path,
/// and a remote whose backend type is unknown silently disappears from the endpoint list.
/// </summary>
public sealed class EndpointSchemesTests
{
    [Theory]
    [InlineData("gdrive", "drive", EndpointType.GoogleDrive)]
    [InlineData("onedrive", "onedrive", EndpointType.OneDrive)]
    [InlineData("dropbox", "dropbox", EndpointType.Dropbox)]
    [InlineData("box", "box", EndpointType.Box)]
    [InlineData("webdav", "webdav", EndpointType.WebDav)]
    [InlineData("sftp", "sftp", EndpointType.Sftp)]
    [InlineData("s3", "s3", EndpointType.S3)]
    public void Every_scheme_resolves_to_its_endpoint_type_both_ways(string scheme, string rcloneType, EndpointType type)
    {
        // The scheme and the backend type differ for Google Drive, which is why the map carries both rather
        // than deriving one from the other.
        Assert.Equal(type, EndpointSchemes.TypeForScheme(scheme));
        Assert.Equal(scheme, EndpointSchemes.SchemeFor(type));
        Assert.Equal(rcloneType, EndpointSchemes.RcloneTypeFor(type));
        Assert.Equal(type, EndpointSchemes.TypeForRcloneType(rcloneType));
        Assert.True(EndpointSchemes.IsSelfDescribingRcloneType(rcloneType));
    }

    [Fact]
    public void Every_cloud_type_has_a_scheme_a_backend_type_and_a_label()
    {
        // A type with no row here is one no provider can produce; a type in the catalogue that is not in
        // CloudTypes would never be offered by the editor.
        Assert.Equal(EndpointSchemes.AllSchemes.Count, EndpointSchemes.CloudTypes.Count);
        foreach (var type in EndpointSchemes.CloudTypes)
        {
            Assert.False(string.IsNullOrWhiteSpace(EndpointSchemes.SchemeFor(type)));
            Assert.False(string.IsNullOrWhiteSpace(EndpointSchemes.RcloneTypeFor(type)));
            Assert.NotEqual("本地文件夹", EndpointSchemes.DisplayNameFor(type));
        }
    }

    [Fact]
    public void A_cloud_uri_is_recognised_and_a_local_path_is_not()
    {
        Assert.True(EndpointSchemes.IsRemote("gdrive://personal/docs"));
        Assert.True(EndpointSchemes.IsRemote("webdav://nextcloud/team"));
        // Case is not part of the scheme.
        Assert.True(EndpointSchemes.IsRemote("SFTP://nas/share"));

        Assert.False(EndpointSchemes.IsRemote(@"C:\Users\me\Documents"));
        Assert.False(EndpointSchemes.IsRemote("/home/me/docs"));
        Assert.False(EndpointSchemes.IsRemote(""));
        // A Windows drive letter contains a colon but no "://", and must not be read as a scheme.
        Assert.False(EndpointSchemes.IsRemote("C://odd"));
        // An unknown scheme is a path, not a remote: treating it as one would send a local-looking string
        // to rclone.
        Assert.False(EndpointSchemes.IsRemote("ftp://host/path"));
    }

    [Fact]
    public void A_scheme_with_no_remote_name_is_still_a_remote_so_the_caller_reports_the_real_problem()
    {
        // Reading "sftp://" as a local folder would hide the user's mistake; the factory reports the missing
        // remote name instead, which is the actionable message.
        Assert.Equal("sftp", EndpointSchemes.SchemeOf("sftp://"));
        Assert.True(EndpointSchemes.IsRemote("sftp://"));
    }

    [Fact]
    public void An_alias_is_not_self_describing_but_still_resolves_to_s3()
    {
        // An alias is only an endpoint when Feng Sync's metadata says it created the prefix boundary, so it
        // must not pass the "list me on my own" test — a hand-made alias could point anywhere.
        Assert.False(EndpointSchemes.IsSelfDescribingRcloneType("alias"));
        Assert.Equal(EndpointType.S3, EndpointSchemes.TypeForRcloneType("alias"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("b2")]
    [InlineData("local")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unsupported_backend_type_is_not_self_describing(string? rcloneType)
    {
        Assert.False(EndpointSchemes.IsSelfDescribingRcloneType(rcloneType));
        Assert.Null(EndpointSchemes.TypeForRcloneType(rcloneType));
    }

    [Fact]
    public void A_uri_trims_slashes_and_omits_an_empty_root()
    {
        Assert.Equal("s3://r2/bucket/prefix", EndpointSchemes.BuildUri(EndpointType.S3, "r2", "/bucket/prefix/"));
        Assert.Equal("s3://r2", EndpointSchemes.BuildUri(EndpointType.S3, "r2", ""));
        Assert.Equal("s3://r2", EndpointSchemes.BuildUri(EndpointType.S3, "r2", null));
        Assert.Equal("onedrive://work", EndpointSchemes.BuildUri(EndpointType.OneDrive, "work", "   "));
    }

    [Fact]
    public void A_local_endpoint_has_no_scheme_and_is_rejected_rather_than_guessed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EndpointSchemes.SchemeFor(EndpointType.Local));
        Assert.Throws<ArgumentOutOfRangeException>(() => EndpointSchemes.RcloneTypeFor(EndpointType.Local));
        Assert.Equal("本地文件夹", EndpointSchemes.DisplayNameFor(EndpointType.Local));
    }
}
