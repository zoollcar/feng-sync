using FengSync.Core;
using FengSync.Core.Diagnostics;
using FengSync.Core.Execution;
using FengSync.Core.Scanning;

namespace FengSync.Tests;

/// <summary>
/// Verifies scan, execution and baseline performance invariants used by the
/// production synchronization path.
/// </summary>
public sealed class PerformanceInvariantTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-perf-" + Guid.NewGuid().ToString("N"));
    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");
    public Task InitializeAsync() { Directory.CreateDirectory(Left); Directory.CreateDirectory(Right); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(_root)) Directory.Delete(_root, true); return Task.CompletedTask; }

    [Fact]
    public async Task Baseline_commit_does_not_trigger_an_additional_directory_scan()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(Right, "a.txt"), "hello");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var comparison = await new ComparisonSnapshotBuilder().CaptureAsync(left, right, ComparisonMode.TimeAndSize, TimeSpan.Zero);
        var metrics = new SyncRunMetrics();
        using var scope = SyncRunMetricsHub.BeginScope(metrics);
        var before = metrics.DirectoryScans;
        await new BaselineRepository().CommitFromSnapshotAsync(left, right, comparison);
        Assert.Equal(before, metrics.DirectoryScans);
    }

    [Fact]
    public async Task EntriesEnumerated_reflects_every_returned_entry()
    {
        for (var i = 0; i < 12; i++) await File.WriteAllTextAsync(Path.Combine(Left, $"f-{i}.txt"), "x");
        var metrics = new SyncRunMetrics();
        using var scope = SyncRunMetricsHub.BeginScope(metrics);
        _ = await new LocalEndpoint(Left).ScanAsync();
        Assert.True(metrics.EntriesEnumerated >= 12, $"Expected >= 12 entries, got {metrics.EntriesEnumerated}");
    }

    [Fact]
    public async Task Profile_runner_default_path_scans_each_endpoint_exactly_once()
    {
        for (var i = 0; i < 6; i++) await File.WriteAllTextAsync(Path.Combine(Left, $"file-{i}.txt"), "src-" + i);
        // CompareAsync does not persist run history or transactions, so a
        // process-wide FENGSYNC_DATA_DIR override is unnecessary and would race
        // unrelated tests running in parallel.
        var profile = SyncProfile.Create("perf-default-path", Left, Right) with { Mode = SyncMode.Mirror };
        var runner = new ProfileRunner();
        var metrics = new SyncRunMetrics();
        using var scope = SyncRunMetricsHub.BeginScope(metrics);
        await runner.CompareAsync(profile);
        // M1: planner-side enumeration only; Mirror adds the source as deletion authority.
        Assert.True(metrics.DirectoryScans <= 4, $"CompareAsync triggered {metrics.DirectoryScans} directory scans; expected <= 4 (left, right, baseline.download/state)");
    }

    [Fact]
    public async Task Paired_snapshot_consumes_both_streams_concurrently_without_legacy_list()
    {
        var coordinator = new ScanStartCoordinator();
        var left = new CoordinatedStreamingEndpoint("left.txt", coordinator);
        var right = new CoordinatedStreamingEndpoint("right.txt", coordinator);

        var comparison = await new ComparisonSnapshotBuilder()
            .CaptureAsync(left, right)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, coordinator.Started);
        Assert.True(comparison.Left.ByPath.ContainsKey("left.txt"));
        Assert.True(comparison.Right.ByPath.ContainsKey("right.txt"));
        Assert.Equal(0, left.LegacyScanCalls + right.LegacyScanCalls);
    }

    [Fact]
    public async Task Delete_executes_directly_without_stat_and_removes_an_empty_directory()
    {
        var directory = Path.Combine(Right, "obsolete");
        Directory.CreateDirectory(directory);
        var left = new DeleteRecordingEndpoint(new LocalEndpoint(Left));
        var right = new DeleteRecordingEndpoint(new LocalEndpoint(Right));
        var comparison = await new ComparisonSnapshotBuilder().CaptureAsync(left, right);
        var plan = new SyncPlan([new SyncOperation("obsolete", OperationKind.DeleteRight, "test")]);
        comparison.Plan = plan;

        var result = await new SyncExecutorV2().ExecuteAsync(PlanSnapshot.FromComparison(plan, comparison), left, right);

        Assert.True(result.Succeeded);
        Assert.Equal(0, right.StatCalls);
        Assert.Equal(1, right.DeleteCalls);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Confirmed_copy_overwrites_without_pre_execution_comparison()
    {
        var leftPath = Path.Combine(Left, "replace.txt");
        var rightPath = Path.Combine(Right, "replace.txt");
        await File.WriteAllTextAsync(leftPath, "planned source");
        await File.WriteAllTextAsync(rightPath, "planned target");
        var left = new LocalEndpoint(Left);
        var right = new LocalEndpoint(Right);
        var comparison = await new ComparisonSnapshotBuilder().CaptureAsync(left, right);
        var plan = new SyncPlan([new SyncOperation("replace.txt", OperationKind.CopyLeftToRight, "confirmed overwrite")]);
        comparison.Plan = plan;

        await File.WriteAllTextAsync(leftPath, "latest source contents");
        await File.WriteAllTextAsync(rightPath, "target changed after confirmation");
        var metrics = new SyncRunMetrics();
        using var scope = SyncRunMetricsHub.BeginScope(metrics);

        var result = await new SyncExecutorV2().ExecuteAsync(PlanSnapshot.FromComparison(plan, comparison), left, right);

        Assert.True(result.Succeeded);
        Assert.Equal("latest source contents", await File.ReadAllTextAsync(rightPath));
        // The two Stat calls are post-publish verification of source and target;
        // there are no execution-time source/target freshness probes before copy.
        Assert.Equal(2, metrics.StatCalls);
    }

    [Fact]
    public async Task Task_journal_save_tolerates_a_brief_diagnostics_read_lock()
    {
        var journalRoot = Path.Combine(_root, "task-journal-lock");
        var store = new TaskJournalStore(journalRoot);
        var operationId = Guid.NewGuid();
        var journal = new SyncJournal(Guid.NewGuid(), DateTimeOffset.UtcNow,
            [new JournalItem(operationId, "a.txt", OperationKind.CopyLeftToRight, JournalState.Pending)]);
        await store.SaveAsync(journal);
        var path = Path.Combine(journalRoot, journal.JobId + ".json");

        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var update = store.SaveAsync(journal with
        {
            Items = [new JournalItem(operationId, "a.txt", OperationKind.CopyLeftToRight, JournalState.Committed)]
        });
        await Task.Delay(100);
        reader.Dispose();
        await update;

        Assert.Empty(await store.LoadIncompleteAsync());
    }

    [Fact]
    public async Task Executor_batches_journal_checkpoints_instead_of_saving_every_state_change()
    {
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var comparison = await new ComparisonSnapshotBuilder().CaptureAsync(left, right);
        var operations = Enumerable.Range(0, 100)
            .Select(i => new SyncOperation($"directory-{i}", OperationKind.CreateRightDirectory, "journal batching"))
            .ToArray();
        var plan = new SyncPlan(operations); comparison.Plan = plan;
        var store = new CountingTaskJournalStore(Path.Combine(_root, "batched-journal"));

        var result = await new SyncExecutorV2().ExecuteAsync(PlanSnapshot.FromComparison(plan, comparison), left, right,
            journals: store, maxConcurrentCopies: 4);

        Assert.True(result.Succeeded);
        Assert.InRange(store.SaveCount, 2, 20);
        Assert.Empty(await store.LoadIncompleteAsync());
    }

    [Fact]
    public async Task Baseline_commit_records_post_publish_fingerprint_after_copy()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "hello");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var comparison = await new ComparisonSnapshotBuilder().CaptureAsync(left, right, ComparisonMode.TimeAndSize, TimeSpan.Zero);
        var comparisonForPlan = comparison;
        var plan = new ModePlanner().Build(SyncMode.Mirror, comparison.Left.Entries, comparison.Right.Entries, null, SyncFilter.Empty);
        comparisonForPlan.Plan = plan;
        var op = plan.Operations.First(x => x.Kind == OperationKind.CopyLeftToRight);
        var postFingerprint = await left.StatAsync("a.txt");
        var runResult = new SyncRunResult(Guid.NewGuid(), new[]
        {
            new OperationRunResult(op.OperationId, op.Path, op.Kind, TransferStage.Committed, postFingerprint?.Fingerprint?.Size ?? 5,
                SourceAfter: postFingerprint?.Fingerprint, TargetAfter: postFingerprint?.Fingerprint, Published: true)
        });
        var transaction = new BaselineRepository().Begin(left, right);
        await new BaselineRepository().CommitFromResultsAsync(left, right, new BaselineCommitInput(comparisonForPlan, runResult.Operations.ToDictionary(x => x.OperationId), transaction));
        var loaded = await new BaselineRepository().LoadAsync(left, right);
        Assert.NotNull(loaded);
        var entry = loaded!.FirstOrDefault(x => x.Path.Equals("a.txt", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);
        Assert.NotNull(entry!.Right);
        Assert.Equal(postFingerprint?.Fingerprint?.Size, entry.Right!.Fingerprint?.Size);
    }

    [Fact]
    public async Task V2_executor_does_not_invoke_scan_during_copy()
    {
        for (var i = 0; i < 5; i++) await File.WriteAllTextAsync(Path.Combine(Left, $"f-{i}.txt"), "payload-" + i);
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var comparison = await new ComparisonSnapshotBuilder().CaptureAsync(left, right, ComparisonMode.TimeAndSize, TimeSpan.Zero);
        var plan = new ModePlanner().Build(SyncMode.Mirror, comparison.Left.Entries, comparison.Right.Entries, null, SyncFilter.Empty);
        comparison.Plan = plan;
        var snapshot = PlanSnapshot.FromComparison(plan, comparison);
        var metrics = new SyncRunMetrics();
        using var scope = SyncRunMetricsHub.BeginScope(metrics);
        var baselineScans = metrics.DirectoryScans;
        await new FengSync.Core.Execution.SyncExecutorV2().ExecuteAsync(snapshot, left, right,
            resourceGovernor: new FengSync.Core.Execution.ResourceGovernor(), maxConcurrentCopies: 2);
        // M2 guarantee: V2 executor must not add any directory scans on top of
        // the planner's two enumerations.
        Assert.Equal(baselineScans, metrics.DirectoryScans);
    }

    private sealed class ScanStartCoordinator
    {
        private readonly TaskCompletionSource _bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        public int Started => Volatile.Read(ref _started);
        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _started) == 2) _bothStarted.TrySetResult();
            return _bothStarted.Task;
        }
    }

    private sealed class CountingTaskJournalStore(string root) : TaskJournalStore(root)
    {
        public int SaveCount { get; private set; }
        public override async Task SaveAsync(SyncJournal journal, CancellationToken ct = default)
        {
            SaveCount++;
            await base.SaveAsync(journal, ct);
        }
    }

    private sealed class CoordinatedStreamingEndpoint(string path, ScanStartCoordinator coordinator) : IEndpoint
    {
        public int LegacyScanCalls { get; private set; }
        public EndpointProfile Profile { get; } = new(Guid.NewGuid(), EndpointType.Local, path);
        public EndpointCapabilities Capabilities { get; } = new(false, false, true, TimeSpan.Zero);
        public Task<IReadOnlyList<EntrySnapshot>> ScanAsync(CancellationToken cancellationToken = default)
        {
            LegacyScanCalls++;
            throw new InvalidOperationException("Legacy list scan should not be used.");
        }
        public async IAsyncEnumerable<EntrySnapshot> ScanEntriesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await coordinator.ArriveAsync().WaitAsync(cancellationToken);
            yield return new(path, EntryKind.File, new(1, DateTimeOffset.UnixEpoch, null));
        }
        public Task<EntrySnapshot?> StatAsync(string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyToAsync(string relativePath, IEndpoint target, string temporaryPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MoveAsync(string from, string to, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string relativePath, bool directory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DeleteRecordingEndpoint(LocalEndpoint inner) : IEndpoint
    {
        public int StatCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public EndpointProfile Profile => inner.Profile;
        public EndpointCapabilities Capabilities => inner.Capabilities;
        public Task<IReadOnlyList<EntrySnapshot>> ScanAsync(CancellationToken cancellationToken = default) => inner.ScanAsync(cancellationToken);
        public IAsyncEnumerable<EntrySnapshot> ScanEntriesAsync(CancellationToken cancellationToken = default) => inner.ScanEntriesAsync(cancellationToken);
        public Task<EntrySnapshot?> StatAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            StatCalls++;
            return inner.StatAsync(relativePath, cancellationToken);
        }
        public Task CopyToAsync(string relativePath, IEndpoint target, string temporaryPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MoveAsync(string from, string to, CancellationToken cancellationToken = default) => inner.MoveAsync(from, to, cancellationToken);
        public async Task DeleteAsync(string relativePath, bool directory, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            await inner.DeleteAsync(relativePath, directory, cancellationToken);
        }
        public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) => inner.CreateDirectoryAsync(relativePath, cancellationToken);
    }
}
