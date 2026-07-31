using System.Diagnostics;
using System.Net.Sockets;

namespace FengSync.Core.SftpServer;

/// <summary>Runs the bundled rclone SFTP server as an application-owned child process.</summary>
public sealed class SftpServerHostedService : IAsyncDisposable
{
    private Process? _process;
    public bool IsRunning { get; private set; }
    public SftpServerOptions? Options { get; private set; }

    public async Task StartAsync(SftpServerOptions options, CancellationToken ct = default)
    {
        options.Validate();
        if (!options.Enabled) { IsRunning = false; Options = options; return; }
        await StopAsync(ct).ConfigureAwait(false);
        var runtime = new SftpRuntimeDiagnostics().Inspect(options);
        if (!runtime.CanStart) throw new InvalidOperationException(runtime.Summary);
        var password = await new SftpPasswordStore().LoadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("未找到 SFTP 密码；请在 SFTP 设置中重新设置密码。");
        var key = options.HostKeyPath ?? new SftpHostKeyStore().GetKeyReference().Path;
        var cache = Path.Combine(AppDataPaths.Root, "sftp", "cache"); Directory.CreateDirectory(cache);
        var start = new ProcessStartInfo(runtime.RcloneExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("serve"); start.ArgumentList.Add("sftp"); start.ArgumentList.Add(":local:" + Path.GetFullPath(options.RootPath!));
        start.ArgumentList.Add("--addr"); start.ArgumentList.Add($"{options.ListenAddress}:{options.Port}");
        start.ArgumentList.Add("--user"); start.ArgumentList.Add(options.UserName!);
        start.ArgumentList.Add("--key"); start.ArgumentList.Add(key);
        start.ArgumentList.Add("--vfs-cache-mode"); start.ArgumentList.Add("writes");
        start.ArgumentList.Add("--cache-dir"); start.ArgumentList.Add(cache);
        start.ArgumentList.Add("--vfs-cache-max-size"); start.ArgumentList.Add(options.CacheMaxSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--vfs-cache-max-age"); start.ArgumentList.Add("1h");
        start.ArgumentList.Add("--vfs-write-back"); start.ArgumentList.Add("5s");
        start.Environment["RCLONE_PASS"] = password;
        _process = Process.Start(start) ?? throw new InvalidOperationException("无法启动内置 rclone SFTP 服务。");
        if (!await WaitUntilListeningAsync(options, _process, ct).ConfigureAwait(false))
        {
            var error = await _process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("内置 rclone SFTP 服务未能开始监听。" + error);
        }
        Options = options; IsRunning = true;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        IsRunning = false; var process = Interlocked.Exchange(ref _process, null); if (process is null) return;
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(ct).ConfigureAwait(false); } }
        finally { process.Dispose(); }
    }
    public ValueTask DisposeAsync() => new(StopAsync());
    private static async Task<bool> WaitUntilListeningAsync(SftpServerOptions options, Process process, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40 && !process.HasExited; attempt++)
        {
            try { using var tcp = new TcpClient(); await tcp.ConnectAsync(options.ListenAddress, options.Port, ct).ConfigureAwait(false); return true; }
            catch (SocketException) { await Task.Delay(50, ct).ConfigureAwait(false); }
        }
        return false;
    }
}
