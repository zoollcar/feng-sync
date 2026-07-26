using System.Threading.Channels;
using FengSync.Core.Diagnostics;

namespace FengSync.Core.Execution;

/// <summary>
/// M2/M4 executor: freshness is checked via per-path StatAsync and the resource
/// governor arbitrates concurrency between source and target. The copy stage
/// uses bounded channels and a fixed worker pool so a 100,000-operation plan
/// does not materialise 100,000 simultaneous Task instances. The existing
/// <see cref="SyncExecutor"/> remains as the legacy fall-back.
/// </summary>
public sealed class SyncExecutorV2
{
    public const int DefaultChannelCapacity = 256;
    public const long SmallFileThresholdBytes = 8 * 1024 * 1024;

    public async Task<SyncRunResult> ExecuteAsync(PlanSnapshot snapshot, IEndpoint left, IEndpoint right, IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default, bool verifyCopies = true, VersioningPolicy? versioning = null,
        ResourceGovernor? resourceGovernor = null, TaskJournalStore? journals = null, int maxConcurrentCopies = 3, int? channelCapacity = null)
    {
        if (!snapshot.Plan.CanExecute) throw new InvalidOperationException("计划含未裁决的冲突、或未选择任何动作。");
        var freshness = await new PlanFreshnessValidator().ValidateStatAsync(snapshot, left, right, maxConcurrentCopies, ct);
        if (freshness.HasBlockingIssues) throw new InvalidOperationException(string.Join(" ", freshness.Issues.Select(x => x.Message)));

        var selected = snapshot.Plan.Operations.Where(x => x.Selected).ToList();
        var results = new System.Collections.Concurrent.ConcurrentDictionary<Guid, OperationRunResult>();
        var runId = Guid.NewGuid();
        var journalStates = selected.ToDictionary(x => x.OperationId, x => new JournalItem(x.OperationId, x.Path, x.Kind, JournalState.Pending));
        var journalLock = new SemaphoreSlim(1, 1);
        var governor = resourceGovernor ?? new ResourceGovernor();
        var capacity = channelCapacity ?? DefaultChannelCapacity;

        async Task Mark(SyncOperation op, JournalState state, TransferStage stage, long bytes = 0, string? error = null)
        {
            results[op.OperationId] = new(op.OperationId, op.Path, op.Kind, stage, bytes, error,
                results.TryGetValue(op.OperationId, out var prev) ? prev.SourceAfter : null,
                results.TryGetValue(op.OperationId, out var prev2) ? prev2.TargetAfter : null,
                results.TryGetValue(op.OperationId, out var prev3) && prev3.Published);
            await journalLock.WaitAsync(CancellationToken.None);
            try
            {
                journalStates[op.OperationId] = new(op.OperationId, op.Path, op.Kind, state, error);
                if (journals is not null) await journals.SaveAsync(new(runId, DateTimeOffset.UtcNow, journalStates.Values.ToList(), LocalRoots(left, right)), CancellationToken.None);
            }
            finally { journalLock.Release(); }
        }

        async Task Record(SyncOperation op, TransferStage stage, long bytes, Fingerprint? sourceAfter, Fingerprint? targetAfter, bool published, string? error = null)
        {
            results[op.OperationId] = new(op.OperationId, op.Path, op.Kind, stage, bytes, error, sourceAfter, targetAfter, published);
            await journalLock.WaitAsync(CancellationToken.None);
            try
            {
                journalStates[op.OperationId] = new(op.OperationId, op.Path, op.Kind, stage == TransferStage.Committed ? JournalState.Committed : stage == TransferStage.Failed ? JournalState.Failed : JournalState.Running, error);
                if (journals is not null) await journals.SaveAsync(new(runId, DateTimeOffset.UtcNow, journalStates.Values.ToList(), LocalRoots(left, right)), CancellationToken.None);
            }
            finally { journalLock.Release(); }
        }

        try
        {
            var aggregatedOperationIds = new HashSet<Guid>();
            foreach (var group in DirectoryMoveOptimizer.Find(snapshot, left, right))
            {
                var target = group.ExecuteOn == EndpointSide.Left ? left : right;
                var source = group.ChangedOn == EndpointSide.Left ? left : right;
                var groupedOperations = group.FileOperations.Concat(group.StructuralOperations).ToList();
                try
                {
                    // Revalidate every old object and the directory destination
                    // immediately before the single directory-level operation.
                    if (await target.StatAsync(group.ToDirectory, ct) is not null)
                        throw new IOException($"目录移动目标在比较后出现：{group.ToDirectory}");
                    foreach (var operation in group.FileOperations)
                    {
                        var move = operation.Move!;
                        var expected = snapshot.MoveFromFingerprints?.GetValueOrDefault(operation.OperationId);
                        var current = await target.StatAsync(move.FromPath, ct);
                        if (expected is null || current?.Fingerprint is null ||
                            !expected.Matches(current.Fingerprint, MoveTolerance(target)))
                            throw new IOException($"目录移动源在比较后已改变：{move.FromPath}");
                    }

                    foreach (var operation in groupedOperations)
                        await Mark(operation, JournalState.Running, TransferStage.Preparing);
                    await target.MoveDirectoryAsync(group.FromDirectory, group.ToDirectory, ct);

                    foreach (var operation in group.FileOperations)
                    {
                        var move = operation.Move!;
                        var sourceAfter = await RequirePostPublishFingerprintAsync(source, move.ToPath, ct);
                        var targetAfter = await RequirePostPublishFingerprintAsync(target, move.ToPath, ct);
                        await Record(operation, TransferStage.Committed, 0, sourceAfter, targetAfter, true);
                        progress?.Report(new(operation.OperationId, operation.Path, TransferStage.Committed, 0, 0));
                    }
                    foreach (var operation in group.StructuralOperations)
                    {
                        await Record(operation, TransferStage.Committed, 0, null, null, true);
                        progress?.Report(new(operation.OperationId, operation.Path, TransferStage.Committed, 0, 0));
                    }
                    foreach (var operation in groupedOperations)
                        aggregatedOperationIds.Add(operation.OperationId);
                }
                catch (NotSupportedException)
                {
                    // Capability probes can be optimistic. Leave the operations
                    // unconsumed so the existing per-file path executes them.
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    foreach (var operation in groupedOperations)
                    {
                        await Record(operation, TransferStage.Failed, 0, null, null, false, ex.Message);
                        aggregatedOperationIds.Add(operation.OperationId);
                    }
                }
            }

            // Parent directories must exist before descendants, but independent
            // siblings should honor the configured concurrency. Grouping by depth
            // preserves that dependency while avoiding 100 serial remote mkdir
            // requests for a wide tree.
            var directoryOps = selected
                .Where(x => (x.Kind is OperationKind.CreateLeftDirectory or OperationKind.CreateRightDirectory) && !aggregatedOperationIds.Contains(x.OperationId))
                .GroupBy(x => x.Path.Count(c => c == '/' || c == '\\'))
                .OrderBy(x => x.Key);
            foreach (var depthGroup in directoryOps)
            {
                await Parallel.ForEachAsync(depthGroup,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxConcurrentCopies), CancellationToken = ct },
                    async (op, token) =>
                {
                    progress?.Report(new(op.OperationId, op.Path, TransferStage.Preparing, 0, 0));
                    try
                    {
                        await (op.Kind == OperationKind.CreateLeftDirectory ? left : right).CreateDirectoryAsync(op.Path, token);
                        await Record(op, TransferStage.Committed, 0, null, null, true, null);
                        progress?.Report(new(op.OperationId, op.Path, TransferStage.Committed, 0, 0));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await Record(op, TransferStage.Failed, 0, null, null, false, ex.Message);
                        progress?.Report(new(op.OperationId, op.Path, TransferStage.Failed, 0, 0, Error: ex.Message));
                        throw;
                    }
                });
            }

