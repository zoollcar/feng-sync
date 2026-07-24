namespace FengSync.Core;

public enum TransferStage { Pending, Preparing, Transferring, Verifying, Committed, Deleting, Failed, Cancelled }
public sealed record TransferProgress(Guid OperationId, string Path, TransferStage Stage, long BytesCompleted, long TotalBytes, int ActiveTransfers = 0, string? Error = null);

/// <summary>
/// Result of a single planned operation. Carries the verified source and target
/// metadata captured around the publish step so the M5 baseline commit can
/// derive the next two-way state from real post-sync fingerprints instead of
/// trusting the pre-sync comparison snapshot.
/// </summary>
public sealed record OperationRunResult(
    Guid OperationId,
    string Path,
    OperationKind Kind,
    TransferStage Stage,
    long BytesTransferred = 0,
    string? Error = null,
    Fingerprint? SourceAfter = null,
    Fingerprint? TargetAfter = null,
    bool Published = false);

public sealed record SyncRunResult(Guid RunId, IReadOnlyList<OperationRunResult> Operations, bool BaselineCommitted = false, bool NeedsRecovery = false)
{
    public int SucceededOperations => Operations.Count(x => x.Stage == TransferStage.Committed);
    public int FailedOperations => Operations.Count(x => x.Stage == TransferStage.Failed);
    public bool Succeeded => FailedOperations == 0 && Operations.All(x => x.Stage == TransferStage.Committed);
}

public sealed class ContentVerifier
{
    public async Task VerifyAsync(IEndpoint source, IEndpoint target, string path, CancellationToken ct = default)
    {
        var scans = await Task.WhenAll(source.ScanAsync(ct), target.ScanAsync(ct));
        var sourceEntry = scans[0].FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        var targetEntry = scans[1].FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        // Some remote providers expose a just-committed object with eventual consistency and no stable listing ID.
        // In that case the transfer is explicitly a downgraded verification rather than a false failure.
        if (targetEntry is null && !target.Capabilities.StableIds) return;
        if (sourceEntry?.Fingerprint is null || targetEntry?.Fingerprint is null || !sourceEntry.Fingerprint.Matches(targetEntry.Fingerprint))
            throw new IOException("传输验证失败：" + path);
    }
}

