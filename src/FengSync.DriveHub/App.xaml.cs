using System.Diagnostics;
using System.Windows;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Services;
using FengSync.Views;

namespace FengSync.DriveHub;

/// <summary>
/// Standalone DriveHub entry point: the same cloud drive management window the main shell exposes under
/// 工具 → 云盘管理, packaged as its own executable so the release folder offers more than one entry.
/// It installs its own <see cref="IAppServiceHost"/>, so the shared windows never depend on the main
/// shell's <c>App</c> type.
/// </summary>
public partial class DriveHubApp : Application, IAppServiceHost
{
    private readonly RcloneLifecycleHost _rcloneLifecycle = new();
    private readonly RcloneMountService _mountService;
    private readonly CloudTransferQueue _cloudTransfers = new();
    private int _shutdownStarted;

    public DriveHubApp()
    {
        _mountService = new RcloneMountService(_rcloneLifecycle);
        // Before OnStartup: the window reads AppServices.Current while its own constructor runs.
        AppServices.Install(this);
        // The window coordinates asynchronous cleanup before the dispatcher is torn down.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    public RcloneLifecycleHost RcloneHost => _rcloneLifecycle;
    public IRcloneLifecycleClient RcloneLifecycle => _rcloneLifecycle;
    public RcloneMountService MountService => _mountService;
    public CloudTransferQueue CloudTransfers => _cloudTransfers;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new RemoteEndpointManagerWindow { Title = "DriveHub — 云盘管理" };
        MainWindow = window;
        window.Closed += async (_, _) => { await CleanupAsync(); Shutdown(); };
        window.Show();
    }

    /// <summary>
    /// Stops only the mounts served by this process's own daemon — mount discovery is scoped to the daemon
    /// that created the mount, so mounts belonging to a running FengSync shell are reported as external and
    /// are left alone. Leaving them out would strand a mount point whose backing daemon just exited.
    /// </summary>
    private async Task CleanupAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
        try
        {
            var result = await _mountService.StopAllFengSyncMountsAsync();
            if (!result.AllStopped)
            {
                var detail = string.Join("; ", result.Failures.Select(f => $"{f.MountPoint}（PID {f.Pid}）：{f.Reason}"));
                Trace.TraceWarning("DriveHub-owned mounts were not fully stopped during shutdown: " + detail);
            }
        }
        catch (Exception ex) { Trace.TraceError("Unable to stop DriveHub mounts during shutdown: " + ex); }
        try { await _rcloneLifecycle.DisposeAsync(); }
        catch (Exception ex) { Trace.TraceError("Unable to stop the rclone daemon during DriveHub shutdown: " + ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // CleanupAsync normally performs this work. Keep a synchronous guard for exits initiated outside
        // the window (for example a system close or a session logoff).
        try
        {
            if (Volatile.Read(ref _shutdownStarted) == 0)
            {
                try { _mountService.StopAllFengSyncMountsAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { Trace.TraceError("Unable to stop DriveHub mounts during final exit: " + ex); }
                try { _rcloneLifecycle.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception ex) { Trace.TraceError("Unable to stop the rclone daemon during final exit: " + ex); }
            }
        }
        finally { base.OnExit(e); }
    }
}
