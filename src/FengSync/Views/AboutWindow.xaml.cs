using System.Diagnostics;
using System.Windows;

namespace FengSync.Views;
public partial class AboutWindow : Window
{
    private readonly Func<Task>? _checkUpdates;
    public AboutWindow(string version, Func<Task>? checkUpdates = null) { InitializeComponent(); VersionText.Text = "版本 " + version; _checkUpdates = checkUpdates; }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) { if (_checkUpdates is not null) await _checkUpdates(); }
    private void Repository_Click(object sender, RoutedEventArgs e) => Open("https://github.com/zoollcar/feng-sync");
    private void Licenses_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Microsoft.Data.Sqlite / SQLitePCLRaw：MIT License\nRclone：MIT License", "第三方许可", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show("无法打开链接：" + ex.Message, "Feng Sync", MessageBoxButton.OK, MessageBoxImage.Warning); } }
}
