namespace FengSync.Core;

/// <summary>Endpoint-neutral executor for local folders, SFTP and Google Drive through rclone RC.</summary>
public sealed class EndpointExecutor
{
    public async Task ExecuteAsync(SyncPlan plan, IEndpoint left, IEndpoint right, IProgress<string>? progress = null, CancellationToken ct = default, VersioningPolicy? versioning = null, int maxConcurrentCopies = 3)
    {
        if (!plan.CanExecute) throw new InvalidOperationException("计划含未裁决的冲突、或未选择任何动作。");
        if (maxConcurrentCopies is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxConcurrentCopies), "并发数必须在 1 到 32 之间。");
        var selected = plan.Operations.Where(x => x.Selected).ToList();
        foreach (var op in selected.Where(x => x.Kind is OperationKind.CreateLeftDirectory or OperationKind.CreateRightDirectory))
            await (op.Kind == OperationKind.CreateLeftDirectory ? left : right).CreateDirectoryAsync(op.Path, ct);
        using (var transferGate = new SemaphoreSlim(maxConcurrentCopies, maxConcurrentCopies))
            await Task.WhenAll(selected.Where(x => x.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft).Select(async op =>
            {
                await transferGate.WaitAsync(ct);
                try
                {
                    var source = op.Kind == OperationKind.CopyLeftToRight ? left : right; var target = op.Kind == OperationKind.CopyLeftToRight ? right : left;
                    var temporary = op.Path + ".fengsync-" + Guid.NewGuid().ToString("N") + ".partial";
                    await CopyAsync(source, target, op.Path, temporary, ct); await target.MoveAsync(temporary, op.Path, ct); progress?.Report(op.Path);
                }
                finally { transferGate.Release(); }
            }));
        foreach (var op in selected.Where(x => x.Kind is OperationKind.DeleteLeft or OperationKind.DeleteRight).OrderByDescending(x => x.Path.Length))
        {
            var target = op.Kind == OperationKind.DeleteLeft ? left : right;
            if (versioning?.Mode == VersioningMode.TimestampedArchive)
            {
                if (string.IsNullOrWhiteSpace(versioning.ArchiveDirectory)) throw new InvalidOperationException("版本保留需要设置归档目录。");
                await target.MoveAsync(op.Path, $"{versioning.ArchiveDirectory.TrimEnd('/')}/{DateTime.UtcNow:yyyyMMdd-HHmmss}/{op.Path}", ct);
            }
            else await target.DeleteAsync(op.Path, false, ct);
        }
    }
    private static async Task CopyAsync(IEndpoint source, IEndpoint target, string path, string temporary, CancellationToken ct)
    {
        if (source is LocalEndpoint localSource && target is LocalEndpoint localTarget) { await localSource.CopyToAsync(path, localTarget, temporary, ct); return; }
        var remote = source as RcloneEndpoint ?? target as RcloneEndpoint ?? throw new NotSupportedException("不支持的端点组合。");
        var sourceFs = source is LocalEndpoint sl ? sl.Root : ((RcloneEndpoint)source).FileSystem;
        var sourcePath = source is LocalEndpoint ? path : ((RcloneEndpoint)source).RemotePath(path);
        var targetFs = target is LocalEndpoint tl ? tl.Root : ((RcloneEndpoint)target).FileSystem;
        var targetPath = target is LocalEndpoint ? temporary : ((RcloneEndpoint)target).RemotePath(temporary);
        await remote.Client.CopyFileAsync(sourceFs, sourcePath, targetFs, targetPath, ct);
    }
}
