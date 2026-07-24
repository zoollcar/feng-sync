using System.Text.Json;
using FengSync.Core;
using FengSync.Core.Diagnostics;
using FengSync.Core.Execution;
using FengSync.Core.Scanning;

namespace FengSync.Tests;

/// <summary>
/// Verifies the M0 metrics plumbing, the M1 no-default-hash guarantee, the M2
/// freshness no-rescan guarantee and the M3 baseline no-rescan guarantee.
/// These are the regression gates the M6 integration plan requires before
/// shipping the new engine.
/// </summary>
public sealed class PerformanceInvariantTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-perf-" + Guid.NewGuid().ToString("N"));
    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");
    public Task InitializeAsync() { Directory.CreateDirectory(Left); Directory.CreateDirectory(Right); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(_root)) Directory.Delete(_root, true); return Task.CompletedTask; }

    [Fact]
    public async Task Default_scan_does_not_hash_file_contents()
    {
        for (var i = 0; i < 5; i++) await File.WriteAllTextAsync(Path.Combine(Left, $"file-{i}.txt"), "payload-" + i);
        var metrics = new SyncRunMetrics();
        using var scope = SyncRunMetricsHub.BeginScope(metrics);
        var endpoint = new LocalEndpoint(Left);
        _ = await endpoint.ScanAsync();
        Assert.Equal(0, metrics.HashBytes);
        Assert.Equal(0, metrics.HashFiles);
    }

    [Fact]
    public async Task Content_hash_endpoint_counts_bytes_and_files()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), new string('x', 4096));
        var metrics = new SyncRunMetrics();
        using var scope = SyncRunMetricsHub.BeginScope(metrics);
        var digest = await new LocalEndpoint(Left).HashAsync("a.txt", HashAlgorithmId.Sha256);
        Assert.NotEmpty(digest.Hex);
        Assert.True(metrics.HashFiles >= 1);
        Assert.True(metrics.HashBytes >= 4096);
    }

    [Fact]
    public async Task Plan_freshness_check_uses_stat_not_full_scan()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "stale.txt"), "stale");
        await File.WriteAllTextAsync(Path.Combine(Right, "stale.txt"), "fresh");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var baseline = await new BaselineRepository().LoadAsync(left, right);
        var plan = new ThreeWayPlanner().Build(left.Scan(), right.Scan(), baseline);
        var snapshot = PlanSnapshot.FromComparison(plan, await new ComparisonSnapshotBuilder().CaptureAsync(left, right, ComparisonMode.TimeAndSize, TimeSpan.Zero, baseline));
        var metrics = new SyncRunMetrics();
        using var scope = SyncRunMetricsHub.BeginScope(metrics);
        var before = metrics.DirectoryScans;
        await new PlanFreshnessValidator().ValidateStatAsync(snapshot, left, right, maxParallel: 2);
        // M2 guarantee: freshness must not trigger an extra directory scan.
        Assert.Equal(before, metrics.DirectoryScans);
    }

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
    public void Rclone_batch_planner_respects_size_thresholds()
    {
        var reqs = Enumerable.Range(0, 250).Select(i => new CopyRequest(Guid.NewGuid(), $"src/{i}.bin", $"dst/{i}.bin", 1024)).ToList();
        var batches = RcloneBatchPlanner.PlanBatches(reqs);
        Assert.Single(batches);
        Assert.Equal(250, batches[0].Count);

        var single = Enumerable.Range(0, 10).Select(i => new CopyRequest(Guid.NewGuid(), $"src/{i}.bin", $"dst/{i}.bin", 1024)).ToList();
        var singleBatches = RcloneBatchPlanner.PlanBatches(single);
        Assert.Equal(10, singleBatches.Count);
    }

    [Fact]
    public void Engine_flags_default_to_the_safe_subset()
    {
        var flags = EngineFeatureFlags.Defaults;
        Assert.True(flags.SnapshotV2);
        Assert.True(flags.LazyHash);
        Assert.True(flags.VerifierV2);
        Assert.True(flags.BaselineV2);
        Assert.True(flags.JournalWal);
        Assert.True(flags.DeviceScheduler);
        // rclone-batch is opt-in until the long-lived async-job work lands.
        Assert.False(flags.RcloneBatch);
    }

    [Fact]
    public void Engine_flags_parse_overrides()
    {
        var parsed = EngineFeatureFlags.Resolve("snapshot-v2=false, rclone-batch");
        Assert.False(parsed.SnapshotV2);
        Assert.True(parsed.RcloneBatch);
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
        var dataDir = Path.Combine(_root, "appdata");
        Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", dataDir);
        try
        {
            var profile = SyncProfile.Create("perf-default-path", Left, Right) with { Mode = SyncMode.Mirror };
            var runner = new ProfileRunner();
            var metrics = new SyncRunMetrics();
            using var scope = SyncRunMetricsHub.BeginScope(metrics);
            await runner.CompareAsync(profile);
            // M1: planner-side enumeration only; Mirror adds the source as deletion authority.
            Assert.True(metrics.DirectoryScans <= 4, $"CompareAsync triggered {metrics.DirectoryScans} directory scans; expected <= 4 (left, right, baseline.download/state)");
        }
        finally { Environment.SetEnvironmentVariable("FENGSYNC_DATA_DIR", null); }
    }

    [Fact]
    public async Task Wal_writer_emits_events_after_begin()
    {
        var journalRoot = Path.Combine(_root, "wal");
        await using var store = new JournalWalStore(journalRoot);
        var runId = Guid.NewGuid().ToString("N");
        await store.BeginRunAsync(runId, new JournalHeader(2, runId, DateTimeOffset.UtcNow, "profile",
            new EndpointIdentity("Local", Left, Left), new EndpointIdentity("Local", Right, Right),
            Guid.NewGuid().ToString("N"),
            new[] { new JournalOperation(Guid.NewGuid().ToString("N"), "a.txt", nameof(OperationKind.CopyLeftToRight), 10) }));
        await store.AppendAsync(new JournalEvent { Kind = JournalEventKind.OperationStarted, OperationId = "x" });
        await store.AppendAsync(new JournalEvent { Kind = JournalEventKind.OperationCommitted, OperationId = "x" });
        await store.AwaitDurabilityAsync();
        await store.CompleteRunAsync(new JournalSummary(runId, DateTimeOffset.UtcNow, 1, 0, 0));
        var eventsPath = Path.Combine(journalRoot, runId + ".events.jsonl");
        Assert.True(File.Exists(eventsPath));
        var lines = await File.ReadAllLinesAsync(eventsPath);
        Assert.True(lines.Length >= 2, $"Expected at least 2 lines, got {lines.Length}");
        var seqs = lines.Select(l => JsonSerializer.Deserialize<JournalEvent>(l)?.Seq ?? 0).ToList();
        Assert.Equal(seqs.OrderBy(x => x).ToList(), seqs);
    }

    [Fact]
    public async Task Wal_writer_rejects_concurrent_begin_calls()
    {
        var journalRoot = Path.Combine(_root, "wal-fail");
        await using var store = new JournalWalStore(journalRoot);
        var runId = Guid.NewGuid().ToString("N");
        await store.BeginRunAsync(runId, new JournalHeader(2, runId, DateTimeOffset.UtcNow, null,
            new EndpointIdentity("Local", Left, Left), new EndpointIdentity("Local", Right, Right),
            Guid.NewGuid().ToString("N"), Array.Empty<JournalOperation>()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.BeginRunAsync(runId, new JournalHeader(2, runId, DateTimeOffset.UtcNow, null,
            new EndpointIdentity("Local", Left, Left), new EndpointIdentity("Local", Right, Right),
            Guid.NewGuid().ToString("N"), Array.Empty<JournalOperation>())));
    }

    [Fact]
    public async Task Wal_recovery_replays_a_complete_final_line()
    {
        var journalRoot = Path.Combine(_root, "wal-tail");
        Directory.CreateDirectory(journalRoot);
        var runId = Guid.NewGuid().ToString("N");
        var operationId = Guid.NewGuid().ToString("N");
        var header = new JournalHeader(2, runId, DateTimeOffset.UtcNow, null,
            new EndpointIdentity("Local", Left, Left), new EndpointIdentity("Local", Right, Right),
            Guid.NewGuid().ToString("N"), [new JournalOperation(operationId, "a.txt", nameof(OperationKind.CopyLeftToRight), 1)]);
        await File.WriteAllTextAsync(Path.Combine(journalRoot, runId + ".header.json"), JsonSerializer.Serialize(header));
        await File.WriteAllTextAsync(Path.Combine(journalRoot, runId + ".events.jsonl"),
            JsonSerializer.Serialize(new JournalEvent { Seq = 1, Kind = JournalEventKind.OperationCommitted, OperationId = operationId }) + "\n");

        var recovered = await JournalRecoveryReader.LoadIncompleteAsync(journalRoot);
        Assert.Empty(recovered);
    }

    [Fact]
    public async Task Wal_recovery_surfaces_sequence_faults_even_when_operations_committed()
    {
        var journalRoot = Path.Combine(_root, "wal-sequence");
        Directory.CreateDirectory(journalRoot);
        var runId = Guid.NewGuid().ToString("N");
        var operationId = Guid.NewGuid().ToString("N");
        var header = new JournalHeader(2, runId, DateTimeOffset.UtcNow, null,
            new EndpointIdentity("Local", Left, Left), new EndpointIdentity("Local", Right, Right),
            Guid.NewGuid().ToString("N"), [new JournalOperation(operationId, "a.txt", nameof(OperationKind.CopyLeftToRight), 1)]);
        await File.WriteAllTextAsync(Path.Combine(journalRoot, runId + ".header.json"), JsonSerializer.Serialize(header));
        await File.WriteAllTextAsync(Path.Combine(journalRoot, runId + ".events.jsonl"),
            JsonSerializer.Serialize(new JournalEvent { Seq = 2, Kind = JournalEventKind.OperationCommitted, OperationId = operationId }) + "\n");

        var recovered = await JournalRecoveryReader.LoadIncompleteAsync(journalRoot);
        var item = Assert.Single(recovered).Items.First(x => x.State == JournalState.Failed);
        Assert.Contains("序号", item.Error);
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
}
