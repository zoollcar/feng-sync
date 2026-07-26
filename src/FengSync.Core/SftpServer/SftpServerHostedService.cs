using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FengSync.Core.SftpServer;

/// <summary>
/// Hosts the audited <c>ssh2</c> SSH/SFTP implementation in a child process.  SSH is deliberately
/// not reimplemented here: this class is responsible only for lifecycle and passing the least
/// sensitive configuration possible (PBKDF2 verifiers, never passwords) to the protocol host.
/// Deployments must bundle Node and the pinned ssh2 modules, or configure their locations explicitly.
/// </summary>
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
        var script = runtime.ProtocolHostPath;
        var node = runtime.NodeExecutable;
        var modulePath = runtime.ModuleDirectory;
        var hostKey = options.HostKeyPath ?? new SftpHostKeyStore().GetKeyReference().Path;
        var auditPath = Path.Combine(AppDataPaths.Root, "sftp", "audit.jsonl");
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ProtocolConfiguration(options, hostKey, auditPath))));
        var start = new ProcessStartInfo(node, $"\"{script}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        start.Environment["FENGSYNC_SFTP_CONFIG"] = payload;
        start.Environment["NODE_PATH"] = modulePath;
        _process = Process.Start(start) ?? throw new InvalidOperationException("无法启动内置 SFTP 协议主机。");
        var ready = await WaitUntilListeningAsync(options, _process, ct).ConfigureAwait(false);
        if (!ready)
        {
            var error = await _process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"内置 SFTP 协议主机未能开始监听。{error}");
        }
        Options = options;
        IsRunning = true;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        IsRunning = false;
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close(); // EOF asks the protocol host to stop accepting new SSH sessions.
                await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
        }
        catch when (!process.HasExited)
        {
            // Cancellation and failed graceful shutdowns must not orphan the
            // Node protocol host (notably when a test runner aborts a fixture).
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally { process.Dispose(); }
    }
    public ValueTask DisposeAsync() => new(StopAsync());

    private static async Task<bool> WaitUntilListeningAsync(SftpServerOptions options, Process process, CancellationToken ct)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        for (var attempt = 0; attempt < 40 && !process.HasExited; attempt++)
        {
            try { await tcp.ConnectAsync(options.ListenAddress, options.Port, ct).ConfigureAwait(false); return true; }
            catch (SocketException) { await Task.Delay(50, ct).ConfigureAwait(false); }
        }
        return false;
    }

    private sealed record ProtocolConfiguration(SftpServerOptions Options, string HostKeyPath, string AuditLogPath);
}
