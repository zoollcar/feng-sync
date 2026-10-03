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

    /// <summary>
    /// rclone's HTTP client reads HTTP_PROXY/HTTPS_PROXY and ignores ALL_PROXY entirely (verified against
    /// the bundled binary: an ALL_PROXY run reached the target, the HTTPS_PROXY run failed with
    /// "proxyconnect tcp"). An all_proxy-only configuration therefore has to be mirrored, or the proxy the
    /// user configured is exported to the child and then quietly dropped.
    /// </summary>
    [Fact]
    public void An_all_proxy_only_configuration_is_mirrored_into_the_variables_rclone_reads()
    {
        var result = RcloneProxyResolver.Resolve(environment:
            name => name == "all_proxy" ? "socks5://env:1080" : null);

        Assert.Equal(RcloneProxySource.Environment, result.Source);
        Assert.Equal("socks5://env:1080", result.AllProxy);
        Assert.Equal("socks5://env:1080", result.HttpProxy);
        Assert.Equal("socks5://env:1080", result.HttpsProxy);
    }

    [Fact]
    public void A_protocol_specific_proxy_is_not_overwritten_by_all_proxy()
    {
        var result = RcloneProxyResolver.Resolve(environment: name => name switch
        {
            "HTTP_PROXY" => "http://plain:80",
            "HTTPS_PROXY" => "http://secure:443",
            "ALL_PROXY" => "socks5://fallback:1080",
            _ => null
        });

        Assert.Equal("http://plain:80", result.HttpProxy);
        Assert.Equal("http://secure:443", result.HttpsProxy);
        Assert.Equal("socks5://fallback:1080", result.AllProxy);
    }

    /// <summary>Windows stores a SOCKS proxy in its own "socks=" slot, which only ever produced all_proxy.</summary>
    [Fact]
    public void A_socks_entry_from_Windows_Internet_Settings_reaches_the_variables_rclone_reads()
    {
        var result = RcloneProxyResolver.Resolve(environment: _ => null,
            winInet: new FakeWinInet(new(true, "socks=socks:1080", null)));

        Assert.Equal(RcloneProxySource.WinInet, result.Source);
        Assert.Equal("socks5://socks:1080", result.HttpProxy);
        Assert.Equal("socks5://socks:1080", result.HttpsProxy);
    }

    private sealed class FakeWinInet(WinInetProxySettings settings) : IWinInetProxyReader
    {
        public WinInetProxySettings Read() => settings;
    }
}
