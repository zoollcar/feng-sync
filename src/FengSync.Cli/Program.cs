using System.Text.Json;
using FengSync.Core;
using FengSync.Core.Automation;

return await CliProgram.RunAsync(args);

public static class CliProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("compare" or "run")) return Write(new { error = "Usage: fengsync compare|run --profile <id|file> [--non-interactive] [--json-log]" }, AutomationExitCode.ConfigurationError);
        var profileArg = ReadOption(args, "--profile");
        if (string.IsNullOrWhiteSpace(profileArg)) return Write(new { error = "--profile is required." }, AutomationExitCode.ConfigurationError);
        try
        {
            var profile = await LoadProfileAsync(profileArg);
            if (args[0] == "compare")
            {
                var comparison = await new ProfileRunner().CompareAsync(profile);
                // An empty plan is an idempotent success, not a conflict. SyncPlan.CanExecute
                // is intentionally false for empty plans because there is nothing to execute.
                var code = comparison.Planned == 0 || comparison.CanExecute ? AutomationExitCode.Success : AutomationExitCode.Conflict;
                return Write(new { profileId = comparison.ProfileId, planned = comparison.Planned, selected = comparison.Selected, canExecute = comparison.CanExecute, exitCode = code.ToString() }, code);
            }
            var result = await new AutomationRunner().RunAsync(profile);
            return Write(new { profileId = profile.Id, exitCode = result.ExitCode.ToString(), result = result.ProfileResult, error = result.Error }, result.ExitCode);
        }
        catch (Exception ex) { return Write(new { error = ex.Message }, AutomationExitCode.ConfigurationError); }
    }

    private static async Task<SyncProfile> LoadProfileAsync(string idOrFile)
    {
        if (File.Exists(idOrFile))
        {
            await using var stream = File.OpenRead(idOrFile);
            var profile = await JsonSerializer.DeserializeAsync<SyncProfile>(stream);
            return profile ?? throw new InvalidOperationException("Profile file is empty or invalid.");
        }
        return (await new ProfileStore().LoadAsync()).SingleOrDefault(x => x.Id == idOrFile)
            ?? throw new InvalidOperationException("Profile was not found.");
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
            if (args[index] == name) return args[index + 1];
        return null;
    }

    private static int Write(object value, AutomationExitCode code)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(value));
        return (int)code;
    }
}
