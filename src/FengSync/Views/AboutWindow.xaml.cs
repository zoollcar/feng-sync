using System.Diagnostics;
using System.Windows;
using FengSync.Localization;

namespace FengSync.Views;
public partial class AboutWindow : Window
{
    private readonly Func<Task>? _checkUpdates;
    public AboutWindow(string version, Func<Task>? checkUpdates = null) { InitializeComponent(); VersionText.Text = Tr.Format("About.Version", version); _checkUpdates = checkUpdates; }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) { if (_checkUpdates is not null) await _checkUpdates(); }
    private void Repository_Click(object sender, RoutedEventArgs e) => Open("https://github.com/zoollcar/feng-sync");
    private void Licenses_Click(object sender, RoutedEventArgs e) => MessageBox.Show(Tr.F("About.LicensesNotice"), Tr.F("About.Licenses"), MessageBoxButton.OK, MessageBoxImage.Information);
    private void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(Tr.Format("About.CannotOpen", ex.Message), "Feng Sync", MessageBoxButton.OK, MessageBoxImage.Warning); } }
}
