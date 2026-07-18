using System.Windows;
using System.Windows.Controls;
using System.IO;
using FengSync.Core;
using FengSync.Core.Automation;

namespace FengSync.Views;

/// <summary>Minimal UI over the testable scheduler service. It never collects or exports credentials.</summary>
public partial class ScheduleWizard : Window
{
    private readonly SyncProfile _profile;
    private readonly WindowsTaskSchedulerService _scheduler;
    private readonly string _cliPath;

    public ScheduleWizard(SyncProfile profile, WindowsTaskSchedulerService? scheduler = null, string? cliPath = null)
    {
        InitializeComponent();
        _profile = profile;
        _scheduler = scheduler ?? new WindowsTaskSchedulerService();
        _cliPath = cliPath ?? Path.Combine(AppContext.BaseDirectory, "FengSync.Cli.exe");
        TaskNameBox.Text = "FengSync-" + profile.Id;
        ProfileText.Text = $"为 Profile “{profile.Name}”创建 Windows 计划任务。";
    }

    private string TaskName => TaskNameBox.Text.Trim();
    private string Schedule => (ScheduleBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "DAILY";

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_cliPath)) { ResultText.Text = "找不到 FengSync.Cli.exe；请重新安装完整应用。"; return; }
        try
        {
            await _scheduler.CreateOrReplaceAsync(new ScheduledProfileTask(TaskName, _profile.Id, _cliPath, Schedule));
            ResultText.Text = "计划任务已创建或更新。";
        }
        catch (Exception ex) { ResultText.Text = ex.Message; }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        try { await _scheduler.TestRunAsync(TaskName); ResultText.Text = "已请求测试运行；请在运行历史中查看结果。"; }
        catch (Exception ex) { ResultText.Text = ex.Message; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        try { await _scheduler.DeleteAsync(TaskName); ResultText.Text = "计划任务已删除。"; }
        catch (Exception ex) { ResultText.Text = ex.Message; }
    }
}
