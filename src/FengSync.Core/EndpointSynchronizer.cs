namespace FengSync.Core;

/// <summary>Common compare/execute workflow used by local, SFTP and Google Drive endpoint pairs.</summary>
public sealed class EndpointSynchronizer
{
    public async Task<SyncPlan> CompareAsync(IEndpoint left, IEndpoint right, SyncMode mode, IEnumerable<BaselineEntry>? baseline = null, SyncFilter? filter = null, CancellationToken ct = default)
    {
        var scans = await Task.WhenAll(left.ScanAsync(ct), right.ScanAsync(ct));
        return new ModePlanner().Build(mode, scans[0], scans[1], baseline, filter, left.Capabilities, right.Capabilities);
    }
    public async Task<SyncPlan> SynchronizeAsync(IEndpoint left, IEndpoint right, SyncMode mode, IEnumerable<BaselineEntry>? baseline = null,
        SyncFilter? filter = null, VersioningPolicy? versioning = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var plan = await CompareAsync(left, right, mode, baseline, filter, ct);
        var snapshot = await PlanSnapshot.CaptureAsync(plan, left, right, ct);
        var result = await new Execution.SyncExecutorV2().ExecuteAsync(snapshot, left, right,
            progress is null ? null : new Progress<TransferProgress>(x => { if (x.Stage == TransferStage.Committed) progress.Report(x.Path); }),
            ct, versioning: versioning);
        if (!result.Succeeded) throw new IOException($"同步有 {result.FailedOperations} 个操作失败。");
        return plan;
    }
}
