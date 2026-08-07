using System.Windows;
using System.Windows.Controls;
using FengSync.Core.Rclone.Diagnostics;
using FengSync.Core.SftpServer;

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
            PasswordStateText.Text = _passwords.HasPassword ? "密码：已设置（受 Windows 账户保护）" : "密码：未设置";
            FingerprintText.Text = "主机指纹：" + new SftpHostKeyStore().GetKeyReference().Fingerprint;
            StatusText.Text = _store.LegacyConfigurationRemoved ? "已清除不兼容的旧版 SFTP 配置，请重新设置服务。" : App.CurrentApp.SftpService.IsRunning ? "SFTP 服务运行中。" : "SFTP 服务当前未运行。";
        }
        catch (SftpServerOperationException ex)
        {
            StatusText.Text = $"{ex.Message} {ex.SuggestedAction}（诊断 ID：{ex.CorrelationId}）";
            System.Diagnostics.Trace.TraceError($"SFTP {ex.Operation} failed; code={ex.TechnicalCode}; correlationId={ex.CorrelationId}");
        }
        catch (RcloneException ex)
        {
            StatusText.Text = $"{ex.Failure.UserMessage} {ex.Failure.SuggestedAction}（诊断 ID：{ex.Failure.CorrelationId}）";
            System.Diagnostics.Trace.TraceError($"rclone {ex.Failure.Operation} failed; category={ex.Failure.Category}; correlationId={ex.Failure.CorrelationId}; detail={ex.Failure.TechnicalMessage}");
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private SftpServerOptions Collect(bool enabled)
    {
        if (!int.TryParse(PortBox.Text, out var port)) throw new InvalidOperationException("端口必须为整数。");
        if (!long.TryParse(CacheSizeBox.Text, out var cacheGiB)) throw new InvalidOperationException("缓存上限必须为整数 GiB。");
        return new(enabled, AutoStartBox.IsChecked == true, AddressBox.Text.Trim(), port, EmptyToNull(RootPathBox.Text), EmptyToNull(UserNameBox.Text), _loaded.HostKeyPath, checked(cacheGiB * 1024L * 1024 * 1024), _passwords.HasPassword);
    }
    private async Task SaveAsync(bool enabled) { var next = Collect(enabled); next.Validate(); await _store.SaveAsync(next); _loaded = next; }
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunBusyAsync((Button)sender, "正在保存…", async () => { await SaveAsync(EnabledBox.IsChecked == true); StatusText.Text = "SFTP 设置已保存。"; });
    private async void Start_Click(object sender, RoutedEventArgs e) => await RunBusyAsync((Button)sender, "正在启动…", async () => { await SaveAsync(true); await App.CurrentApp.SftpService.StartAsync(_loaded); StatusText.Text = "SFTP 服务器正在监听 " + _loaded.ListenAddress + ":" + _loaded.Port; });
    private async void Stop_Click(object sender, RoutedEventArgs e) { while (_operationInProgress) await Task.Delay(25); await RunBusyAsync((Button)sender, "正在停止…", async () => { await App.CurrentApp.SftpService.StopAsync(); StatusText.Text = "SFTP 服务器已停止；端口已释放。"; }); }
    private void Diagnose_Click(object sender, RoutedEventArgs e) { try { StatusText.Text = new SftpRuntimeDiagnostics().Inspect(Collect(false)).Summary; } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private async void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PasswordDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { await _passwords.SaveAsync(dialog.Password); PasswordStateText.Text = "密码：已设置（受 Windows 账户保护）"; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async Task RunBusyAsync(Button button, string busyText, Func<Task> action)
    {
        if (_operationInProgress) return; _operationInProgress = true; var original = button.Content;
        try { button.IsEnabled = false; button.Content = busyText; StatusText.Text = busyText; await action(); }
        catch (SftpServerOperationException ex)
        {
            StatusText.Text = $"{ex.Message} {ex.SuggestedAction}（诊断 ID：{ex.CorrelationId}）";
            System.Diagnostics.Trace.TraceError($"SFTP {ex.Operation} failed; code={ex.TechnicalCode}; correlationId={ex.CorrelationId}");
        }
        catch (RcloneException ex)
        {
            StatusText.Text = $"{ex.Failure.UserMessage} {ex.Failure.SuggestedAction}（诊断 ID：{ex.Failure.CorrelationId}）";
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
            Title = "设置 SFTP 密码"; Width = 360; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var save = new Button { Content = "保存", IsDefault = true }; save.Click += (_, _) => { if (string.IsNullOrEmpty(_password.Password)) { MessageBox.Show("密码不能为空。", Title); return; } DialogResult = true; };
            Content = new StackPanel { Margin = new Thickness(18), Children = { new TextBlock { Text = "密码" }, _password, save } };
        }
    }
}
