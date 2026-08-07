using System.Diagnostics;
using System.Net;
using FengSync.Core.Rclone.Transport;

namespace FengSync.Core;

/// <summary>
/// Shared environment & DNS preparation for every bundled <c>rclone.exe</c> child process
/// Feng Sync launches. Centralised so the single RC daemon consistently receives proxy
/// propagation, Go-resolver selection and OAuth DNS warm-up behavior.
/// </summary>
/// <remarks>
/// WPF GUI processes occasionally lose the proxy environment variables the user's shell
/// or launcher set. Reinjecting them plus <c>GODEBUG=netdns=go</c> keeps rclone's outbound
/// HTTP (including the Google Drive OAuth token exchange) on the user's TUN / system proxy
/// instead of falling back to a direct socket that hits TUN-mode fakedns drops.
/// </remarks>
public static class RcloneEnvironment
{
    /// <summary>Standard proxy variables, both capitalisations and <c>all_proxy</c>.</summary>
    private static readonly string[] ProxyEnvNames =
    {
        "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NO_PROXY",
        "https_proxy", "http_proxy", "all_proxy", "no_proxy"
    };

    /// <summary>Hosts whose positive DNS cache entry is required before rclone can complete OAuth.</summary>
    private static readonly string[] OAuthHosts = { "oauth2.googleapis.com", "accounts.google.com" };

    /// <summary>
    /// Copies the parent's proxy environment into <paramref name="start"/> and forces
    /// rclone's Go runtime onto the pure-Go resolver. Call before <c>Process.Start(start)</c>.
    /// </summary>
    public static RcloneProxyConfiguration Prepare(ProcessStartInfo start, RcloneProxyOptions? applicationProxy = null,
        IWinInetProxyReader? winInet = null)
    {
        var proxy = RcloneProxyResolver.Resolve(applicationProxy, Environment.GetEnvironmentVariable, winInet);
        foreach (var name in ProxyEnvNames) start.Environment.Remove(name);
        SetPair(start, "HTTP_PROXY", "http_proxy", proxy.HttpProxy);
        SetPair(start, "HTTPS_PROXY", "https_proxy", proxy.HttpsProxy);
        SetPair(start, "ALL_PROXY", "all_proxy", proxy.AllProxy);
        SetPair(start, "NO_PROXY", "no_proxy", proxy.NoProxy);
        // Keep the diagnostic state out of rclone's semantics while making it available
        // to the owning process and support bundles without exposing a proxy credential.
        start.Environment["FENGSYNC_RCLONE_PROXY_SOURCE"] = proxy.Source.ToString();
        if (proxy.HasUnsupportedPac)
        {
            start.Environment["FENGSYNC_RCLONE_PROXY_DIAGNOSTIC"] =
                "Windows 使用 PAC 自动代理；rclone 无法直接采用 PAC，请在 Feng Sync 中配置固定代理。";
        }
        // TUN-mode cgo getaddrinfo occasionally returns EAI_NONAME for *.googleapis.com
        // when fakedns has no warm cache. Pure-Go resolver falls back to the configured
        // DNS server transparently.
        start.Environment["GODEBUG"] = "netdns=go";
        return proxy;
    }

    private static void SetPair(ProcessStartInfo start, string upper, string lower, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        start.Environment[upper] = value;
        start.Environment[lower] = value;
    }

    /// <summary>
    /// Resolves the OAuth endpoints once so TUN-mode fakedns / system DNS client caches them
    /// before rclone makes its POST. Failures are swallowed — a warm cache miss must not
    /// break the caller's flow.
    /// </summary>
    public static async Task WarmOAuthDnsAsync(CancellationToken ct = default)
    {
        foreach (var host in OAuthHosts)
        {
            try { _ = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false); }
            catch { /* best-effort warm-up */ }
        }
    }
}
