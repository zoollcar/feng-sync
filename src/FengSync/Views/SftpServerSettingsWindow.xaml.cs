using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using FengSync.Core.Rclone.Diagnostics;
using FengSync.Core.SftpServer;
using FengSync.Localization;

namespace FengSync.Views;

public partial class SftpServerSettingsWindow : Window
{
    private readonly SftpServerSettingsStore _store = new();
    private readonly SftpPasswordStore _passwords = new();
    private SftpServerOptions _loaded = new();
    private bool _operationInProgress;

    public SftpServerSettingsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _loaded = await _store.LoadAsync();
            EnabledBox.IsChecked = _loaded.Enabled; AutoStartBox.IsChecked = _loaded.StartWithApplication;
            AddressBox.Text = _loaded.ListenAddress; PortBox.Text = _loaded.Port.ToString(); RootPathBox.Text = _loaded.RootPath ?? ""; UserNameBox.Text = _loaded.UserName ?? "";
            CacheSizeBox.Text = (_loaded.CacheMaxSizeBytes / (1024L * 1024 * 1024)).ToString();
            PasswordStateText.Text = _passwords.HasPassword ? Tr.F("Sftp.PasswordSet") : Tr.F("Sftp.PasswordUnset");
            FingerprintText.Text = Tr.Format("Sftp.HostFingerprint", new SftpHostKeyStore().GetKeyReference().Fingerprint);
            StatusText.Text = _store.LegacyConfigurationRemoved ? Tr.F("Sftp.LegacyRemoved") : App.CurrentApp.SftpService.IsRunning ? Tr.F("Sftp.Running") : Tr.F("Sftp.NotRunning");
        }
        catch (SftpServerOperationException ex)
        {
            StatusText.Text = Tr.Format("Sftp.OperationError", ex.Message, ex.SuggestedAction, ex.CorrelationId);
            System.Diagnostics.Trace.TraceError($"SFTP {ex.Operation} failed; code={ex.TechnicalCode}; correlationId={ex.CorrelationId}");
        }
        catch (RcloneException ex)
        {
            StatusText.Text = Tr.Format("Sftp.OperationError", ex.Failure.UserMessage, ex.Failure.SuggestedAction, ex.Failure.CorrelationId);
            System.Diagnostics.Trace.TraceError($"rclone {ex.Failure.Operation} failed; category={ex.Failure.Category}; correlationId={ex.Failure.CorrelationId}; detail={ex.Failure.TechnicalMessage}");
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private SftpServerOptions Collect(bool enabled)
    {
        if (!int.TryParse(PortBox.Text, out var port)) throw new InvalidOperationException(Tr.F("Sftp.PortNotInteger"));
        if (!long.TryParse(CacheSizeBox.Text, out var cacheGiB)) throw new InvalidOperationException(Tr.F("Sftp.CacheSizeNotInteger"));
        return new(enabled, AutoStartBox.IsChecked == true, AddressBox.Text.Trim(), port, EmptyToNull(RootPathBox.Text), EmptyToNull(UserNameBox.Text), _loaded.HostKeyPath, checked(cacheGiB * 1024L * 1024 * 1024), _passwords.HasPassword);
    }
    private async Task SaveAsync(bool enabled) { var next = Collect(enabled); next.Validate(); await _store.SaveAsync(next); _loaded = next; }
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunBusyAsync((Button)sender, Tr.F("Sftp.Saving"), async () => { await SaveAsync(EnabledBox.IsChecked == true); StatusText.Text = Tr.F("Sftp.Saved"); });
    private async void Start_Click(object sender, RoutedEventArgs e) => await RunBusyAsync((Button)sender, Tr.F("Sftp.Starting"), async () => { await SaveAsync(true); await App.CurrentApp.SftpService.StartAsync(_loaded); StatusText.Text = Tr.Format("Sftp.Listening", _loaded.ListenAddress, _loaded.Port); });
    private async void Stop_Click(object sender, RoutedEventArgs e) { while (_operationInProgress) await Task.Delay(25); await RunBusyAsync((Button)sender, Tr.F("Sftp.Stopping"), async () => { await App.CurrentApp.SftpService.StopAsync(); StatusText.Text = Tr.F("Sftp.Stopped"); }); }
    private void Diagnose_Click(object sender, RoutedEventArgs e) { try { StatusText.Text = new SftpRuntimeDiagnostics().Inspect(Collect(false)).Summary; } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private async void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PasswordDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { await _passwords.SaveAsync(dialog.Password); PasswordStateText.Text = Tr.F("Sftp.PasswordSet"); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async Task RunBusyAsync(Button button, string busyText, Func<Task> action)
    {
        if (_operationInProgress) return; _operationInProgress = true; var original = button.Content;
        try { button.IsEnabled = false; button.Content = busyText; StatusText.Text = busyText; await action(); }
        catch (SftpServerOperationException ex)
        {
            StatusText.Text = Tr.Format("Sftp.OperationError", ex.Message, ex.SuggestedAction, ex.CorrelationId);
            System.Diagnostics.Trace.TraceError($"SFTP {ex.Operation} failed; code={ex.TechnicalCode}; correlationId={ex.CorrelationId}");
        }
        catch (RcloneException ex)
        {
            StatusText.Text = Tr.Format("Sftp.OperationError", ex.Failure.UserMessage, ex.Failure.SuggestedAction, ex.Failure.CorrelationId);
            System.Diagnostics.Trace.TraceError($"rclone {ex.Failure.Operation} failed; category={ex.Failure.Category}; correlationId={ex.Failure.CorrelationId}; detail={ex.Failure.TechnicalMessage}");
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { button.IsEnabled = true; button.Content = original; _operationInProgress = false; }
    }
    private static string? EmptyToNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private sealed class PasswordDialog : Window
    {
        private readonly PasswordBox _password = new(); public string Password => _password.Password;
        public PasswordDialog()
        {
            Title = Tr.F("Sftp.PasswordDialogTitle"); Width = 360; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            AutomationProperties.SetAutomationId(this, "SftpPasswordDialog"); AutomationProperties.SetName(this, Title);
            AutomationProperties.SetAutomationId(_password, "SftpPassword"); AutomationProperties.SetName(_password, Tr.F("Sftp.PasswordName"));
            var save = new Button { Content = Tr.F("Sftp.Save"), IsDefault = true };
            AutomationProperties.SetAutomationId(save, "SaveSftpPassword"); AutomationProperties.SetName(save, Tr.F("Sftp.SavePasswordName"));
            save.Click += (_, _) => { if (string.IsNullOrEmpty(_password.Password)) { MessageBox.Show(Tr.F("Sftp.PasswordEmpty"), Title); return; } DialogResult = true; };
            Content = new StackPanel { Margin = new Thickness(18), Children = { new TextBlock { Text = Tr.F("Sftp.PasswordLabel") }, _password, save } };
        }
    }
}
