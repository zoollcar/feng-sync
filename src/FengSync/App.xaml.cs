using FengSync.Core.SftpServer;

namespace FengSync;
public partial class App : System.Windows.Application
{
    private readonly SftpServerHostedService _sftpService = new();
    private int _shutdownStarted;
    public static App CurrentApp => (App)Current;
    public SftpServerHostedService SftpService => _sftpService;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // Keep WPF rendering compatible with remote sessions and screen capture.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // MainWindow coordinates asynchronous cleanup before it explicitly shuts the
        // application down.  Never let WPF tear down the dispatcher first.
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        _ = StartConfiguredSftpAsync();
    }

    /// <summary>Stops application-owned services, then terminates the WPF dispatcher.</summary>
    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
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
        try { _sftpService.StopAsync().GetAwaiter().GetResult(); }
        finally { base.OnExit(e); }
    }
}