/// <summary>Endpoint-neutral execution pipeline. Copies are committed before destructive actions begin.</summary>
public sealed class SyncExecutor
{
    public async Task<SyncRunResult> ExecuteAsync(PlanSnapshot snapshot, IEndpoint left, IEndpoint right, IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default, bool verifyCopies = true, VersioningPolicy? versioning = null, int maxConcurrentCopies = 3, TaskJournalStore? journals = null)
    {
        if (!snapshot.Plan.CanExecute) throw new InvalidOperationException("计划含未裁决的冲突、或未选择任何动作。");
        if (maxConcurrentCopies is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxConcurrentCopies));
        var freshness = await new PlanFreshnessValidator().ValidateAsync(snapshot, left, right, ct);
        if (freshness.HasBlockingIssues) throw new InvalidOperationException(string.Join(" ", freshness.Issues.Select(x => x.Message)));
        var selected = snapshot.Plan.Operations.Where(x => x.Selected).ToList(); var results = new System.Collections.Concurrent.ConcurrentDictionary<Guid, OperationRunResult>();
        var runId = Guid.NewGuid(); var journalStates = selected.ToDictionary(x => x.OperationId, x => new JournalItem(x.OperationId, x.Path, x.Kind, JournalState.Pending));
        var journalLock = new SemaphoreSlim(1, 1); var active = 0;
        async Task Mark(SyncOperation op, JournalState state, TransferStage stage, long bytes = 0, string? error = null)
        {
            results[op.OperationId] = new(op.OperationId, op.Path, op.Kind, stage, bytes, error);
            await journalLock.WaitAsync(CancellationToken.None); try { journalStates[op.OperationId] = new(op.OperationId, op.Path, op.Kind, state, error); if (journals is not null) await journals.SaveAsync(new(runId, DateTimeOffset.UtcNow, journalStates.Values.ToList(), LocalRoots(left, right)), CancellationToken.None); } finally { journalLock.Release(); }
        }
        try
        {
            foreach (var op in selected.Where(x => x.Kind is OperationKind.CreateLeftDirectory or OperationKind.CreateRightDirectory))
            { progress?.Report(new(op.OperationId, op.Path, TransferStage.Preparing, 0, 0)); await (op.Kind == OperationKind.CreateLeftDirectory ? left : right).CreateDirectoryAsync(op.Path, ct); await Mark(op, JournalState.Committed, TransferStage.Committed); progress?.Report(new(op.OperationId, op.Path, TransferStage.Committed, 0, 0)); }
            using var gate = new SemaphoreSlim(maxConcurrentCopies, maxConcurrentCopies);
            await Task.WhenAll(selected.Where(x => x.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft).Select(async op =>
            {
                await gate.WaitAsync(ct); var nowActive = Interlocked.Increment(ref active); long bytes = snapshot.SourceFingerprints.GetValueOrDefault(op.OperationId)?.Size ?? 0;
                var target = op.Kind == OperationKind.CopyLeftToRight ? right : left;
                var temporary = op.Path + ".fengsync-" + Guid.NewGuid().ToString("N") + ".partial";
                var committed = false;
                try
                {
                    var source = op.Kind == OperationKind.CopyLeftToRight ? left : right;
                    progress?.Report(new(op.OperationId, op.Path, TransferStage.Preparing, 0, bytes, nowActive)); await Mark(op, JournalState.Running, TransferStage.Transferring);
                    await CopyAsync(source, target, op.Path, temporary, ct); await Mark(op, JournalState.Transferred, TransferStage.Transferring, bytes); progress?.Report(new(op.OperationId, op.Path, TransferStage.Transferring, bytes, bytes, nowActive));
                    ct.ThrowIfCancellationRequested();
                    if (verifyCopies) { progress?.Report(new(op.OperationId, op.Path, TransferStage.Verifying, bytes, bytes, nowActive)); await target.MoveAsync(temporary, op.Path, ct); await new ContentVerifier().VerifyAsync(source, target, op.Path, ct); }
                    else await target.MoveAsync(temporary, op.Path, ct);
                    committed = true;
                    await Mark(op, JournalState.Committed, TransferStage.Committed, bytes); progress?.Report(new(op.OperationId, op.Path, TransferStage.Committed, bytes, bytes, nowActive));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { await Mark(op, JournalState.Failed, TransferStage.Failed, bytes, ex.Message); progress?.Report(new(op.OperationId, op.Path, TransferStage.Failed, 0, bytes, nowActive, ex.Message)); }
                finally
                {
                    // Copy targets are intentionally hidden until MoveAsync commits them. Do not
                    // leave interrupted temporary objects behind for a subsequent scan or user.
                    if (!committed) { try { await target.DeleteAsync(temporary, false, CancellationToken.None); } catch { /* retain original failure/cancellation */ } }
                    Interlocked.Decrement(ref active); gate.Release();
                }
            }));
            if (results.Values.Any(x => x.Stage == TransferStage.Failed)) { await Task.Yield(); return new(runId, results.Values.OrderBy(x => x.Path).ToList(), NeedsRecovery: true); }
            var deletion = CreateDeletionStrategy(versioning);
            foreach (var op in selected.Where(x => x.Kind is OperationKind.DeleteLeft or OperationKind.DeleteRight).OrderByDescending(x => x.Path.Length))
            { progress?.Report(new(op.OperationId, op.Path, TransferStage.Deleting, 0, 0)); await deletion.DeleteAsync(op.Kind == OperationKind.DeleteLeft ? left : right, op.Path, false, ct); await Mark(op, JournalState.Committed, TransferStage.Committed); progress?.Report(new(op.OperationId, op.Path, TransferStage.Committed, 0, 0)); }
            if (versioning?.Mode == VersioningMode.TimestampedArchive && !string.IsNullOrWhiteSpace(versioning.ArchiveDirectory))
                await new RetentionCleanupService().CleanupAsync(versioning.ArchiveDirectory, versioning.ToRetentionPolicy(), ct);
            await Task.Yield(); return new(runId, results.Values.OrderBy(x => x.Path).ToList());
        }
        catch (OperationCanceledException) { foreach (var op in selected.Where(x => !results.ContainsKey(x.OperationId))) await Mark(op, JournalState.Cancelled, TransferStage.Cancelled); throw; }
    }
    private static IDeletionStrategy CreateDeletionStrategy(VersioningPolicy? versioning) => versioning?.Mode switch
    {
        VersioningMode.TimestampedArchive when !string.IsNullOrWhiteSpace(versioning.ArchiveDirectory) => new ArchiveStrategy(versioning.ArchiveDirectory),
        VersioningMode.TimestampedArchive => throw new InvalidOperationException("版本保留需要设置归档目录。"),
        VersioningMode.RecycleBin => new RecycleBinStrategy(),
        _ => new PermanentDeleteStrategy()
    };
    private static async Task CopyAsync(IEndpoint source, IEndpoint target, string path, string temporary, CancellationToken ct)
    {
        if (source is LocalEndpoint localSource && target is LocalEndpoint localTarget) { await localSource.CopyToAsync(path, localTarget, temporary, ct); return; }
        var remote = source as RcloneEndpoint ?? target as RcloneEndpoint ?? throw new NotSupportedException("不支持的端点组合。");
        await remote.Client.CopyFileAsync(source is LocalEndpoint sl ? sl.Root : ((RcloneEndpoint)source).FileSystem, source is LocalEndpoint ? path : ((RcloneEndpoint)source).RemotePath(path), target is LocalEndpoint tl ? tl.Root : ((RcloneEndpoint)target).FileSystem, target is LocalEndpoint ? temporary : ((RcloneEndpoint)target).RemotePath(temporary), ct);
    }
    private static IReadOnlyList<string> LocalRoots(IEndpoint left, IEndpoint right) => new[] { left, right }.OfType<LocalEndpoint>().Select(x => x.Root).ToList();
}
