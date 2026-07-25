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
            // Directories must exist before their descendants are copied; do them
            // serially up front so the bounded copy pipeline can stay focused on
            // file IO.
            foreach (var op in selected.Where(x => x.Kind is OperationKind.CreateLeftDirectory or OperationKind.CreateRightDirectory))
            {
                progress?.Report(new(op.OperationId, op.Path, TransferStage.Preparing, 0, 0));
                await (op.Kind == OperationKind.CreateLeftDirectory ? left : right).CreateDirectoryAsync(op.Path, ct);
                await Record(op, TransferStage.Committed, 0, null, null, true, null);
                progress?.Report(new(op.OperationId, op.Path, TransferStage.Committed, 0, 0));
            }

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

            if (results.Values.Any(x => x.Stage == TransferStage.Failed))
            {
                await Task.Yield();
                return new(runId, results.Values.OrderBy(x => x.Path).ToList(), NeedsRecovery: true);
            }

            var deletion = CreateDeletionStrategy(versioning);
            foreach (var op in selected.Where(x => x.Kind is OperationKind.DeleteLeft or OperationKind.DeleteRight).OrderByDescending(x => x.Path.Length))
            {
                progress?.Report(new(op.OperationId, op.Path, TransferStage.Deleting, 0, 0));
                await deletion.DeleteAsync(op.Kind == OperationKind.DeleteLeft ? left : right, op.Path, false, ct);
                await Record(op, TransferStage.Committed, 0, null, null, true, null);
                progress?.Report(new(op.OperationId, op.Path, TransferStage.Committed, 0, 0));
            }
            if (versioning?.Mode == VersioningMode.TimestampedArchive && !string.IsNullOrWhiteSpace(versioning.ArchiveDirectory))
                await new RetentionCleanupService().CleanupAsync(versioning.ArchiveDirectory, versioning.ToRetentionPolicy(), ct);

            await Task.Yield();
            return new(runId, results.Values.OrderBy(x => x.Path).ToList());
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
        var committed = false;
        using (await governor.AcquireAsync(new[] { sourceKey, targetKey }, ct))
        {
            try
            {
                progress?.Report(new(op.OperationId, op.Path, TransferStage.Preparing, 0, bytes, nowActive));
                await mark(op, JournalState.Running, TransferStage.Transferring, 0, null);
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
                    await target.MoveAsync(temporary, op.Path, ct);
                    // M2: per-file StatAsync on the target only — never ScanAsync
                    await new StatVerifier().VerifyAsync(source, target, op.Path, ct);
                }
                else
                {
                    await target.MoveAsync(temporary, op.Path, ct);
                }
                committed = true;
                // Capture verified post-publish fingerprints so the M5 baseline
                // commit can derive the next two-way state from real data.
                var sourceAfter = await RequirePostPublishFingerprintAsync(source, op.Path, ct);
                var targetAfter = await RequirePostPublishFingerprintAsync(target, op.Path, ct);
                await record(op, TransferStage.Committed, bytes, sourceAfter, targetAfter, true, null);
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
    public async Task VerifyAsync(IEndpoint source, IEndpoint target, string path, CancellationToken ct = default)
    {
        var sourceEntry = await source.StatAsync(path, ct);
        var targetEntry = await target.StatAsync(path, ct);
        // Some remote providers expose a just-committed object with eventual
        // consistency and no stable listing ID. In that case the transfer is
        // explicitly a downgraded verification rather than a false failure.
        if (targetEntry is null && !target.Capabilities.StableIds) return;
        if (sourceEntry?.Fingerprint is null || targetEntry?.Fingerprint is null || !sourceEntry.Fingerprint.Matches(targetEntry.Fingerprint))
            throw new IOException("传输验证失败：" + path);
    }
}
