using System.Text.Json;

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
                cancellationToken).ConfigureAwait(false);
        }
        finally { _startupLock.Release(); }
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
