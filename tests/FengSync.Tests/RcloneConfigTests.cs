using FengSync.Core;

namespace FengSync.Tests;

public sealed class RcloneConfigTests
{
    [Fact]
    public void Config_dump_parses_only_supported_cloud_accounts()
    {
        const string json = "{\"driveA\":{\"type\":\"drive\",\"token\":\"secret\"},\"server\":{\"type\":\"sftp\",\"host\":\"example.test\"},\"bucket\":{\"type\":\"s3\"},\"local\":{\"type\":\"local\"}}";
        var accounts = RcloneConfig.ParseDump(json);
        Assert.Collection(accounts, first => { Assert.Equal("bucket", first.Name); Assert.True(first.IsS3); }, second => { Assert.Equal("driveA", second.Name); Assert.True(second.IsGoogleDrive); }, third => { Assert.Equal("server", third.Name); Assert.False(third.IsGoogleDrive); });
    }
    [Fact]
    public void Non_object_dump_is_treated_as_empty_account_list() => Assert.Empty(RcloneConfig.ParseDump("[]"));

    [Fact]
    public void Config_dump_preserves_unicode_remote_names_for_later_delete_or_reconnect()
    {
        var account = Assert.Single(RcloneConfig.ParseDump("{\"SFTP_连接\":{\"type\":\"sftp\"}}"));

        Assert.Equal("SFTP_连接", account.Name);
        Assert.Equal("SFTP  ·  SFTP_连接", account.Display);
    }
}
