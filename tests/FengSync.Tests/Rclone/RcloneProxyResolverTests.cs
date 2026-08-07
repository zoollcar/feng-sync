using FengSync.Core.Rclone.Transport;

namespace FengSync.Tests.Rclone;

public sealed class RcloneProxyResolverTests
{
    [Fact]
    public void Application_proxy_wins_over_environment_and_WinInet()
    {
        var result = RcloneProxyResolver.Resolve(new(HttpsProxy: "app:9000"),
            name => name == "HTTPS_PROXY" ? "env:8000" : null,
            new FakeWinInet(new(true, "127.0.0.1:10800", null)));

        Assert.Equal(RcloneProxySource.Application, result.Source);
        Assert.Equal("http://app:9000", result.HttpsProxy);
        Assert.Contains("127.0.0.1", result.NoProxy);
        Assert.Contains("localhost", result.NoProxy);
    }

    [Fact]
    public void Environment_proxy_wins_over_WinInet()
    {
        var result = RcloneProxyResolver.Resolve(environment:
            name => name == "ALL_PROXY" ? "socks5://env:1080" : null,
            winInet: new FakeWinInet(new(true, "127.0.0.1:10800", null)));

        Assert.Equal(RcloneProxySource.Environment, result.Source);
        Assert.Equal("socks5://env:1080", result.AllProxy);
    }

    [Fact]
    public void WinInet_protocol_map_is_converted_to_rclone_proxy_variables()
    {
        var result = RcloneProxyResolver.Resolve(environment: _ => null,
            winInet: new FakeWinInet(new(true, "http=proxy:80;https=secure:443;socks=socks:1080", null)));

        Assert.Equal(RcloneProxySource.WinInet, result.Source);
        Assert.Equal("http://proxy:80", result.HttpProxy);
        Assert.Equal("http://secure:443", result.HttpsProxy);
        Assert.Equal("socks5://socks:1080", result.AllProxy);
    }

    [Fact]
    public void Pac_only_configuration_is_reported_as_unsupported()
    {
        var result = RcloneProxyResolver.Resolve(environment: _ => null,
            winInet: new FakeWinInet(new(false, null, "https://proxy.test/config.pac")));

        Assert.True(result.HasUnsupportedPac);
        Assert.Equal("https://proxy.test/config.pac", result.AutoConfigUrl);
    }

    private sealed class FakeWinInet(WinInetProxySettings settings) : IWinInetProxyReader
    {
        public WinInetProxySettings Read() => settings;
    }
}
