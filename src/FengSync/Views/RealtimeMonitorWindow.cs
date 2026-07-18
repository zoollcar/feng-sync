using System.Windows;
using System.Windows.Controls;
using System.IO;
using FengSync.Core;
using FengSync.Core.Automation;

namespace FengSync.Views;

/// <summary>Visible lifecycle control for local real-time profiles; the coordinator prevents watcher feedback loops.</summary>
public sealed class RealtimeMonitorWindow : Window
{
    private readonly SyncProfile _profile;
    private readonly Func<SyncProfile, CancellationToken, Task> _run;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private RealtimeMonitor? _monitor;
    private RealtimeRunCoordinator? _coordinator;

    public RealtimeMonitorWindow(SyncProfile profile, Func<SyncProfile, CancellationToken, Task> run)
    {
        _profile = profile; _run = run; Title = "实时同步监控"; Width = 500; Height = 230; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var start = new Button { Content = "开始监控" };
        var stop = new Button { Content = "停止", Margin = new Thickness(8, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(start, "StartRealtimeMonitor");
        System.Windows.Automation.AutomationProperties.SetAutomationId(stop, "StopRealtimeMonitor");
        start.Click += (_, _) => Start(); stop.Click += async (_, _) => await StopAsync();
        Content = new StackPanel { Margin = new Thickness(18), Children = { new TextBlock { Text = $"Profile：{profile.Name}\n仅监控本地端点。同步自身写入在冷却期内会被抑制；运行中变更会合并为一次后续运行。", TextWrapping = TextWrapping.Wrap }, _status, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), Children = { start, stop } } } };
        Closed += async (_, _) => await StopAsync();
    }

    private void Start()
    {
        if (_monitor is not null) return;
        if (!Directory.Exists(_profile.LeftPath) || !Directory.Exists(_profile.RightPath)) { _status.Text = "实时监控仅支持两个已存在的本地目录。"; return; }
        _coordinator = new RealtimeRunCoordinator(ct => _run(_profile, ct));
        _coordinator.StatusChanged += message => Dispatcher.InvokeAsync(() => _status.Text = message);
        _monitor = new RealtimeMonitor(_profile.LeftPath, _profile.RightPath, _coordinator.NotifyChanged);
        _status.Text = "正在监控本地变更。";
    }
    private async Task StopAsync()
    {
        _monitor?.Dispose(); _monitor = null;
        if (_coordinator is not null) await _coordinator.DisposeAsync(); _coordinator = null;
        _status.Text = "监控已停止。";
    }
}
