using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using FengSync.Core.SftpServer;

namespace FengSync.Views;

/// <summary>Program-level SFTP configuration and lifecycle surface. Password text exists only while an account is edited.</summary>
public partial class SftpServerSettingsWindow : Window
{
    private readonly SftpServerSettingsStore _store = new();
    private SftpServerOptions _loaded = new();
    private readonly ObservableCollection<SftpAccount> _accounts = [];
    private readonly ObservableCollection<SftpShare> _shares = [];

    public SftpServerSettingsWindow()
    {
        InitializeComponent();
        AccountsList.ItemsSource = _accounts; SharesList.ItemsSource = _shares;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _loaded = await _store.LoadAsync();
            EnabledBox.IsChecked = _loaded.Enabled; AutoStartBox.IsChecked = _loaded.StartWithApplication;
            AddressBox.Text = _loaded.ListenAddress; PortBox.Text = _loaded.Port.ToString(); MaxConnectionsBox.Text = _loaded.MaxConnections.ToString();
            NodePathBox.Text = _loaded.NodeExecutablePath ?? ""; NodeModulesBox.Text = _loaded.NodeModulePath ?? "";
            Replace(_accounts, _loaded.Accounts); Replace(_shares, _loaded.Shares);
            var key = new SftpHostKeyStore().GetKeyReference();
            FingerprintText.Text = "主机指纹：" + key.Fingerprint;
            RefreshStatus();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private SftpServerOptions Collect(bool enabled) => new(enabled, AutoStartBox.IsChecked == true, AddressBox.Text.Trim(), Parse(PortBox, "端口"), Parse(MaxConnectionsBox, "最大连接数"),
        _loaded.IdleTimeout, EmptyToNull(NodePathBox.Text), EmptyToNull(NodeModulesBox.Text), _loaded.HostKeyPath, _accounts.ToList(), _shares.ToList());

    private async Task SaveAsync(bool enabled)
    {
        var next = Collect(enabled); next.Validate();
        if (enabled)
        {
            var runtime = new SftpRuntimeDiagnostics().Inspect(next);
            if (!runtime.CanStart) throw new InvalidOperationException(runtime.Summary);
        }
        await _store.SaveAsync(next); _loaded = next; RefreshStatus();
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try { await SaveAsync(EnabledBox.IsChecked == true); StatusText.Text = "SFTP 设置已保存。"; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try { await SaveAsync(true); await App.CurrentApp.SftpService.StartAsync(_loaded); StatusText.Text = "SFTP 服务器正在监听 " + _loaded.ListenAddress + ":" + _loaded.Port; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await App.CurrentApp.SftpService.StopAsync(); StatusText.Text = "SFTP 服务器已停止；端口已释放。";
    }
    private void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        try { StatusText.Text = new SftpRuntimeDiagnostics().Inspect(Collect(false)).Summary; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void RefreshStatus() => StatusText.Text = App.CurrentApp.SftpService.IsRunning ? "SFTP 服务运行中。" : "SFTP 服务当前未运行。";

    private void NewAccount_Click(object sender, RoutedEventArgs e) => EditAccount(null);
    private void EditAccount_Click(object sender, RoutedEventArgs e) => EditAccount(AccountsList.SelectedItem as SftpAccount);
    private void EditAccount(SftpAccount? current)
    {
        var dialog = new AccountDialog(current) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Value is null) return;
        if (current is not null) _accounts.Remove(current); _accounts.Add(dialog.Value);
    }
    private void RemoveAccount_Click(object sender, RoutedEventArgs e) { if (AccountsList.SelectedItem is SftpAccount account) _accounts.Remove(account); }
    private void NewShare_Click(object sender, RoutedEventArgs e) => EditShare(null);
    private void EditShare_Click(object sender, RoutedEventArgs e) => EditShare(SharesList.SelectedItem as SftpShare);
    private void EditShare(SftpShare? current)
    {
        var dialog = new ShareDialog(current) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Value is null) return;
        if (current is not null) _shares.Remove(current); _shares.Add(dialog.Value);
    }
    private void RemoveShare_Click(object sender, RoutedEventArgs e) { if (SharesList.SelectedItem is SftpShare share) _shares.Remove(share); }

    private static int Parse(TextBox source, string name) => int.TryParse(source.Text, out var value) ? value : throw new InvalidOperationException(name + "必须为整数。");
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T>? source) { target.Clear(); foreach (var item in source ?? []) target.Add(item); }

    private sealed class AccountDialog : Window
    {
        private readonly TextBox _name = new(); private readonly PasswordBox _password = new(); private readonly TextBox _shares = new(); private readonly CheckBox _enabled = new() { Content = "启用账号", IsChecked = true };
        public SftpAccount? Value { get; private set; }
        public AccountDialog(SftpAccount? current)
        {
            Title = current is null ? "新建 SFTP 账号" : "编辑 SFTP 账号"; Width = 360; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _name.Text = current?.UserName ?? ""; _enabled.IsChecked = current?.Enabled ?? true; _shares.Text = current?.AllowedShares is { Count: > 0 } grants ? string.Join(", ", grants) : "";
            var save = new Button { Content = "保存", IsDefault = true }; save.Click += (_, _) => { try { var allowed = _shares.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); Value = string.IsNullOrWhiteSpace(_password.Password) && current is not null ? current with { UserName = _name.Text.Trim(), Enabled = _enabled.IsChecked == true, AllowedShares = allowed } : SftpAccount.CreatePasswordAccount(_name.Text.Trim(), _password.Password) with { Enabled = _enabled.IsChecked == true, AllowedShares = allowed }; DialogResult = true; } catch (Exception ex) { MessageBox.Show(ex.Message); } };
            Content = Form(("用户名", (Control)_name), ("密码（编辑时留空不变）", _password), ("允许的共享目录（逗号分隔；留空=全部）", _shares), ("", _enabled), ("", save));
        }
    }
    private sealed class ShareDialog : Window
    {
        private readonly TextBox _name = new(); private readonly TextBox _path = new(); private readonly ComboBox _permission = new() { ItemsSource = Enum.GetValues<SftpPermission>() };
        public SftpShare? Value { get; private set; }
        public ShareDialog(SftpShare? current)
        {
            Title = current is null ? "新建共享目录" : "编辑共享目录"; Width = 460; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _name.Text = current?.VirtualName ?? ""; _path.Text = current?.PhysicalPath ?? ""; _permission.SelectedItem = current?.Permission ?? SftpPermission.ReadOnly;
            var save = new Button { Content = "保存", IsDefault = true }; save.Click += (_, _) => { Value = new SftpShare(_name.Text.Trim(), _path.Text.Trim(), (SftpPermission)_permission.SelectedItem); DialogResult = true; };
            Content = Form(("虚拟名称", _name), ("物理目录", _path), ("权限", _permission), ("", save));
        }
    }
    private static StackPanel Form(params (string Label, Control Control)[] fields)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        foreach (var (label, control) in fields) { if (!string.IsNullOrEmpty(label)) panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) }); panel.Children.Add(control); }
        return panel;
    }
}
