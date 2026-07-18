namespace FengSync.Core;

using FengSync.Core.Capabilities;
using FengSync.Core.Configuration;

public sealed record ProfileRunResult(string ProfileId, int Planned, int Executed, DateTimeOffset CompletedUtc);
public sealed record ProfileComparisonResult(string ProfileId, int Planned, int Selected, bool CanExecute);

/// <summary>Reusable batch entry point; UI, scheduled tasks and the command line share the same safe workflow.</summary>
public sealed class ProfileRunner
{
    private readonly RunHistoryRepository _history;
    private readonly ApplicationSettings _applicationSettings;
    public ProfileRunner(RunHistoryRepository? history = null, ApplicationSettings? applicationSettings = null)
        => (_history, _applicationSettings) = (history ?? new RunHistoryRepository(), applicationSettings ?? new ApplicationSettings());

    /// <summary>Runs the same capability and safety checks as execution, without changing either endpoint.</summary>
    public async Task<ProfileComparisonResult> CompareAsync(SyncProfile profile, CancellationToken ct = default)
    {
        await using var prepared = await PrepareAsync(profile, ct);
        return new(profile.Id, prepared.Plan.Operations.Count, prepared.Plan.Operations.Count(x => x.Selected), prepared.Plan.CanExecute);
    }

    public async Task<ProfileRunResult> RunAsync(SyncProfile profile, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await using var prepared = await PrepareAsync(profile, ct);
        if (!prepared.Plan.CanExecute && prepared.Plan.Operations.Any()) throw new InvalidOperationException("批处理遇到未裁决冲突；请先在界面中处理。 ");
        var selected = prepared.Plan.Operations.Count(x => x.Selected);
        var transaction = profile.Mode == SyncMode.TwoWay
            ? await prepared.BaselineRepository.BeginAsync(prepared.Left, prepared.Right, ct) : null;
        SyncRunResult? run = null;
        if (selected > 0)
        {
            var snapshot = await PlanSnapshot.CaptureAsync(prepared.Plan, prepared.Left, prepared.Right, ct);
            run = await new SyncExecutor().ExecuteAsync(snapshot, prepared.Left, prepared.Right,
                progress is null ? null : new Progress<TransferProgress>(x => progress.Report(x.Path)), ct, prepared.Effective.VerifyCopies, prepared.Effective.Versioning, prepared.Effective.MaxConcurrentCopies, new TaskJournalStore());
            if (transaction is not null)
            {
                transaction = run.Operations.Where(x => x.Stage == TransferStage.Committed)
                    .Aggregate(transaction, (current, item) => current.RecordCommitted(item.Path));
                await prepared.BaselineRepository.SaveAsync(transaction, ct);
                await prepared.BaselineRepository.CommitAsync(transaction, prepared.Left, prepared.Right, run.Succeeded, ct);
            }
        }
        var failed = run?.FailedOperations ?? 0;
        var succeeded = run?.SucceededOperations ?? selected;
        var transferred = run?.Operations.Sum(x => x.BytesTransferred) ?? 0;
        var outcome = failed > 0 ? (succeeded > 0 ? RunOutcome.PartialSuccess : RunOutcome.Failed) : RunOutcome.Succeeded;
        var detail = run?.Operations.FirstOrDefault(x => x.Error is not null)?.Error;
        await _history.AppendAsync(new(profile.Id, outcome, DateTimeOffset.UtcNow, prepared.Plan.Operations.Count, succeeded, failed, transferred, detail, run?.RunId), ct);
        if (run is { Succeeded: false }) throw new IOException($"同步有 {run.FailedOperations} 个操作失败；正式基线未变更。{detail}");
        return new(profile.Id, prepared.Plan.Operations.Count, selected, DateTimeOffset.UtcNow);
    }

    private async Task<PreparedProfileRun> PrepareAsync(SyncProfile profile, CancellationToken ct)
    {
        if (!profile.Enabled) throw new InvalidOperationException("配置档案已禁用。");
        var profileValidation = ProfileValidator.Validate(profile);
        if (!profileValidation.IsValid) throw new InvalidOperationException(string.Join(" ", profileValidation.Errors));
        var compatibility = new FeatureCapabilityService().Evaluate(profile);
        if (!compatibility.CanRun) throw new InvalidOperationException("该 Profile 需要修复：" + compatibility.Summary);
        var effective = EffectiveProfileSettings.Resolve(profile, _applicationSettings);
        var endpoints = await EndpointFactory.OpenAsync(profile.LeftPath, profile.RightPath, ct);
        try
        {
            var left = endpoints.Left; var right = endpoints.Right;
            var configurationSafety = left is LocalEndpoint localLeft && right is LocalEndpoint localRight
                ? new SafetyValidator().ValidateConfiguration(localLeft.Root, localRight.Root, effective.Versioning?.ArchiveDirectory)
                : SafetyValidationResult.Pass;
            if (configurationSafety.HasBlockingIssues) throw new InvalidOperationException(string.Join(" ", configurationSafety.Issues.Select(x => x.Message)));
            var scans = await Task.WhenAll(left.ScanAsync(ct), right.ScanAsync(ct));
            var leftEntries = scans[0]; var rightEntries = scans[1]; var baselines = new BaselineRepository();
            var baseline = profile.Mode == SyncMode.TwoWay ? await baselines.LoadAsync(left, right, ct) : null;
            var plan = new ModePlanner().Build(profile.Mode, leftEntries, rightEntries, baseline, effective.Filter);
            var safety = new SafetyValidator();
            var planSafety = safety.ValidatePlan(plan, leftEntries.Count, rightEntries.Count, profile.Mode, profile.MaxDeletes, profile.MaxDeleteRatio)
                .Combine(safety.ValidateCapacity(plan, leftEntries.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase), rightEntries.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase), left, right));
            if (planSafety.HasBlockingIssues) throw new InvalidOperationException(string.Join(" ", planSafety.Issues.Select(x => x.Message)));
            return new(endpoints, left, right, plan, effective, baselines);
        }
        catch { await endpoints.DisposeAsync(); throw; }
    }

    private sealed class PreparedProfileRun(EndpointPair endpoints, IEndpoint left, IEndpoint right, SyncPlan plan, EffectiveProfileSettings effective, BaselineRepository baselineRepository) : IAsyncDisposable
    {
        public IEndpoint Left { get; } = left;
        public IEndpoint Right { get; } = right;
        public SyncPlan Plan { get; } = plan;
        public EffectiveProfileSettings Effective { get; } = effective;
        public BaselineRepository BaselineRepository { get; } = baselineRepository;
        public ValueTask DisposeAsync() => endpoints.DisposeAsync();
    }
}