            foreach (var op in selected.Where(x => x.Kind == OperationKind.Move && !aggregatedOperationIds.Contains(x.OperationId)))
            {
                var move = op.Move ?? throw new InvalidOperationException("移动操作缺少描述。");
                var target = move.ExecuteOn == EndpointSide.Left ? left : right;
                var source = move.ChangedOn == EndpointSide.Left ? left : right;
                await Mark(op, JournalState.Running, TransferStage.Preparing);
                progress?.Report(new(op.OperationId, op.Path, TransferStage.Preparing, 0, 0));
                try
                {
                    // A same-target dual move is intentionally represented as an
                    // internal committed operation so the paired baseline gets
                    // re-keyed even though neither endpoint needs I/O.
                    if (move.ChangedOn == move.ExecuteOn && move.PreferredExecution == EndpointMoveExecution.None && move.Fallback == MoveFallback.None)
                    {
                        var after = await RequirePostPublishFingerprintAsync(target, move.ToPath, ct);
                        await Record(op, TransferStage.Committed, 0, after, after, true);
                        progress?.Report(new(op.OperationId, op.Path, TransferStage.Committed, 0, 0));
                        continue;
                    }
                    var from = await target.StatAsync(move.FromPath, ct);
                    var expected = snapshot.MoveFromFingerprints?.GetValueOrDefault(op.OperationId);
                    if (from is null || (move.Kind == EntryKind.File && (expected is null || from.Fingerprint is null || !expected.Matches(from.Fingerprint, MoveTolerance(target)))))
                        throw new IOException($"移动源在比较后已改变：{move.FromPath}");
                    if (await target.StatAsync(move.ToPath, ct) is not null) throw new IOException($"移动目标已存在：{move.ToPath}");
                    try
                    {
                        if (move.PreferredExecution == EndpointMoveExecution.None) throw new NotSupportedException();
                        await target.MoveAsync(move.FromPath, move.ToPath, ct);
                    }
                    catch (NotSupportedException) when (move.Fallback == MoveFallback.CrossEndpointCopyDelete)
                    {
                        // The changed endpoint already contains the destination
                        // content. Publish a no-overwrite copy to the execution
                        // endpoint, then delete its still-validated old object.
                        var temporary = move.ToPath + ".fengsync-" + Guid.NewGuid().ToString("N") + ".partial";
                        await CopyAsync(source, target, move.ToPath, temporary, ct);
                        await target.MoveAsync(temporary, move.ToPath, ct);
                        await target.DeleteAsync(move.FromPath, move.Kind == EntryKind.Directory, ct);
                    }
                    var changed = await RequirePostPublishFingerprintAsync(source, move.ToPath, ct);
                    var targetAfter = await RequirePostPublishFingerprintAsync(target, move.ToPath, ct);
                    await Record(op, TransferStage.Committed, 0, changed, targetAfter, true);
                    progress?.Report(new(op.OperationId, op.Path, TransferStage.Committed, 0, 0));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Record(op, TransferStage.Failed, 0, null, null, false, ex.Message);
                    progress?.Report(new(op.OperationId, op.Path, TransferStage.Failed, 0, 0, Error: ex.Message));
                }
            }

