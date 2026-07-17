using FengSync.Core;

namespace FengSync.Tests;

public sealed class RcloneConfigTests
{
    [Fact]
    public void Config_dump_parses_only_supported_cloud_accounts()
    {
        const string json = "{\"driveA\":{\"type\":\"drive\",\"token\":\"secret\"},\"server\":{\"type\":\"sftp\",\"host\":\"example.test\"},\"local\":{\"type\":\"local\"}}";
        var accounts = RcloneConfig.ParseDump(json);
        Assert.Collection(accounts, first => { Assert.Equal("driveA", first.Name); Assert.True(first.IsGoogleDrive); }, second => { Assert.Equal("server", second.Name); Assert.False(second.IsGoogleDrive); });
    }
    [Fact]
    public void Non_object_dump_is_treated_as_empty_account_list() => Assert.Empty(RcloneConfig.ParseDump("[]"));
}
