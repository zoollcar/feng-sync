namespace FengSync.Core;

public sealed record ProfileRunResult(string ProfileId, int Planned, int Executed, DateTimeOffset CompletedUtc);

/// <summary>Reusable batch entry point; UI, scheduled tasks and the command line share the same safe workflow.</summary>
public sealed class ProfileRunner
{
    public async Task<ProfileRunResult> RunAsync(SyncProfile profile, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!profile.Enabled) throw new InvalidOperationException("配置档案已禁用。");
        var left = new LocalEndpoint(profile.LeftPath); var right = new LocalEndpoint(profile.RightPath);
        var leftEntries = left.Scan(); var rightEntries = right.Scan();
        var baseline = profile.Mode == SyncMode.TwoWay ? await new BaselineStore().LoadAsync(left, right, ct) : null;
        var plan = new ModePlanner().Build(profile.Mode, leftEntries, rightEntries, baseline, profile.Filter);
        if (!plan.CanExecute && plan.Operations.Any()) throw new InvalidOperationException("批处理遇到未裁决冲突；请先在界面中处理。 ");
        var selected = plan.Operations.Count(x => x.Selected);
        if (selected > 0)
            await new LocalExecutor().ExecuteAsync(plan, left, right, progress, ct, new TaskJournalStore(), profile.MaxConcurrentCopies, profile.VerifyCopies, profile.Versioning);
        if (profile.Mode == SyncMode.TwoWay && selected > 0) await new BaselineStore().CommitAsync(left, right, ct);
        return new(profile.Id, plan.Operations.Count, selected, DateTimeOffset.UtcNow);
    }
}
