namespace FengSync.Core.Capabilities;

public sealed record ProfileCompatibilityResult(IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings)
{
    public bool CanRun => Blockers.Count == 0;
    public string Summary => string.Join("；", Blockers.Concat(Warnings));
}
