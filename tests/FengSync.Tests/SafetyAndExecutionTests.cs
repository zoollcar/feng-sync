using FengSync.Core;

namespace FengSync.Tests;

public sealed class SafetyAndExecutionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-safety-" + Guid.NewGuid().ToString("N"));
    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");
    public Task InitializeAsync() { Directory.CreateDirectory(Left); Directory.CreateDirectory(Right); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(_root)) Directory.Delete(_root, true); return Task.CompletedTask; }

    [Fact] public void Nested_endpoints_and_archive_inside_tree_are_blocked()
    {
        var nested = Path.Combine(Left, "child"); Directory.CreateDirectory(nested);
        Assert.True(PathTopologyValidator.Validate(Left, nested).HasBlockingIssues);
        Assert.True(ArchivePathValidator.Validate(Path.Combine(Left, "versions"), [Left, Right]).HasBlockingIssues);
    }

    [Fact] public void Deletion_threshold_and_empty_mirror_are_blocked()
    {
        var plan = new SyncPlan([new("a", OperationKind.DeleteRight, "test")]);
        Assert.True(DeletionGuard.Validate(plan, sourceItemCount: 0, targetItemCount: 1, SyncMode.Mirror, maxDeletes: 0, maxDeleteRatio: 0).HasBlockingIssues);
    }

    [Fact] public void Deselected_delete_is_not_counted_by_the_deletion_guard()
    {
        var plan = new SyncPlan([new("a", OperationKind.DeleteRight, "test", selected: false)]);

        Assert.False(DeletionGuard.Validate(plan, sourceItemCount: 1, targetItemCount: 1, SyncMode.Mirror, maxDeletes: 0, maxDeleteRatio: 0).HasBlockingIssues);
    }

    [Fact] public async Task Freshness_validation_rejects_changed_copy_source()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "one");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var snapshot = await PlanSnapshot.CaptureAsync(new ThreeWayPlanner().Build(left.Scan(), right.Scan(), null), left, right);
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "changed");
        Assert.True((await new PlanFreshnessValidator().ValidateAsync(snapshot, left, right)).HasBlockingIssues);
    }

    [Fact]
    public void Hashless_remote_fingerprints_allow_listing_timestamp_precision_but_not_material_changes()
    {
        var original = new Fingerprint(12, DateTimeOffset.Parse("2026-07-19T00:00:00Z"), null);
        Assert.True(original.Matches(new(12, original.ModifiedUtc.AddSeconds(4), null), TimeSpan.FromSeconds(5)));
        Assert.False(original.Matches(new(12, original.ModifiedUtc.AddSeconds(6), null), TimeSpan.FromSeconds(5)));
        Assert.False(original.Matches(new(13, original.ModifiedUtc, null), TimeSpan.FromSeconds(5)));
    }

    [Fact] public async Task Unified_executor_reports_byte_progress_and_verifies_copy()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), new string('x', 1024));
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right); var events = new List<TransferProgress>();
        var result = await new SyncExecutor().ExecuteAsync(await PlanSnapshot.CaptureAsync(new ThreeWayPlanner().Build(left.Scan(), right.Scan(), null), left, right), left, right, new Progress<TransferProgress>(events.Add));
        Assert.True(result.Succeeded); Assert.Equal(1, result.SucceededOperations); Assert.Contains(events, x => x.BytesCompleted == 1024 && x.Stage == TransferStage.Committed);
    }

    [Fact] public async Task Local_executor_resumes_a_verified_fengsync_partial_file_without_exposing_it_to_the_plan()
    {
        var content = string.Concat(Enumerable.Repeat("abcdefgh", 8192));
        await File.WriteAllTextAsync(Path.Combine(Left, "large.bin"), content);
        var partial = Path.Combine(Right, "large.bin.fengsync-resume.partial");
        await File.WriteAllTextAsync(partial, content[..12000]);
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        Assert.Single(left.Scan()); Assert.Empty(right.Scan());

        var plan = new SyncPlan([new SyncOperation("large.bin", OperationKind.CopyLeftToRight, "copy")]);
        var result = await new SyncExecutor().ExecuteAsync(await PlanSnapshot.CaptureAsync(plan, left, right), left, right);

        Assert.True(result.Succeeded);
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(Right, "large.bin")));
        Assert.False(File.Exists(partial));
    }

    [Fact] public async Task Local_executor_discards_a_tampered_partial_before_copying_from_zero()
    {
        var content = new string('a', 32768);
        await File.WriteAllTextAsync(Path.Combine(Left, "tampered.bin"), content);
        var partial = Path.Combine(Right, "tampered.bin.fengsync-resume.partial");
        await File.WriteAllTextAsync(partial, new string('z', 16000));
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var plan = new SyncPlan([new SyncOperation("tampered.bin", OperationKind.CopyLeftToRight, "copy")]);

        var result = await new SyncExecutor().ExecuteAsync(await PlanSnapshot.CaptureAsync(plan, left, right), left, right);

        Assert.True(result.Succeeded);
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(Right, "tampered.bin")));
        Assert.False(File.Exists(partial));
    }

    [Fact] public async Task V2_copy_atomically_replaces_the_unchanged_existing_target()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "initial.txt"), "right-change");
        await File.WriteAllTextAsync(Path.Combine(Right, "initial.txt"), "left-change");
        var left = new LocalEndpoint(Left);
        var right = new LocalEndpoint(Right);
        var plan = new SyncPlan([new SyncOperation("initial.txt", OperationKind.CopyLeftToRight, "update")]);
        var snapshot = await PlanSnapshot.CaptureAsync(plan, left, right);

        var result = await new FengSync.Core.Execution.SyncExecutorV2().ExecuteAsync(snapshot, left, right);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Operations.Select(x => $"{x.Kind} {x.Path}: {x.Error}")));
        Assert.Equal("right-change", await File.ReadAllTextAsync(Path.Combine(Right, "initial.txt")));
    }

    [Fact]
    public async Task V2_runs_an_unchanged_delete_after_an_unrelated_copy_fails()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "copy.txt"), "source");
        await File.WriteAllTextAsync(Path.Combine(Right, "delete.txt"), "remove");
        var left = new LocalEndpoint(Left);
        var right = new LocalEndpoint(Right);
        var copy = new SyncOperation("copy.txt", OperationKind.CopyLeftToRight, "copy");
        var delete = new SyncOperation("delete.txt", OperationKind.DeleteRight, "delete");
        var snapshot = await PlanSnapshot.CaptureAsync(new SyncPlan([copy, delete]), left, right);

        // Cause only the copy to fail by making its previously absent target a directory.
        Directory.CreateDirectory(Path.Combine(Right, "copy.txt"));

        var result = await new FengSync.Core.Execution.SyncExecutorV2().ExecuteAsync(snapshot, left, right);

        Assert.Equal(TransferStage.Failed, result.Operations.Single(x => x.OperationId == copy.OperationId).Stage);
        Assert.Equal(TransferStage.Committed, result.Operations.Single(x => x.OperationId == delete.OperationId).Stage);
        Assert.False(File.Exists(Path.Combine(Right, "delete.txt")));
    }

    [Fact]
    public async Task V2_preserves_a_delete_target_changed_after_comparison_when_an_unrelated_copy_fails()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "copy.txt"), "source");
        await File.WriteAllTextAsync(Path.Combine(Right, "delete.txt"), "old");
        var left = new LocalEndpoint(Left);
        var right = new LocalEndpoint(Right);
        var copy = new SyncOperation("copy.txt", OperationKind.CopyLeftToRight, "copy");
        var delete = new SyncOperation("delete.txt", OperationKind.DeleteRight, "delete");
        var snapshot = await PlanSnapshot.CaptureAsync(new SyncPlan([copy, delete]), left, right);

        Directory.CreateDirectory(Path.Combine(Right, "copy.txt"));
        await File.WriteAllTextAsync(Path.Combine(Right, "delete.txt"), "newer contents");

        var result = await new FengSync.Core.Execution.SyncExecutorV2().ExecuteAsync(snapshot, left, right);

        Assert.Equal(TransferStage.Failed, result.Operations.Single(x => x.OperationId == copy.OperationId).Stage);
        var deleteResult = result.Operations.Single(x => x.OperationId == delete.OperationId);
        Assert.Equal(TransferStage.Failed, deleteResult.Stage);
        Assert.Contains("删除目标在比较后已改变", deleteResult.Error);
        Assert.Equal("newer contents", await File.ReadAllTextAsync(Path.Combine(Right, "delete.txt")));
    }

    [Fact] public void Maintenance_only_removes_expired_recognized_temporary_files()
    {
        var expired = Path.Combine(Right, "old.bin.fengsync-maintenance.partial");
        var recent = Path.Combine(Right, "recent.bin.fengsync-maintenance.partial");
        var ordinary = Path.Combine(Right, "ordinary.partial");
        File.WriteAllText(expired, "x"); File.WriteAllText(recent, "x"); File.WriteAllText(ordinary, "x");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));

        var removed = TransferTemporaryMaintenance.RemoveExpiredLocalFiles([Right], TimeSpan.FromDays(7), DateTimeOffset.UtcNow);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(expired)); Assert.True(File.Exists(recent)); Assert.True(File.Exists(ordinary));
    }

    [Fact] public async Task Archive_retention_honors_days_count_and_capacity()
    {
        var archive = Path.Combine(_root, "archive"); Directory.CreateDirectory(archive);
        await File.WriteAllTextAsync(Path.Combine(archive, "old.txt"), "old"); File.SetLastWriteTimeUtc(Path.Combine(archive, "old.txt"), DateTime.UtcNow.AddDays(-3));
        await File.WriteAllTextAsync(Path.Combine(archive, "one.txt"), new string('1', 10)); await File.WriteAllTextAsync(Path.Combine(archive, "two.txt"), new string('2', 10));
        var cleanup = new RetentionCleanupService();
        var candidates = await cleanup.PreviewAsync(archive, new RetentionPolicy(KeepDays: 1, MaxVersionsPerFile: 1, MaxTotalBytes: 10));
        Assert.Contains(candidates, x => x.Path.EndsWith("old.txt"));
        Assert.True(candidates.Count >= 2);
    }

    [Fact] public async Task Retention_count_keeps_same_named_files_in_different_original_directories()
    {
        var archive = Path.Combine(_root, "archive-by-path");
        var first = Path.Combine(archive, "20250101-000000000", "a");
        var second = Path.Combine(archive, "20250101-000000001", "b");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(first, "readme.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(second, "readme.txt"), "second");

        var candidates = await new RetentionCleanupService().PreviewAsync(archive, new RetentionPolicy(MaxVersionsPerFile: 1));

        Assert.Empty(candidates);
    }

    [Fact] public void Versioning_policy_exposes_all_retention_limits_to_cleanup()
    {
        var policy = new VersioningPolicy(VersioningMode.TimestampedArchive, "C:\\archive", KeepDays: 7, MaxVersionsPerFile: 4, MaxTotalBytes: 1024);

        Assert.Equal(new RetentionPolicy(7, 4, 1024), policy.ToRetentionPolicy());
    }

    [Fact] public async Task Failed_unified_run_does_not_publish_a_two_way_baseline()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "content");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var plan = new SyncPlan([new SyncOperation("a.txt", OperationKind.CopyLeftToRight, "test")]);
        Directory.CreateDirectory(Path.Combine(Right, "a.txt"));
        var snapshot = await PlanSnapshot.CaptureAsync(plan, left, right);
        var transaction = new BaselineRepository().Begin(left, right);
        var run = await new SyncExecutor().ExecuteAsync(snapshot, left, right);
        var committed = await new BaselineRepository().CommitAsync(transaction, left, right, run.Succeeded);
        Assert.False(run.Succeeded);
        Assert.Equal(BaselineTransactionState.NeedsRecovery, committed.State);
        Assert.Null(await new BaselineStore().LoadAsync(left, right));
        Assert.Single(Directory.EnumerateFiles(Right, "*.fengsync-*.partial", SearchOption.AllDirectories));
    }

    [Fact] public async Task Remote_baseline_is_keyed_by_stable_endpoint_identity_and_atomically_replaces_only_on_commit()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "drive-file.txt"), "first");
        var left = new LocalEndpoint(Left);
        var right = new LocalEndpoint(Right);
        var store = new RemoteBaselineStore(Path.Combine(_root, "remote-baselines"));

        Assert.Null(await store.LoadAsync(left, right));
        await store.CommitAsync(left, right);
        var first = await store.LoadAsync(left, right);
        Assert.Single(first!);

        await File.WriteAllTextAsync(Path.Combine(Left, "drive-file.txt"), "second-version");
        // A failed transfer never invokes CommitAsync, so the durable baseline remains first.
        var unchanged = await store.LoadAsync(left, right);
        Assert.Equal(first, unchanged);
        await store.CommitAsync(left, right);
        Assert.NotEqual(first![0].Left!.Fingerprint!.Size, (await store.LoadAsync(left, right))![0].Left!.Fingerprint!.Size);
    }

    [Fact] public void Recycle_bin_is_allowed_only_for_windows_local_profiles()
    {
        var local = SyncProfile.Create("local", Left, Right) with { Versioning = new(VersioningMode.RecycleBin) };
        var remote = local with { LeftPath = "sftp://server/data" };
        var service = new FengSync.Core.Capabilities.FeatureCapabilityService();
        Assert.False(service.Evaluate(remote).CanRun);
        Assert.Equal(OperatingSystem.IsWindows(), service.Evaluate(local).CanRun);
    }

    [Fact] public void Invalid_profile_deletion_limits_are_blocked_before_planning()
    {
        var profile = SyncProfile.Create("limits", Left, Right) with { MaxDeletes = -1, MaxDeleteRatio = 1.1 };
        Assert.False(new FengSync.Core.Capabilities.FeatureCapabilityService().Evaluate(profile).CanRun);
    }

    [Fact] public async Task Recovery_coordinator_combines_failed_journals_and_persisted_baseline_transactions_and_cleans_safe_partials()
    {
        var jobs = Path.Combine(_root, "recovery-jobs");
        var transactions = new BaselineTransactionStore(Path.Combine(_root, "recovery-transactions"));
        var journal = new SyncJournal(Guid.NewGuid(), DateTimeOffset.UtcNow,
            [new(Guid.NewGuid(), "a.txt", OperationKind.CopyLeftToRight, JournalState.Failed, "network lost")]);
        await new TaskJournalStore(jobs).SaveAsync(journal);
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var transaction = new BaselineRepository(transactionStore: transactions).Begin(left, right).Rollback(needsRecovery: true);
        await transactions.SaveAsync(transaction);
        await File.WriteAllTextAsync(Path.Combine(Right, "a.txt.fengsync-crash.partial"), "partial");

        var coordinator = new RecoveryCoordinator(new TaskJournalStore(jobs), transactions);
        var items = await coordinator.FindRecoveryRequiredAsync();

        Assert.Contains(items, x => x.Journal?.JobId == journal.JobId && x.Detail.Contains("network lost"));
        Assert.Contains(items, x => x.Transaction?.Id == transaction.Id && x.Detail.Contains("基线"));
        Assert.Equal(1, coordinator.RemoveSafeLocalTemporaryFiles(items));
        Assert.False(File.Exists(Path.Combine(Right, "a.txt.fengsync-crash.partial")));
    }

    [Fact] public async Task Recovery_coordinator_keeps_cancelled_journal_details_visible()
    {
        var jobs = new TaskJournalStore(Path.Combine(_root, "cancelled-jobs"));
        var journal = new SyncJournal(Guid.NewGuid(), DateTimeOffset.UtcNow,
            [new(Guid.NewGuid(), "large.bin", OperationKind.CopyLeftToRight, JournalState.Cancelled, "user cancelled")], [Right]);
        await jobs.SaveAsync(journal);
        var partial = Path.Combine(Right, "large.bin.fengsync-cancelled.partial");
        await File.WriteAllTextAsync(partial, "partial");

        var coordinator = new RecoveryCoordinator(jobs, new BaselineTransactionStore(Path.Combine(_root, "empty-transactions")));
        var items = await coordinator.FindRecoveryRequiredAsync();

        Assert.Contains(items, x => x.Journal?.JobId == journal.JobId && x.Detail.Contains("user cancelled"));
        Assert.Equal(1, coordinator.RemoveSafeLocalTemporaryFiles(items));
        Assert.False(File.Exists(partial));
    }

    [Fact] public void Run_result_presentation_reports_errors_and_builds_a_retry_plan_for_only_retryable_failures()
    {
        var retryable = new SyncOperation("retry.txt", OperationKind.CopyLeftToRight, "copy");
        var permanent = new SyncOperation("blocked.txt", OperationKind.CopyLeftToRight, "copy");
        var result = new SyncRunResult(Guid.NewGuid(),
        [new(retryable.OperationId, retryable.Path, retryable.Kind, TransferStage.Failed, Error: "网络连接中断"),
         new(permanent.OperationId, permanent.Path, permanent.Kind, TransferStage.Failed, Error: "不支持的端点组合。")]);

        var retry = RunResultPresentation.BuildRetryPlan(result, [retryable, permanent]);
        var log = RunResultPresentation.ToLog(result);

        Assert.Equal(RunDisplayOutcome.Failed, RunResultPresentation.OutcomeOf(result));
        Assert.Single(retry.Operations);
        Assert.Equal("retry.txt", retry.Operations[0].Path);
        Assert.Contains("blocked.txt", log);
        Assert.Contains("失败", log);
    }

    [Fact] public void Run_result_log_preserves_a_cancelled_outcome_and_summary_when_no_operation_started()
    {
        var result = new SyncRunResult(Guid.NewGuid(), []);

        var log = RunResultPresentation.ToLog(result, cancelled: true, summary: "用户请求停止同步。");

        Assert.Contains("结果：Cancelled", log);
        Assert.Contains("摘要：用户请求停止同步。", log);
    }

    [Fact] public void Risk_summary_counts_overwrites_deletes_transfer_and_only_allows_threshold_override()
    {
        var copy = new SyncOperation("overwrite.txt", OperationKind.CopyLeftToRight, "copy");
        var delete = new SyncOperation("gone.txt", OperationKind.DeleteRight, "delete");
        var plan = new SyncPlan([copy, delete]);
        var left = new Dictionary<string, EntrySnapshot> { ["overwrite.txt"] = new("overwrite.txt", EntryKind.File, new(12, DateTimeOffset.UtcNow, null)) };
        var right = new Dictionary<string, EntrySnapshot> { ["overwrite.txt"] = new("overwrite.txt", EntryKind.File, new(3, DateTimeOffset.UtcNow, null)), ["gone.txt"] = new("gone.txt", EntryKind.File, new(1, DateTimeOffset.UtcNow, null)) };
        var summary = SyncRiskSummary.Create(plan, left, right);
        var threshold = new SafetyValidationResult([new("delete.count", "too many", SafetySeverity.Blocking)]);
        Assert.Equal(1, summary.Copies); Assert.Equal(1, summary.Overwrites); Assert.Equal(1, summary.Deletes); Assert.Equal(12, summary.TransferBytes);
        Assert.True(SyncConfirmationPolicy.RequiresConfirmation(summary));
        Assert.True(SyncConfirmationPolicy.CanOverrideWithProfileName(threshold));
        Assert.False(SyncConfirmationPolicy.CanOverrideWithProfileName(new SafetyValidationResult([new("storage.insufficient", "no space", SafetySeverity.Blocking)])));
    }
}
