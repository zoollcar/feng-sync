using System.Diagnostics;

namespace FengSync.Core.Automation;

public sealed record ScheduledProfileTask(string Name, string ProfileId, string CliPath, string Schedule, string? SensitiveValue = null)
{
    private static readonly HashSet<string> SupportedSchedules = new(StringComparer.OrdinalIgnoreCase)
    {
        "MINUTE", "HOURLY", "DAILY", "WEEKLY", "MONTHLY", "ONCE", "ONSTART", "ONLOGON", "ONIDLE", "ONEVENT"
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(ProfileId) || string.IsNullOrWhiteSpace(CliPath))
            throw new InvalidOperationException("计划任务必须包含名称、Profile ID 和 CLI 路径。");
        if (Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || HasCommandDelimiter(Name)) throw new InvalidOperationException("计划任务名称无效。");
        if (HasCommandDelimiter(ProfileId)) throw new InvalidOperationException("Profile ID 无效。");
        if (!SupportedSchedules.Contains(Schedule.Trim())) throw new InvalidOperationException("计划频率无效。");
    }

    private static bool HasCommandDelimiter(string value) => value.IndexOfAny(['"', '\r', '\n']) >= 0;
}

public sealed record ScheduledProcessResult(int ExitCode, string StandardOutput, string StandardError);
public delegate Task<ScheduledProcessResult> ScheduledProcessRunner(string fileName, string arguments, CancellationToken cancellationToken);

/// <summary>Small, testable wrapper around schtasks. Only a profile ID is persisted in the task command line.</summary>
public sealed class WindowsTaskSchedulerService
{
    private readonly ScheduledProcessRunner _run;
    public WindowsTaskSchedulerService(ScheduledProcessRunner? runner = null) => _run = runner ?? RunProcessAsync;

    public async Task CreateOrReplaceAsync(ScheduledProfileTask task, CancellationToken ct = default)
    {
        task.Validate();
        // schtasks stores only an identifier and switches. Credentials remain in the application's credential store.
        var taskCommand = $"\"{task.CliPath}\" run --profile {task.ProfileId} --non-interactive --json-log";
        var arguments = $"/Create /F /TN \"{task.Name}\" /TR \"{taskCommand.Replace("\"", "\\\"")}\" /SC {task.Schedule.Trim().ToUpperInvariant()}";
        var result = await _run("schtasks.exe", arguments, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException("无法创建计划任务：" + result.StandardError.Trim());
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("任务名称不能为空。", nameof(name));
        var result = await _run("schtasks.exe", $"/Delete /F /TN \"{name}\"", ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException("无法删除计划任务：" + result.StandardError.Trim());
    }

    /// <summary>Requests Task Scheduler to execute an existing task now, for use by the schedule wizard's test action.</summary>
    public async Task TestRunAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['"', '\r', '\n']) >= 0) throw new ArgumentException("任务名称无效。", nameof(name));
        var result = await _run("schtasks.exe", $"/Run /TN \"{name}\"", ct).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException("无法测试运行计划任务：" + result.StandardError.Trim());
    }

    private static async Task<ScheduledProcessResult> RunProcessAsync(string file, string arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 schtasks.exe。");
        var output = process.StandardOutput.ReadToEndAsync(ct); var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return new(process.ExitCode, await output, await error);
    }
}
