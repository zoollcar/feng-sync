namespace FengSync.Core.Automation;

using FengSync.Core.Rclone.Diagnostics;

/// <summary>Shared UI/CLI automation façade. It never changes a profile's safety settings.</summary>
public sealed class AutomationRunner
{
    public async Task<AutomationRunResult> RunAsync(SyncProfile profile, CancellationToken ct = default)
    {
        try
        {
            var result = await new ProfileRunner().RunAsync(profile, ct: ct).ConfigureAwait(false);
            return new(AutomationExitCode.Success, result, null);
        }
        catch (OperationCanceledException) { return new(AutomationExitCode.Cancelled, null, "Operation cancelled."); }
        catch (RcloneException ex)
        {
            RcloneFailureLog.Write(ex.Failure, "automation");
            return new(AutomationExitCode.Failure, null, ex.Failure.UserMessage,
                ex.Failure.Category.ToString(), ex.Failure.Operation, ex.Failure.Retryable,
                ex.Failure.CorrelationId, ex.Failure.SuggestedAction);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("冲突", StringComparison.Ordinal)) { return new(AutomationExitCode.Conflict, null, ex.Message); }
        catch (InvalidOperationException ex) { return new(AutomationExitCode.ConfigurationError, null, ex.Message); }
        catch (Exception ex) { return new(AutomationExitCode.Failure, null, ex.Message); }
    }
}

public sealed record AutomationRunResult(AutomationExitCode ExitCode, ProfileRunResult? ProfileResult, string? Error,
    string? FailureCategory = null, string? FailureOperation = null, bool? Retryable = null,
    string? CorrelationId = null, string? SuggestedAction = null)
{
    public bool Succeeded => ExitCode is AutomationExitCode.Success or AutomationExitCode.Warning;
}
