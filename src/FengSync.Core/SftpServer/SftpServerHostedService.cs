using System.Net.Sockets;
using System.Text.Json;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Core.SftpServer;

/// <summary>Safe, structured failure for UI display. Secret-bearing RC input is never retained.</summary>
public sealed class SftpServerOperationException : Exception
{
    public SftpServerOperationException(string operation, string message, string suggestedAction, string? technicalCode = null)
        : base(message)
    {
        Operation = operation;
        SuggestedAction = suggestedAction;
        TechnicalCode = technicalCode;
        CorrelationId = Guid.NewGuid().ToString("N");
    }

    public string Operation { get; }
    public string SuggestedAction { get; }
    public string? TechnicalCode { get; }
    public string CorrelationId { get; }
}

/// <summary>Runs the bundled SFTP server through rclone's serve/* JSON RC lifecycle API.</summary>
public sealed class SftpServerHostedService : IAsyncDisposable
{
    private readonly IRcloneLifecycleClient _rc;
    private readonly bool _ownsClient;
    private readonly Func<CancellationToken, Task<string?>> _loadPassword;
    private readonly Func<SftpServerOptions, CancellationToken, Task<bool>> _listenerProbe;
    private string? _serverId;

    public SftpServerHostedService(
        IRcloneLifecycleClient rc,
        Func<CancellationToken, Task<string?>>? loadPassword = null,
        Func<SftpServerOptions, CancellationToken, Task<bool>>? listenerProbe = null)
    {
        _rc = rc;
        _loadPassword = loadPassword ?? (ct => new SftpPasswordStore().LoadAsync(ct));
        _listenerProbe = listenerProbe ?? WaitUntilListeningAsync;
    }

    /// <summary>Compatibility constructor. Application code should inject its shared lifecycle host.</summary>
    public SftpServerHostedService()
    {
        _rc = new RcloneLifecycleHost();
        _ownsClient = true;
        _loadPassword = ct => new SftpPasswordStore().LoadAsync(ct);
        _listenerProbe = WaitUntilListeningAsync;
    }

    public bool IsRunning { get; private set; }
    public string? ServerId => _serverId;
    public string? BoundAddress { get; private set; }
    public SftpServerOptions? Options { get; private set; }

    public async Task StartAsync(SftpServerOptions options, CancellationToken ct = default)
    {
        options.Validate();
        if (!options.Enabled)
        {
            await StopAsync(ct).ConfigureAwait(false);
            Options = options;
            return;
        }

        await StopAsync(ct).ConfigureAwait(false);
        var runtime = new SftpRuntimeDiagnostics().Inspect(options);
        if (!runtime.CanStart)
            throw new SftpServerOperationException("serve/start", runtime.Summary, "检查共享目录、监听地址和 rclone 运行时后重试。", "RuntimeValidation");
        var password = await _loadPassword(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
            throw new SftpServerOperationException("serve/start", "未找到 SFTP 密码。", "请在 SFTP 设置中重新设置密码。", "PasswordMissing");

        var key = options.HostKeyPath ?? new SftpHostKeyStore().GetKeyReference().Path;
        var cache = Path.Combine(AppDataPaths.Root, "sftp", "cache");
        Directory.CreateDirectory(cache);
        JsonElement response;
        try
        {
            // Password is carried only in the authenticated loopback JSON request. Do not retain the
            // payload or attach a raw RC exception because rclone error input may echo it.
            response = await _rc.CallAsync("serve/start", new
            {
                type = "sftp",
                fs = ":local:" + Path.GetFullPath(options.RootPath!),
                addr = $"{options.ListenAddress}:{options.Port}",
                user = options.UserName,
                pass = password,
                key,
                vfs_cache_mode = "writes",
                vfs_cache_max_size = options.CacheMaxSizeBytes,
                vfs_cache_max_age = "1h",
                vfs_write_back = "5s",
                _config = new { CacheDir = cache }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RcloneException) { throw; }
        catch (Exception ex)
        {
            throw new SftpServerOperationException("serve/start", "内置 SFTP 服务启动失败。",
                "检查端口是否被占用、共享目录权限和 rclone 日志后重试。", ex.GetType().Name);
        }

        _serverId = GetString(response, "id");
        BoundAddress = GetString(response, "addr") ?? $"{options.ListenAddress}:{options.Port}";
        if (string.IsNullOrWhiteSpace(_serverId) || !await IsServerListedAsync(_serverId, ct).ConfigureAwait(false))
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw new SftpServerOperationException("serve/start", "rclone 未返回可管理的 SFTP 服务标识。",
                "查看诊断日志并确认捆绑 rclone 支持 serve/start。", "MissingServerId");
        }

        if (!await _listenerProbe(options, ct).ConfigureAwait(false))
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw new SftpServerOperationException("serve/start", "内置 SFTP 服务已创建，但未能在预期时间内开始监听。",
                "检查防火墙和监听地址后重试。", "ListenTimeout");
        }
        Options = options;
        IsRunning = true;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var id = _serverId;
        if (string.IsNullOrWhiteSpace(id)) return;
        try
        {
            await _rc.CallAsync("serve/stop", new { id }, ct).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _serverId, null, id);
            IsRunning = false;
            BoundAddress = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            IsRunning = true;
            throw new SftpServerOperationException("serve/stop", "内置 SFTP 服务未能正常停止。",
                "可重试停止；若 rclone 宿主已经退出，服务也会随之结束。", ex.GetType().Name);
        }
    }

    private async Task<bool> IsServerListedAsync(string id, CancellationToken ct)
    {
        var response = await _rc.CallAsync("serve/list", new { }, ct).ConfigureAwait(false);
        if (!response.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return false;
        // Deliberately inspect only id and addr. The params block can contain the password.
        foreach (var server in list.EnumerateArray())
        {
            if (!string.Equals(GetString(server, "id"), id, StringComparison.Ordinal)) continue;
            BoundAddress = GetString(server, "addr") ?? BoundAddress;
            return true;
        }
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<bool> WaitUntilListeningAsync(SftpServerOptions options, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(options.ListenAddress, options.Port, ct).ConfigureAwait(false);
                return true;
            }
            catch (SocketException) { await Task.Delay(50, ct).ConfigureAwait(false); }
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            if (_ownsClient && _rc is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}