            if (results.Values.Any(x => x.Stage == TransferStage.Failed))
                return new(runId, results.Values.OrderBy(x => x.Path).ToList(), NeedsRecovery: true);

            var copyOps = selected.Where(x => x.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft).ToList();
            var copyChannel = Channel.CreateBounded<SyncOperation>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true
            });
            var active = 0;
            var activeLock = new object();

            async Task RunCopyWorker()
            {
                await foreach (var op in copyChannel.Reader.ReadAllAsync(ct))
                {
                    var nowActive = 0;
                    lock (activeLock) nowActive = ++active;
                    try { await RunOneCopyAsync(op, nowActive, verifyCopies, snapshot, left, right, governor, progress, Mark, Record, ct); }
                    finally { lock (activeLock) active--; }
                }
            }

            var workerCount = Math.Min(Math.Max(1, maxConcurrentCopies), copyOps.Count == 0 ? 1 : Math.Min(capacity, copyOps.Count));
            var workerTasks = Enumerable.Range(0, workerCount).Select(_ => Task.Run(RunCopyWorker, ct)).ToArray();
            foreach (var op in copyOps) await copyChannel.Writer.WriteAsync(op, ct);
            copyChannel.Writer.TryComplete();
            await Task.WhenAll(workerTasks);

            // A copy failure must be reported, but it must not prevent unrelated
            // deletions from reaching the endpoint.  Each deletion was planned
            // from the same stable snapshot and is independently idempotent.
            // The result remains unsuccessful below, so callers retain a recovery
            // transaction and only commit the operations that actually completed.
            var deletion = CreateDeletionStrategy(versioning);
            foreach (var op in selected.Where(x => (x.Kind is OperationKind.DeleteLeft or OperationKind.DeleteRight) && !aggregatedOperationIds.Contains(x.OperationId)).OrderByDescending(x => x.Path.Length))
            {
                progress?.Report(new(op.OperationId, op.Path, TransferStage.Deleting, 0, 0));
                try
                {
                    var deleteFromLeft = op.Kind == OperationKind.DeleteLeft;
                    var target = deleteFromLeft ? left : right;
                    var entry = (deleteFromLeft ? snapshot.LeftEntries : snapshot.RightEntries)?.GetValueOrDefault(op.Path);
                    await EnsureDeleteTargetUnchangedAsync(target, op.Path, entry, ct);
                    await deletion.DeleteAsync(target, op.Path, entry?.Kind == EntryKind.Directory, ct);
                    await Record(op, TransferStage.Committed, 0, null, null, true, null);
                    progress?.Report(new(op.OperationId, op.Path, TransferStage.Committed, 0, 0));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Record(op, TransferStage.Failed, 0, null, null, false, ex.Message);
                    progress?.Report(new(op.OperationId, op.Path, TransferStage.Failed, 0, 0, Error: ex.Message));
                    // A stale or otherwise failed delete must not prevent later,
                    // independently validated deletes from reaching their desired state.
                }
            }
            if (versioning?.Mode == VersioningMode.TimestampedArchive && !string.IsNullOrWhiteSpace(versioning.ArchiveDirectory))
                await new RetentionCleanupService().CleanupAsync(versioning.ArchiveDirectory, versioning.ToRetentionPolicy(), ct);

            await Task.Yield();
            return new(runId, results.Values.OrderBy(x => x.Path).ToList(), NeedsRecovery: results.Values.Any(x => x.Stage == TransferStage.Failed));
        }
        catch (OperationCanceledException)
        {
            foreach (var op in selected.Where(x => !results.ContainsKey(x.OperationId)))
                await Mark(op, JournalState.Cancelled, TransferStage.Cancelled);
            throw;
        }
    }

    private async Task RunOneCopyAsync(SyncOperation op, int nowActive, bool verifyCopies, PlanSnapshot snapshot, IEndpoint left, IEndpoint right,
        ResourceGovernor governor, IProgress<TransferProgress>? progress,
        Func<SyncOperation, JournalState, TransferStage, long, string?, Task> mark,
        Func<SyncOperation, TransferStage, long, Fingerprint?, Fingerprint?, bool, string?, Task> record,
        CancellationToken ct)
    {
        var source = op.Kind == OperationKind.CopyLeftToRight ? left : right;
        var target = op.Kind == OperationKind.CopyLeftToRight ? right : left;
        var sourceKey = ResourceKey.For(source);
        var targetKey = ResourceKey.For(target);
        var bytes = snapshot.SourceFingerprints.GetValueOrDefault(op.OperationId)?.Size ?? 0;
        var expectedTarget = (op.Kind == OperationKind.CopyLeftToRight ? snapshot.RightFingerprints : snapshot.LeftFingerprints)
            .GetValueOrDefault(op.OperationId);
        var committed = false;
        using (await governor.AcquireAsync(new[] { sourceKey, targetKey }, ct))
        {
            try
            {
                progress?.Report(new(op.OperationId, op.Path, TransferStage.Preparing, 0, bytes, nowActive));
                await mark(op, JournalState.Running, TransferStage.Transferring, 0, null);
                var currentTarget = await target.StatAsync(op.Path, ct);
                if (expectedTarget is null)
                {
                    if (currentTarget is not null) throw new IOException($"复制目标在比较后出现：{op.Path}");
                }
                else if (currentTarget?.Fingerprint is null || !expectedTarget.Matches(currentTarget.Fingerprint, MoveTolerance(target)))
                {
                    throw new IOException($"复制目标在比较后已改变：{op.Path}");
                }
                var (temporary, _) = await TransferResume.PrepareAsync(source, target, op.Path, ct);
                if (source is LocalEndpoint localSource && target is LocalEndpoint localTarget)
                    await TransferResume.AppendLocalAsync(localSource, localTarget, op.Path, temporary, ct);
                else
                    await CopyAsync(source, target, op.Path, temporary, ct);
                await mark(op, JournalState.Transferred, TransferStage.Transferring, bytes, null);
                progress?.Report(new(op.OperationId, op.Path, TransferStage.Transferring, bytes, bytes, nowActive));
                if (verifyCopies)
                {
                    progress?.Report(new(op.OperationId, op.Path, TransferStage.Verifying, bytes, bytes, nowActive));
                    await PublishStagedAsync(target, temporary, op.Path, expectedTarget is not null, ct);
                    // M2: per-file StatAsync on the target only — never ScanAsync
                    var verified = await new StatVerifier().VerifyAsync(source, target, op.Path, ct);
                    committed = true;
                    await record(op, TransferStage.Committed, bytes, verified.Source, verified.Target, true, null);
                }
                else
                {
                    await PublishStagedAsync(target, temporary, op.Path, expectedTarget is not null, ct);
                    committed = true;
                    // Capture post-publish fingerprints so the M5 baseline commit
                    // can derive the next two-way state from real endpoint data.
                    var sourceAfter = await RequirePostPublishFingerprintAsync(source, op.Path, ct);
                    var targetAfter = await RequirePostPublishFingerprintAsync(target, op.Path, ct);
                    await record(op, TransferStage.Committed, bytes, sourceAfter, targetAfter, true, null);
                }
                progress?.Report(new(op.OperationId, op.Path, TransferStage.Committed, bytes, bytes, nowActive));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await record(op, TransferStage.Failed, bytes, null, null, false, ex.Message);
                progress?.Report(new(op.OperationId, op.Path, TransferStage.Failed, 0, bytes, nowActive, ex.Message));
            }
            finally
            {
                // Local staging survives cancellation/failure for the next planned run.
                // Remote backends are deliberately retried from zero, so remove their staging object.
                if (!committed && target is not LocalEndpoint)
                {
                    try { await TransferResume.DiscardCandidatesAsync(target, op.Path, CancellationToken.None); }
                    catch { /* preserve original failure */ }
                }
            }
        }
    }

    private static async Task<Fingerprint> RequirePostPublishFingerprintAsync(IEndpoint endpoint, string path, CancellationToken ct)
    {
        var entry = await endpoint.StatAsync(path, ct);
        return entry?.Fingerprint ?? throw new IOException($"发布后无法确认文件元数据：{path}");
    }

    /// <summary>
    /// Deletes are allowed to continue after an unrelated copy fails, but they
    /// must still be conditional on the object observed during comparison. An
    /// already-absent object is an idempotent success; a newly created or
    /// modified object must never be deleted by an old plan.
    /// </summary>
    private static async Task EnsureDeleteTargetUnchangedAsync(IEndpoint endpoint, string path, EntrySnapshot? expected, CancellationToken ct)
    {
        var current = await endpoint.StatAsync(path, ct);
        if (current is null) return;
        if (expected is null)
            throw new IOException($"删除目标在比较后出现：{path}");
        if (current.Kind != expected.Kind)
            throw new IOException($"删除目标类型在比较后已改变：{path}");
        if (expected.Fingerprint is not null &&
            (current.Fingerprint is null || !expected.Fingerprint.Matches(current.Fingerprint, MoveTolerance(endpoint))))
            throw new IOException($"删除目标在比较后已改变：{path}");
    }

    private static TimeSpan MoveTolerance(IEndpoint endpoint) => endpoint is RcloneEndpoint ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2);

    private static Task PublishStagedAsync(IEndpoint target, string temporary, string destination, bool overwrite, CancellationToken ct) =>
        target is IStagedPublishEndpoint publisher
            ? publisher.PublishStagedAsync(temporary, destination, overwrite, ct)
            : target.MoveAsync(temporary, destination, ct);

    private static IDeletionStrategy CreateDeletionStrategy(VersioningPolicy? versioning) => versioning?.Mode switch
    {
        VersioningMode.TimestampedArchive when !string.IsNullOrWhiteSpace(versioning.ArchiveDirectory) => new ArchiveStrategy(versioning.ArchiveDirectory),
        VersioningMode.TimestampedArchive => throw new InvalidOperationException("版本保留需要设置归档目录。"),
        VersioningMode.RecycleBin => new RecycleBinStrategy(),
        _ => new PermanentDeleteStrategy()
    };

    private static async Task CopyAsync(IEndpoint source, IEndpoint target, string path, string temporary, CancellationToken ct)
    {
        if (source is LocalEndpoint localSource && target is LocalEndpoint localTarget)
        {
            await localSource.CopyToAsync(path, localTarget, temporary, ct);
            return;
        }
        var remote = source as RcloneEndpoint ?? target as RcloneEndpoint ?? throw new NotSupportedException("不支持的端点组合。");
        await remote.Client.CopyFileAsync(
            source is LocalEndpoint sl ? sl.Root : ((RcloneEndpoint)source).FileSystem,
            source is LocalEndpoint ? path : ((RcloneEndpoint)source).RemotePath(path),
            target is LocalEndpoint tl ? tl.Root : ((RcloneEndpoint)target).FileSystem,
            target is LocalEndpoint ? temporary : ((RcloneEndpoint)target).RemotePath(temporary),
            ct);
    }

    private static IReadOnlyList<string> LocalRoots(IEndpoint left, IEndpoint right) =>
        new[] { left, right }.OfType<LocalEndpoint>().Select(x => x.Root).ToList();
}

/// <summary>
/// Replaces the legacy <see cref="ContentVerifier"/> which scanned both endpoints
/// for a single path. The M2 guarantee forbids full-tree ScanAsync during
/// verification, so this class uses StatAsync on the source and target only.
/// </summary>
public sealed class StatVerifier
{
    public async Task<(Fingerprint Source, Fingerprint Target)> VerifyAsync(IEndpoint source, IEndpoint target, string path, CancellationToken ct = default)
    {
        var sourceEntry = await source.StatAsync(path, ct);
        var targetEntry = await target.StatAsync(path, ct);
        // Some remote providers expose a just-committed object with eventual
        // consistency and no stable listing ID. In that case the transfer is
        // explicitly a downgraded verification rather than a false failure.
        if (targetEntry is null && !target.Capabilities.StableIds)
            throw new IOException("传输后暂时无法读取目标文件元数据：" + path);
        if (sourceEntry?.Fingerprint is null || targetEntry?.Fingerprint is null || !sourceEntry.Fingerprint.Matches(targetEntry.Fingerprint))
            throw new IOException("传输验证失败：" + path);
        return (sourceEntry.Fingerprint, targetEntry.Fingerprint);
    }
}
