using System.Windows;
using System.Windows.Controls;
using System.IO;
using FengSync.Core;
using FengSync.Core.Automation;
using FengSync.Localization;

namespace FengSync.Views;

/// <summary>Minimal UI over the testable scheduler service. It never collects or exports credentials.</summary>
public partial class ScheduleWizard : Window
{
    private readonly SyncProfile _profile;
    private readonly WindowsTaskSchedulerService _scheduler;
    private readonly string _cliPath;
    private bool _operationInProgress;

    public ScheduleWizard(SyncProfile profile, WindowsTaskSchedulerService? scheduler = null, string? cliPath = null)
    {
        InitializeComponent();
        _profile = profile;
        _scheduler = scheduler ?? new WindowsTaskSchedulerService();
        _cliPath = cliPath ?? Path.Combine(AppContext.BaseDirectory, "FengSync.Cli.exe");
        TaskNameBox.Text = "FengSync-" + profile.Id;
        ProfileText.Text = Tr.Format("Schedule.ProfileIntro", profile.Name);
    }

    private string TaskName => TaskNameBox.Text.Trim();
    private string Schedule => (ScheduleBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "DAILY";

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_cliPath)) { ResultText.Text = Tr.F("Schedule.CliMissing"); return; }
        await RunBusyAsync((Button)sender, Tr.F("Schedule.Creating"), async () => { await _scheduler.CreateOrReplaceAsync(new ScheduledProfileTask(TaskName, _profile.Id, _cliPath, Schedule)); ResultText.Text = Tr.F("Schedule.Created"); });
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync((Button)sender, Tr.F("Schedule.Requesting"), async () => { await _scheduler.TestRunAsync(TaskName); ResultText.Text = Tr.F("Schedule.TestRequested"); });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync((Button)sender, Tr.F("Schedule.Deleting"), async () => { await _scheduler.DeleteAsync(TaskName); ResultText.Text = Tr.F("Schedule.Deleted"); });
    }
    private async Task RunBusyAsync(Button button, string busyText, Func<Task> action)
    {
        if (_operationInProgress) return;
        _operationInProgress = true;
        var original = button.Content;
        try { button.IsEnabled = false; button.Content = busyText; ResultText.Text = busyText; await action(); }
        catch (Exception ex) { ResultText.Text = ex.Message; }
        finally { button.IsEnabled = true; button.Content = original; _operationInProgress = false; }
    }
}
