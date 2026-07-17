namespace FengSync;
public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // Keep WPF rendering compatible with remote sessions and screen capture.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
    }
}
