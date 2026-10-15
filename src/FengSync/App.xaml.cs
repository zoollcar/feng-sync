using FengSync.Core.Configuration;
using FengSync.Core.Mount;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Localization;
using FengSync.Core.SftpServer;
using FengSync.Services;
using FengSync.Views;
using System.IO;

namespace FengSync;
public partial class App : System.Windows.Application, IAppServiceHost
{
    private readonly RcloneLifecycleHost _rcloneLifecycle = new();
    private readonly SftpServerHostedService _sftpService;
    private readonly RcloneMountService _mountService;
    private readonly CloudTransferQueue _cloudTransfers = new(history: new CloudTransferHistoryRepository());
    /// <summary>
    /// The LAN transfer window, while one is open. Held here rather than inside <see cref="IAppServiceHost"/>
    /// for the same reason the SFTP server is: a fixed listen port is not something two shells can own, and
    /// the DriveHub entry point shows no route to this window at all.
    /// </summary>
    private LanTransferWindow? _lanTransfer;
    private int _shutdownStarted;

    public App()
    {
        // The daemon is created on first use, which is always after this runs, so the proxy setting is
        // read from disk exactly once per process — at the moment rclone can still be told about it.
        _rcloneLifecycle.ProxyOptionsProvider = RcloneProxySettings.LoadAsync;
        _sftpService = new SftpServerHostedService(_rcloneLifecycle);
        // The record store is what makes a mount survive a restart: it remembers what was mounted, and the
        // mount list offers to put it back. Records deliberately outlive a normal exit, because shutting down
        // unmounts everything this process owns.
        _mountService = new RcloneMountService(_rcloneLifecycle, records: new MountRecordStore());
        // Installed here, not in OnStartup: StartupUri windows are created after the constructor, and
        // windows read AppServices.Current while their own constructors run.
        AppServices.Install(this);
    }
    public static App CurrentApp => (App)Current;
    public SftpServerHostedService SftpService => _sftpService;
    public RcloneLifecycleHost RcloneHost => _rcloneLifecycle;
    public IRcloneLifecycleClient RcloneLifecycle => _rcloneLifecycle;
    /// <summary>Application-owned mount service so other windows can observe the same instance.</summary>
    public RcloneMountService MountService => _mountService;
    /// <summary>
    /// Application-owned cloud transfer queue so uploads and downloads keep running (and stay visible
    /// in the standalone transfer window) after the cloud manager window is closed.
    /// </summary>
    public CloudTransferQueue CloudTransfers => _cloudTransfers;

    /// <summary>
    /// Remembers the LAN window that has just opened, so shutdown can await its teardown. One at a time:
    /// <see cref="LanTransferWindow.ShowOrActivate"/> keeps the window a singleton and the listen port free.
    /// </summary>
    internal void LatchLanTransfer(LanTransferWindow window) => _lanTransfer = window;

    internal void ReleaseLanTransfer(LanTransferWindow window)
    {
        if (ReferenceEquals(_lanTransfer, window)) _lanTransfer = null;
    }

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
        // Before base.OnStartup, which is what creates the StartupUri window: a window that renders its
        // first frame in one language and the rest of itself in another is a worse bug than a few
        // milliseconds of startup, and the file being read is one small local JSON.
        AppLanguageStartup.RegisterWindowLanguage();
        AppLanguageStartup.ApplySavedLanguage();
        base.OnStartup(e);
        UpdatedFromVersion = e.Args.SkipWhile(x => x != "--updated-from").Skip(1).FirstOrDefault();
        UpdateTaskDirectory = e.Args.SkipWhile(x => x != "--update-task").Skip(1).FirstOrDefault();
        _ = StartConfiguredSftpAsync();
        _ = RestoreAutoMountsAsync();
    }

    public string? UpdatedFromVersion { get; private set; }
    public string? UpdateTaskDirectory { get; private set; }

    /// <summary>Stops application-owned services, then terminates the WPF dispatcher.</summary>
    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
        // The LAN listener goes down first: it is the one thing here a machine on another desk can still be
        // writing to, and a shutdown that leaves it bound answers requests with a half-stopped process behind them.
        try
        {
            var lan = _lanTransfer;
            if (lan is not null) await lan.ShutdownAsync();
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to stop the LAN transfer listener during shutdown: " + ex); }
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
        finally
        {
            try { await _rcloneLifecycle.DisposeAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to stop rclone lifecycle host during shutdown: " + ex); }
            Shutdown();
        }
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

    /// <summary>
    /// Puts back the mounts the user marked for automatic restore, and only those. Everything else is offered
    /// by the mount list instead: mounting the lot at startup would slow it down, and a mount that fails here
    /// is a failure nobody can see. Failures are traced rather than shown — a modal dialog before the main
    /// window has drawn is not a place anyone can act on one.
    /// </summary>
    private async Task RestoreAutoMountsAsync()
    {
        try
        {
            var store = _mountService.Records;
            if (store is null) return;
            var records = await store.LoadAsync();
            if (records.Count == 0) return;
            var live = await _mountService.ScanAsync();
            var plan = MountRecovery.Plan(records, live.Select(mount => mount.MountPoint).ToList(), WinFspDetector.Detect().Installed);
            var outcome = await MountRestore.RunAsync(_mountService, plan, automaticOnly: true);
            if (outcome.Restored > 0 || outcome.Failed > 0)
                System.Diagnostics.Trace.TraceInformation("Restored {0} auto-mount(s), {1} failed.", outcome.Restored, outcome.Failed);
            foreach (var failure in outcome.Failures)
                System.Diagnostics.Trace.TraceWarning("Auto-mount failed: {0}", failure);
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to restore remembered mounts: " + ex); }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        // ShutdownAsync normally performs this work.  Keep this synchronous final
        // guard for exits initiated outside MainWindow (for example, a system close).
        try
        {
            if (Volatile.Read(ref _shutdownStarted) == 0)
            {
                try { _lanTransfer?.ShutdownAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to stop the LAN transfer listener during final exit: " + ex); }
                try { _mountService.StopAllFengSyncMountsAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to stop Feng Sync mounts during final exit: " + ex); }
                try { _sftpService.StopAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to stop SFTP server during final exit: " + ex); }
                try { _rcloneLifecycle.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceError("Unable to stop rclone lifecycle host during final exit: " + ex); }
            }
        }
        finally { base.OnExit(e); }
    }
}
