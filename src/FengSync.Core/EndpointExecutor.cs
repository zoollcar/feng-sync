namespace FengSync.Core;

/// <summary>Endpoint-neutral executor for local folders, SFTP and Google Drive through rclone RC.</summary>
public sealed class EndpointExecutor
{
    public async Task ExecuteAsync(SyncPlan plan, IEndpoint left, IEndpoint right, IProgress<string>? progress = null, CancellationToken ct = default, VersioningPolicy? versioning = null, int maxConcurrentCopies = 3)
    {
        var snapshot = await PlanSnapshot.CaptureAsync(plan, left, right, ct);
        var result = await new Execution.SyncExecutorV2().ExecuteAsync(snapshot, left, right,
            progress is null ? null : new Progress<TransferProgress>(x => progress.Report($"{x.Stage}: {x.Path}{(x.Error is null ? "" : " — " + x.Error)}")),
            ct, versioning: versioning, maxConcurrentCopies: maxConcurrentCopies);
        if (!result.Succeeded)
            throw new IOException($"同步有 {result.FailedOperations} 个操作失败：{result.Operations.FirstOrDefault(x => x.Error is not null)?.Error}");
    }
}
