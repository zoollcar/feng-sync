using System.Diagnostics;
using System.Text.Json;
using FengSync.Core.Rclone.Transport;

namespace FengSync.Core.Rclone.Lifecycle;

/// <summary>
/// Lazily owns one authenticated loopback rclone daemon for all application-lifetime mount and serve
/// operations. The host can later be replaced by a wider application RcloneHost without changing the
/// lifecycle services.
/// </summary>
public sealed class RcloneLifecycleHost : IRcloneLifecycleClient, IAsyncDisposable
{
    private readonly SemaphoreSlim _startupLock = new(1, 1);
    private RcloneDaemon? _daemon;
    private bool _disposed;

    /// <summary>
    /// Supplies the proxy for the daemon. Called at most once, under the startup lock, immediately before
    /// the child process is created — rclone reads its proxy from the environment at start and offers no
    /// way to change it afterwards, so a later settings edit cannot reach a running daemon. Null means
    /// "resolve from the environment, then the Windows Internet Settings".
    /// </summary>
    public Func<CancellationToken, Task<RcloneProxyOptions?>>? ProxyOptionsProvider { get; set; }

    public async Task<JsonElement> CallAsync(string operation, object payload, CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.CallAsync(operation, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns a client backed by the application-lifetime daemon.</summary>
    public async Task<RcloneRcClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var daemon = await GetDaemonAsync(cancellationToken).ConfigureAwait(false);
        return daemon.Client;
    }

    private async Task<RcloneDaemon> GetDaemonAsync(CancellationToken cancellationToken)
    {
        if (_daemon is { HasExited: false }) return _daemon;
        await _startupLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_daemon is { HasExited: true })
            {
                await _daemon.DisposeAsync().ConfigureAwait(false);
                _daemon = null;
            }
            return _daemon ??= await RcloneDaemon.StartAsync(
                BundledRclone.ExecutablePath,
                BundledRclone.ConfigPath,
                cancellationToken,
                proxyOptions: await ResolveProxyOptionsAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally { _startupLock.Release(); }
    }

    /// <summary>
    /// A settings read must never stop the daemon: without a proxy the user still gets a working (if
    /// slower, or firewalled) connection, whereas refusing to start leaves them with no cloud access at
    /// all and no way to fix it from inside the application. The failure is logged instead.
    /// </summary>
    private async Task<RcloneProxyOptions?> ResolveProxyOptionsAsync(CancellationToken cancellationToken)
    {
        if (ProxyOptionsProvider is not { } provider) return null;
        try { return await provider(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Trace.TraceWarning("Unable to read the configured rclone proxy; falling back to the environment: " + ex);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _startupLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_daemon is not null) await _daemon.DisposeAsync().ConfigureAwait(false);
            _daemon = null;
        }
        finally
        {
            _startupLock.Release();
            _startupLock.Dispose();
        }
    }
}
