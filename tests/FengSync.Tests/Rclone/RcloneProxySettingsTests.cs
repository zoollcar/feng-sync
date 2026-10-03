using System.Diagnostics;
using System.Text.RegularExpressions;
using FengSync.Core;
using FengSync.Core.Configuration;
using FengSync.Core.Rclone.Transport;

namespace FengSync.Tests.Rclone;

/// <summary>
/// The proxy setting has to survive three hops before it can do anything: the settings file, the resolver,
/// and the environment of the rclone child. Every one of them used to be able to lose it silently — most
/// importantly the last, because rclone's HTTP client reads only HTTP_PROXY/HTTPS_PROXY and never
/// ALL_PROXY, so a value written into the wrong variable looks configured and changes nothing.
/// </summary>
public sealed class RcloneProxySettingsTests
{
    [Fact]
    public void An_empty_setting_means_inherit()
    {
        Assert.Null(RcloneProxySettings.From(new ApplicationSettings()));
        Assert.Null(RcloneProxySettings.From(new ApplicationSettings { RcloneProxy = "" }));
        Assert.Null(RcloneProxySettings.From(new ApplicationSettings { RcloneProxy = "   " }));
    }

    [Fact]
    public void A_configured_proxy_fills_every_variable_rclone_could_read()
    {
        var options = RcloneProxySettings.From(new ApplicationSettings { RcloneProxy = "  proxy.corp:8080  " });

        // rclone ignores ALL_PROXY, so leaving the http/https slots empty would make this a no-op. The
        // trailing spaces are what a paste from a wiki page looks like; they must not reach the child.
        Assert.Equal("proxy.corp:8080", options!.HttpProxy);
        Assert.Equal("proxy.corp:8080", options.HttpsProxy);
        Assert.Equal("proxy.corp:8080", options.AllProxy);
    }

    [Fact]
    public async Task The_setting_survives_a_round_trip_and_reaches_the_child_environment()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fengsync-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "FengSync.local.json");
        try
        {
            await new SettingsStore(path).SaveAsync(new ApplicationSettings { RcloneProxy = "socks5://127.0.0.1:1080" });
            var loaded = await new SettingsStore(path).LoadAsync();

            var start = new ProcessStartInfo("rclone.exe", "version") { UseShellExecute = false };
            var resolved = RcloneEnvironment.Prepare(start, RcloneProxySettings.From(loaded.Settings));

            Assert.Equal(RcloneProxySource.Application, resolved.Source);
            Assert.Equal("socks5://127.0.0.1:1080", start.Environment["HTTPS_PROXY"]);
            Assert.Equal("socks5://127.0.0.1:1080", start.Environment["HTTP_PROXY"]);
            // The daemon's own RC endpoint is loopback; routing that through the proxy would break it.
            Assert.Contains("127.0.0.1", start.Environment["NO_PROXY"]);
            Assert.Contains("localhost", start.Environment["NO_PROXY"]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("proxy.corp:8080")]
    [InlineData("http://proxy.corp:8080")]
    [InlineData("socks5://127.0.0.1:1080")]
    [InlineData("user:pass@proxy.corp:8080")]
    public void A_usable_address_is_accepted(string? value) =>
        Assert.Empty(ConfigurationValidator.Validate(new ApplicationSettings { RcloneProxy = value }));

    [Theory]
    [InlineData("http://")]
    [InlineData("pro xy:8080")]
    [InlineData("http:// /")]
    public void An_address_that_cannot_be_dialled_is_rejected(string value) =>
        Assert.Contains(ConfigurationValidator.Validate(new ApplicationSettings { RcloneProxy = value }),
            error => error.Contains("代理", StringComparison.Ordinal));

    [Fact]
    public void Every_shell_that_owns_an_rclone_daemon_attaches_the_proxy_provider()
    {
        // The provider is the only thing that connects the setting to the daemon, and it has to be set once
        // per process: FengSync.exe and DriveHub.exe each own a daemon, and neither runs the other's App.
        // Constructing either one here is impossible (WPF allows a single Application per AppDomain), so the
        // wiring is checked at the source, the same way the WPF design-system guardrails are.
        var shells = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "App.xaml.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(file => file.Text.Contains("RcloneLifecycleHost", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(shells);
        var unwired = shells.Where(file => !ProxyProviderAssignment.IsMatch(file.Text))
            .Select(file => file.Path).ToArray();
        Assert.True(unwired.Length == 0,
            "这些入口自己拥有 rclone daemon，但没有接上代理设置，设置页的改动对它们无效：" + string.Join(", ", unwired));
    }

    /// <summary>Matches the assignment, not just a mention: a field merely named after the hook wires nothing.</summary>
    private static readonly Regex ProxyProviderAssignment = new(@"\bProxyOptionsProvider\s*=", RegexOptions.CultureInvariant);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FengSync.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("未能从测试执行目录向上定位 FengSync.sln。");
    }
}
