namespace FengSync.Core;

using FengSync.Core.Capabilities;
using FengSync.Core.Configuration;
using FengSync.Core.Diagnostics;
using FengSync.Core.Scanning;

public sealed record ProfileRunResult(string ProfileId, int Planned, int Executed, DateTimeOffset CompletedUtc);
public sealed record ProfileComparisonResult(string ProfileId, int Planned, int Selected, bool CanExecute);

/// <summary>Reusable batch entry point; UI, scheduled tasks and the command line share the same safe workflow.</summary>
public sealed class ProfileRunner
{
    private readonly RunHistoryRepository _history;
    private readonly ApplicationSettings _applicationSettings;
    private readonly RcloneRcClient? _sharedRcloneClient;
    public ProfileRunner(RunHistoryRepository? history = null, ApplicationSettings? applicationSettings = null,
        RcloneRcClient? sharedRcloneClient = null)
        => (_history, _applicationSettings, _sharedRcloneClient) =
            (history ?? new RunHistoryRepository(), applicationSettings ?? new ApplicationSettings(), sharedRcloneClient);

    /// <summary>Runs the same capability and safety checks as execution, without changing either endpoint.</summary>
    public async Task<ProfileComparisonResult> CompareAsync(SyncProfile profile, CancellationToken ct = default)
    {
        await using var prepared = await PrepareAsync(profile, ct);
        return new(profile.Id, prepared.Plan.Operations.Count, prepared.Plan.Operations.Count(x => x.Selected), prepared.Plan.CanExecute);
    }

