using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace FengSync.Core;

/// <summary>Owns a private rclone rcd process bound to loopback only. RC credentials stay in child-process environment variables.</summary>
public sealed class RcloneDaemon : IAsyncDisposable
{
    private readonly Process _process;
    private readonly HttpClient _http;
    private readonly string _user;
    private readonly string _password;
    private RcloneDaemon(Process process, HttpClient http, string user, string password, Uri uri) => (_process, _http, _user, _password, BaseUri) = (process, http, user, password, uri);
    public Uri BaseUri { get; }
    public RcloneRcClient Client => new(_http, BaseUri, _user, _password);

    public static async Task<RcloneDaemon> StartAsync(string executable, string configPath, CancellationToken ct = default)
    {
        var port = ReservePort(); var user = "fengsync-" + Guid.NewGuid().ToString("N"); var password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("rcd"); start.ArgumentList.Add("--rc-addr"); start.ArgumentList.Add($"127.0.0.1:{port}"); start.ArgumentList.Add("--config"); start.ArgumentList.Add(configPath);
        start.Environment["RCLONE_RC_USER"] = user; start.Environment["RCLONE_RC_PASS"] = password;
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 rclone。");
        var uri = new Uri($"http://127.0.0.1:{port}/"); var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var daemon = new RcloneDaemon(process, http, user, password, uri);
        try
        {
            for (var attempt = 0; attempt < 25; attempt++) { ct.ThrowIfCancellationRequested(); if (process.HasExited) throw new InvalidOperationException("rclone 启动失败：" + await process.StandardError.ReadToEndAsync(ct)); try { await daemon.Client.CallAsync("core/version", new { }, ct); return daemon; } catch (HttpRequestException) { await Task.Delay(100, ct); } }
            throw new TimeoutException("rclone RC 服务未在预期时间内就绪。");
        }
        catch { await daemon.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        _http.Dispose(); if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); } _process.Dispose();
    }
    private static int ReservePort()
    { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
}
