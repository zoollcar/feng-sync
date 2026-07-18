namespace FengSync.Core.Automation;

/// <summary>Process exit codes; values are part of the automation contract.</summary>
public enum AutomationExitCode
{
    Success = 0,
    Warning = 1,
    Failure = 2,
    Conflict = 3,
    ConfigurationError = 4,
    Cancelled = 5
}