    public async Task<ProfileRunResult> RunAsync(SyncProfile profile, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var historyLogged = false;
        try
        {
        await using var prepared = await PrepareAsync(profile, ct);
        if (!prepared.Plan.CanExecute && prepared.Plan.Operations.Any()) throw new InvalidOperationException("批处理遇到未裁决冲突；请先在界面中处理。 ");
        var selected = prepared.Plan.Operations.Count(x => x.Selected);
        // Mirror/Update also need a paired baseline for safe move propagation.
        var transaction = await prepared.BaselineRepository.BeginAsync(prepared.Left, prepared.Right, ct);
        SyncRunResult? run = null;
        if (selected > 0)
        {
            // M1/M2: build the per-operation fingerprints from the comparison
            // snapshot that PrepareAsync already produced, so the executor does
            // not need to call ScanAsync a third time. The V2 executor performs
            // executes the user-confirmed operations without comparing endpoint
            // state again. Post-publish StatVerifier checks are retained to verify
            // transfer integrity and build the next baseline.
            var snapshot = PlanSnapshot.FromComparison(prepared.Plan, prepared.Comparison);
            run = await new Execution.SyncExecutorV2().ExecuteAsync(snapshot, prepared.Left, prepared.Right,
                progress is null ? null : new Progress<TransferProgress>(x => progress.Report(x.Path)), ct, prepared.Effective.VerifyCopies, prepared.Effective.Versioning, resourceGovernor: null, journals: new TaskJournalStore(), maxConcurrentCopies: prepared.Effective.MaxConcurrentCopies);
            transaction = run.Operations.Where(x => x.Stage == TransferStage.Committed)
                .Aggregate(transaction, (current, item) => current.RecordCommitted(item.Path));
            await prepared.BaselineRepository.SaveAsync(transaction, ct);
            await CommitBaselineFromResultsAsync(prepared, transaction, run, ct);
        }
        // A successful no-op two-way run establishes (or refreshes) the paired baseline.
        // Without this, two initially equal folders could never learn that a later absence
        // is a deletion rather than an initial-sync difference.
        else
        {
            await CommitBaselineNoopAsync(prepared, transaction, ct);
        }
        var failed = run?.FailedOperations ?? 0;
        var succeeded = run?.SucceededOperations ?? selected;
        var transferred = run?.Operations.Sum(x => x.BytesTransferred) ?? 0;
        var outcome = failed > 0 ? (succeeded > 0 ? RunOutcome.PartialSuccess : RunOutcome.Failed) : RunOutcome.Succeeded;
        var detail = run?.Operations.FirstOrDefault(x => x.Error is not null)?.Error;
        await _history.AppendAsync(new(profile.Id, outcome, DateTimeOffset.UtcNow, prepared.Plan.Operations.Count, succeeded, failed, transferred, detail, run?.RunId), CancellationToken.None);
        historyLogged = true;
        if (run is { Succeeded: false }) throw new IOException($"同步有 {run.FailedOperations} 个操作失败；正式基线未变更。{detail}");
        return new(profile.Id, prepared.Plan.Operations.Count, selected, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!historyLogged)
        {
            await _history.AppendAsync(new(profile.Id, RunOutcome.Cancelled, DateTimeOffset.UtcNow, 0, 0, 0, 0, "同步已取消。"), CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (!historyLogged)
        {
            await _history.AppendAsync(RunHistoryEntry.FromFailure(profile.Id, RunOutcome.Failed,
                DateTimeOffset.UtcNow, ex.Message, ex), CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// M5: commits the next paired baseline derived from the operation results
    /// captured by the executor. If the run had any failed operations the
    /// transaction is rolled back to NeedsRecovery so the previous baseline
    /// remains the deletion authority. No <see cref="IEndpoint.ScanAsync"/> is
    /// called; the next state comes from <see cref="BaselineStateBuilder"/>.
    /// </summary>
    private static async Task<BaselineTransaction> CommitBaselineFromResultsAsync(PreparedProfileRun prepared, BaselineTransaction transaction, SyncRunResult run, CancellationToken ct)
    {
        if (!run.Succeeded)
        {
            // The executor may complete independent deletes after a copy failed.
            // Persist only those committed results, leaving every failed or skipped
            // path at its previous baseline state for a safe retry.
            if (run.SucceededOperations > 0)
                await prepared.BaselineRepository.CommitFromResultsAsync(prepared.Left, prepared.Right,
                    new BaselineCommitInput(prepared.Comparison, run.Operations.ToDictionary(x => x.OperationId), transaction), ct);
            var rolledBack = transaction.Rollback(needsRecovery: true);
            await prepared.BaselineRepository.SaveAsync(rolledBack, ct);
            return rolledBack;
        }
        var input = new BaselineCommitInput(prepared.Comparison, run.Operations.ToDictionary(x => x.OperationId), transaction);
        await prepared.BaselineRepository.CommitFromResultsAsync(prepared.Left, prepared.Right, input, ct);
        var completed = transaction.Complete();
        await prepared.BaselineRepository.SaveAsync(completed, ct);
        return completed;
    }

    /// <summary>
    /// M5: a no-op two-way run still establishes (or refreshes) the baseline so
    /// future comparisons know the two folders match exactly. Uses the snapshot
    /// path because there are no operation results to drive state derivation.
    /// </summary>
    private static async Task<BaselineTransaction> CommitBaselineNoopAsync(PreparedProfileRun prepared, BaselineTransaction transaction, CancellationToken ct)
    {
        await prepared.BaselineRepository.CommitFromSnapshotAsync(prepared.Left, prepared.Right, prepared.Comparison, ct);
        var completed = transaction.Complete();
        await prepared.BaselineRepository.SaveAsync(completed, ct);
        return completed;
    }

    private async Task<PreparedProfileRun> PrepareAsync(SyncProfile profile, CancellationToken ct)
    {
        if (!profile.Enabled) throw new InvalidOperationException("配置档案已禁用。");
        var profileValidation = ProfileValidator.Validate(profile);
        if (!profileValidation.IsValid) throw new InvalidOperationException(string.Join(" ", profileValidation.Errors));
        var compatibility = new FeatureCapabilityService().Evaluate(profile);
        if (!compatibility.CanRun) throw new InvalidOperationException("该 Profile 需要修复：" + compatibility.Summary);
        var effective = EffectiveProfileSettings.Resolve(profile, _applicationSettings);
        var endpoints = _sharedRcloneClient is null
            ? await EndpointFactory.OpenAsync(profile.LeftPath, profile.RightPath, ct)
            : EndpointFactory.OpenWithClient(profile.LeftPath, profile.RightPath, _sharedRcloneClient);
        try
        {
            var left = endpoints.Left; var right = endpoints.Right;
            var configurationSafety = left is LocalEndpoint localLeft && right is LocalEndpoint localRight
                ? new SafetyValidator().ValidateConfiguration(localLeft.Root, localRight.Root, effective.Versioning?.ArchiveDirectory)
                : SafetyValidationResult.Pass;
            if (configurationSafety.HasBlockingIssues) throw new InvalidOperationException(string.Join(" ", configurationSafety.Issues.Select(x => x.Message)));
            var baselines = new BaselineRepository();
            var baselineLoad = await baselines.LoadDetailedAsync(left, right, ct);
            var baseline = baselineLoad.CanPropagateDeletes ? baselineLoad.Entries : null;
            // Build a single paired snapshot of both endpoints so the planner, the
            // safety check, the freshness validator and the baseline commit all
            // operate on the same enumeration — no module below this point may
            // call ScanAsync again.
            var comparison = await new ComparisonSnapshotBuilder().CaptureAsync(left, right, ComparisonMode.TimeAndSize, TimeSpan.FromSeconds(effective.TimeToleranceSeconds), baseline, ct);
            var leftEntries = comparison.Left.Entries; var rightEntries = comparison.Right.Entries;
            var plan = new ModePlanner().Build(profile.Mode, leftEntries, rightEntries, baseline, effective.Filter, left.Capabilities, right.Capabilities);
            var safety = new SafetyValidator();
            var planSafety = safety.ValidatePlan(plan, leftEntries.Count, rightEntries.Count, profile.Mode, profile.MaxDeletes, profile.MaxDeleteRatio)
                .Combine(safety.ValidateCapacity(plan, comparison.Left.ByPath as IReadOnlyDictionary<string, EntrySnapshot>, comparison.Right.ByPath as IReadOnlyDictionary<string, EntrySnapshot>, left, right));
            if (planSafety.HasBlockingIssues) throw new InvalidOperationException(string.Join(" ", planSafety.Issues.Select(x => x.Message)));
            comparison.Plan = plan;
            return new(endpoints, left, right, comparison, plan, effective, baselines);
        }
        catch { await endpoints.DisposeAsync(); throw; }
    }

    private sealed class PreparedProfileRun(EndpointPair endpoints, IEndpoint left, IEndpoint right, ComparisonSnapshot comparison, SyncPlan plan, EffectiveProfileSettings effective, BaselineRepository baselineRepository) : IAsyncDisposable
    {
        public IEndpoint Left { get; } = left;
        public IEndpoint Right { get; } = right;
        public ComparisonSnapshot Comparison { get; } = comparison;
        public SyncPlan Plan { get; } = plan;
        public EffectiveProfileSettings Effective { get; } = effective;
        public BaselineRepository BaselineRepository { get; } = baselineRepository;
        public ValueTask DisposeAsync() => endpoints.DisposeAsync();
    }
}
