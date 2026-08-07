using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FengSync.Core.Rclone.Diagnostics;
using FengSync.Core.Rclone.Transport;

namespace FengSync.Core;

/// <summary>Owns a private rclone rcd process bound to loopback only. RC credentials stay in child-process environment variables.</summary>
public sealed class RcloneDaemon : IAsyncDisposable
{
    private readonly Process _process;
    private readonly HttpClient _http;
    private readonly string _user;
    private readonly string _password;
    private readonly Task _stdoutDrain;
    private readonly Task _stderrDrain;
    private readonly Queue<string> _startupErrors = new();
    private readonly object _errorGate = new();
    private RcloneDaemon(Process process, HttpClient http, string user, string password, Uri uri, IRcloneLogSink sink,
        RcloneProxyConfiguration proxyConfiguration)
    {
        (_process, _http, _user, _password, BaseUri) = (process, http, user, password, uri);
        ProxyConfiguration = proxyConfiguration;
        _stdoutDrain = DrainAsync(process.StandardOutput, "stdout", sink);
        _stderrDrain = DrainAsync(process.StandardError, "stderr", sink, CaptureStartupError);
    }
    public Uri BaseUri { get; }
    public bool HasExited => _process.HasExited;
    public RcloneProxyConfiguration ProxyConfiguration { get; }
    public RcloneRcClient Client => new(_http, BaseUri, _user, _password);

    public static async Task<RcloneDaemon> StartAsync(string executable, string configPath, CancellationToken ct = default,
        IRcloneLogSink? logSink = null, RcloneProxyOptions? proxyOptions = null)
    {
        var port = ReservePort(); var user = "fengsync-" + Guid.NewGuid().ToString("N"); var password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        var proxyConfiguration = RcloneEnvironment.Prepare(start, proxyOptions);
        start.ArgumentList.Add("rcd"); start.ArgumentList.Add("--use-json-log"); start.ArgumentList.Add("--rc-addr"); start.ArgumentList.Add($"127.0.0.1:{port}"); start.ArgumentList.Add("--config"); start.ArgumentList.Add(configPath);
        start.Environment["RCLONE_RC_USER"] = user; start.Environment["RCLONE_RC_PASS"] = password;
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 rclone。");
        // Cloud backends may need a token refresh and an initial page walk.  Keep this bounded, but do not race a healthy Drive request.
        var uri = new Uri($"http://127.0.0.1:{port}/"); var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var daemon = new RcloneDaemon(process, http, user, password, uri,
            logSink ?? new CompositeRcloneLogSink(new TraceRcloneLogSink(), new FileRcloneLogSink()), proxyConfiguration);
        try
        {
            for (var attempt = 0; attempt < 25; attempt++) { ct.ThrowIfCancellationRequested(); if (process.HasExited) throw new InvalidOperationException("rclone 启动失败：" + daemon.StartupError); try { using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct); ready.CancelAfter(TimeSpan.FromSeconds(2)); await daemon.Client.CallAsync("core/version", new { }, ready.Token); return daemon; } catch (RcloneException) when (!process.HasExited) { await Task.Delay(100, ct); } catch (OperationCanceledException) when (!ct.IsCancellationRequested && !process.HasExited) { await Task.Delay(100, ct); } }
            throw new TimeoutException("rclone RC 服务未在预期时间内就绪。");
        }
        catch { await daemon.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    using var quit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await Client.CallAsync("core/quit", new { }, quit.Token).ConfigureAwait(false);
                    await _process.WaitForExitAsync(quit.Token).ConfigureAwait(false);
                }
                catch
                {
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await Task.WhenAll(_stdoutDrain, _stderrDrain).ConfigureAwait(false);
            _http.Dispose();
            _process.Dispose();
        }
    }
    private static int ReservePort()
    { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }

    private string StartupError { get { lock (_errorGate) return string.Join(Environment.NewLine, _startupErrors); } }
    private void CaptureStartupError(string line)
    {
        lock (_errorGate)
        {
            if (_startupErrors.Count >= 20) _startupErrors.Dequeue();
            _startupErrors.Enqueue(line);
        }
    }
    private static async Task DrainAsync(StreamReader reader, string stream, IRcloneLogSink sink,
        Action<string>? rawLine = null)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            rawLine?.Invoke(RcloneFailureClassifier.RedactText(line));
            sink.Write(RcloneLogParser.Parse(line, stream));
        }
    }
}
