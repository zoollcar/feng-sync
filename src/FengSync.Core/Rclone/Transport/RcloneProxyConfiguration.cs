using Microsoft.Win32;

namespace FengSync.Core.Rclone.Transport;

public enum RcloneProxySource { None, Application, Environment, WinInet }

public sealed record RcloneProxyOptions(string? HttpProxy = null, string? HttpsProxy = null,
    string? AllProxy = null, string? NoProxy = null);

public sealed record RcloneProxyConfiguration(
    RcloneProxySource Source,
    string? HttpProxy,
    string? HttpsProxy,
    string? AllProxy,
    string NoProxy,
    string? AutoConfigUrl = null)
{
    public bool HasStaticProxy => !string.IsNullOrWhiteSpace(HttpProxy) ||
        !string.IsNullOrWhiteSpace(HttpsProxy) || !string.IsNullOrWhiteSpace(AllProxy);
    public bool HasUnsupportedPac => !HasStaticProxy && !string.IsNullOrWhiteSpace(AutoConfigUrl);
}

public interface IWinInetProxyReader
{
    WinInetProxySettings Read();
}

public sealed record WinInetProxySettings(bool Enabled, string? ProxyServer, string? AutoConfigUrl);

public sealed class WindowsRegistryProxyReader : IWinInetProxyReader
{
    public WinInetProxySettings Read()
    {
        if (!OperatingSystem.IsWindows()) return new(false, null, null);
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return new(Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) != 0,
            key?.GetValue("ProxyServer") as string, key?.GetValue("AutoConfigURL") as string);
    }
}

public static class RcloneProxyResolver
{
    private const string LoopbackNoProxy = "127.0.0.1,localhost,::1";

    public static RcloneProxyConfiguration Resolve(RcloneProxyOptions? application = null,
        Func<string, string?>? environment = null, IWinInetProxyReader? winInet = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        winInet ??= new WindowsRegistryProxyReader();
        var environmentNoProxy = First(environment("NO_PROXY"), environment("no_proxy"));
        if (HasProxy(application))
            return Build(RcloneProxySource.Application, application!.HttpProxy, application.HttpsProxy,
                application.AllProxy, application.NoProxy ?? environmentNoProxy);

        var http = First(environment("HTTP_PROXY"), environment("http_proxy"));
        var https = First(environment("HTTPS_PROXY"), environment("https_proxy"));
        var all = First(environment("ALL_PROXY"), environment("all_proxy"));
        var no = environmentNoProxy;
        if (HasProxy(new(http, https, all, no)))
            return Build(RcloneProxySource.Environment, http, https, all, no);

        var system = winInet.Read();
        if (system.Enabled && !string.IsNullOrWhiteSpace(system.ProxyServer))
        {
            var parsed = ParseWinInet(system.ProxyServer);
            return Build(RcloneProxySource.WinInet, parsed.HttpProxy, parsed.HttpsProxy,
                parsed.AllProxy, environmentNoProxy, system.AutoConfigUrl);
        }
        return new(RcloneProxySource.None, null, null, null, MergeNoProxy(null), system.AutoConfigUrl);
    }

    private static RcloneProxyConfiguration Build(RcloneProxySource source, string? http, string? https,
        string? all, string? no, string? pac = null) => new(source, Normalize(http), Normalize(https),
            Normalize(all), MergeNoProxy(no), pac);

    private static RcloneProxyOptions ParseWinInet(string value)
    {
        if (!value.Contains('='))
        {
            var proxy = Normalize(value);
            return new(proxy, proxy, proxy);
        }
        string? http = null, https = null, all = null;
        foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2) continue;
            switch (pair[0].ToLowerInvariant())
            {
                case "http": http = pair[1]; break;
                case "https": https = pair[1]; break;
                case "socks": all = pair[1].Contains("://", StringComparison.Ordinal) ? pair[1] : "socks5://" + pair[1]; break;
            }
        }
        return new(http, https, all);
    }

    private static bool HasProxy(RcloneProxyOptions? value) => value is not null &&
        (!string.IsNullOrWhiteSpace(value.HttpProxy) || !string.IsNullOrWhiteSpace(value.HttpsProxy) ||
         !string.IsNullOrWhiteSpace(value.AllProxy));
    private static string? First(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second;
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;
    }
    private static string MergeNoProxy(string? value)
    {
        var entries = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var loopback in LoopbackNoProxy.Split(','))
            if (!entries.Contains(loopback, StringComparer.OrdinalIgnoreCase)) entries.Add(loopback);
        return string.Join(',', entries);
    }
}
