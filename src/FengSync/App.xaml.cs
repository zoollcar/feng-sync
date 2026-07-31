using FengSync.Core.Mount;
using FengSync.Core.SftpServer;
using System.IO;

namespace FengSync;
public partial class App : System.Windows.Application
{
    private readonly SftpServerHostedService _sftpService = new();
    private readonly RcloneMountService _mountService = new();
    private int _shutdownStarted;
    public static App CurrentApp => (App)Current;
    public SftpServerHostedService SftpService => _sftpService;
    /// <summary>Application-owned mount service so other windows can observe the same instance.</summary>
    public RcloneMountService MountService => _mountService;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // Native rendering is the normal path.  Test and RDP troubleshooting can
        // opt into the same software-rendering fallback without changing a user's
        // permanent graphics behavior.
        System.Windows.Media.RenderOptions.ProcessRenderMode =
            Environment.GetEnvironmentVariable("FENGSYNC_FORCE_SOFTWARE_RENDERING") == "1"
                ? System.Windows.Interop.RenderMode.SoftwareOnly
                : System.Windows.Interop.RenderMode.Default;
        // MainWindow coordinates asynchronous cleanup before it explicitly shuts the
        // application down.  Never let WPF tear down the dispatcher first.
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        UpdatedFromVersion = e.Args.SkipWhile(x => x != "--updated-from").Skip(1).FirstOrDefault();
        UpdateTaskDirectory = e.Args.SkipWhile(x => x != "--update-task").Skip(1).FirstOrDefault();
        _ = StartConfiguredSftpAsync();
    }

    public string? UpdatedFromVersion { get; private set; }
    public string? UpdateTaskDirectory { get; private set; }

    /// <summary>Stops application-owned services, then terminates the WPF dispatcher.</summary>
    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
        // Mounts must come down BEFORE the SFTP service: SFTP may be backed by a remote
        // we just mounted, so leaving the SFTP child process alive while we tear down
        // the mount would orphan the underlying connection. Any failures here are
        // recorded but do not block the dispatcher shutdown.
        try
        {
            var result = await _mountService.StopAllFengSyncMountsAsync();
            if (!result.AllStopped)
            {
                var detail = string.Join("; ", result.Failures.Select(f => $"{f.MountPoint}（PID {f.Pid}）：{f.Reason}"));
                System.Diagnostics.Trace.TraceWarning("Feng Sync-owned mounts were not fully stopped during shutdown: " + detail);
            }
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to stop Feng Sync mounts during shutdown: " + ex); }
        try { await _sftpService.StopAsync(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to stop SFTP server during shutdown: " + ex); }
        finally { Shutdown(); }
    }

    private async Task StartConfiguredSftpAsync()
    {
        try
        {
            var options = await new SftpServerSettingsStore().LoadAsync();
            if (options.Enabled && options.StartWithApplication) await _sftpService.StartAsync(options);
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to start configured SFTP server: " + ex); }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        // ShutdownAsync normally performs this work.  Keep this synchronous final
        // guard for exits initiated outside MainWindow (for example, a system close).
        try { _mountService.StopAllFengSyncMountsAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to stop Feng Sync mounts during final exit: " + ex); }
        try { _sftpService.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to stop SFTP server during final exit: " + ex); }
        finally { base.OnExit(e); }
    }
}