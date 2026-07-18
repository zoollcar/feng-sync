using FengSync.Core.SftpServer;

namespace FengSync;
public partial class App : System.Windows.Application
{
    private readonly SftpServerHostedService _sftpService = new();
    public static App CurrentApp => (App)Current;
    public SftpServerHostedService SftpService => _sftpService;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // Keep WPF rendering compatible with remote sessions and screen capture.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        _ = StartConfiguredSftpAsync();
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

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        try { await _sftpService.StopAsync(); }
        finally { base.OnExit(e); }
    }
}
