using FengSync.Core;

namespace FengSync.Tests;

public sealed class MoveDetectionTests
{
    private static EntrySnapshot File(string path, string text, string? id = null) =>
        new(path, EntryKind.File, new(text.Length, DateTimeOffset.UnixEpoch, null), id is null ? null : new EntryIdentity(StableObjectId: id));

    [Fact]
    public void Stable_id_matches_only_within_one_endpoint()
    {
        var moves = new EndpointMoveDetector().Detect(EndpointSide.Left,
            [new BaselineEntry("old.txt", File("old.txt", "x", "local:1"), File("old.txt", "x", "remote:1"))],
            [File("new.txt", "x", "local:1")]);
        var move = Assert.Single(moves);
        Assert.Equal(("old.txt", "new.txt", MoveConfidence.Certain), (move.OldPath, move.NewPath, move.Confidence));
    }

    [Fact]
    public void Stable_id_match_is_not_suppressed_by_large_weak_candidate_bucket()
    {
        var current = Enumerable.Range(0, 17).Select(i => File($"new-{i}.txt", "x", i == 16 ? "local:1" : $"local:{i + 2}")).ToList();
        var moves = new EndpointMoveDetector().Detect(EndpointSide.Left,
            [new BaselineEntry("old.txt", File("old.txt", "x", "local:1"), null)], current);
        Assert.Equal("new-16.txt", Assert.Single(moves).NewPath);
    }

    [Fact]
    public void Case_sensitive_paths_do_not_merge_case_distinct_objects()
    {
        var plan = new ModePlanner().Build(SyncMode.Update,
            [File("A.txt", "a"), File("a.txt", "b")], [], null,
            leftCapabilities: new(false, false, false, TimeSpan.Zero, Paths: new EndpointPathSemantics(true, System.Text.NormalizationForm.FormC)),
            rightCapabilities: new(false, false, false, TimeSpan.Zero, Paths: new EndpointPathSemantics(true, System.Text.NormalizationForm.FormC)));
        Assert.Equal(2, plan.Operations.Count(x => x.Kind == OperationKind.CopyLeftToRight));
    }

    [Fact]
    public void Case_only_rename_is_not_lost_by_path_indexing()
    {
        var moves = new EndpointMoveDetector().Detect(EndpointSide.Left,
            [new BaselineEntry("Foo.txt", File("Foo.txt", "x", "1"), File("Foo.txt", "x"))],
            [File("foo.txt", "x", "1")]);
        Assert.Equal("foo.txt", Assert.Single(moves).NewPath);
    }

    [Fact]
    public void Two_way_left_move_becomes_target_side_move()
    {
        var baseline = new[] { new BaselineEntry("old.txt", File("old.txt", "x", "l1"), File("old.txt", "x", "r1")) };
        var plan = new ModePlanner().Build(SyncMode.TwoWay, [File("new.txt", "x", "l1")], [File("old.txt", "x", "r1")], baseline,
            leftCapabilities: new(true, true, true, TimeSpan.Zero), rightCapabilities: new(true, true, true, TimeSpan.Zero));
        var operation = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.Move, operation.Kind);
        Assert.Equal((EndpointSide.Left, EndpointSide.Right, "old.txt", "new.txt"),
            (operation.Move!.ChangedOn, operation.Move.ExecuteOn, operation.Move.FromPath, operation.Move.ToPath));
    }

    [Fact]
    public async Task Move_executor_renames_the_planned_target_only_after_source_freshness_check()
    {
        var root = Path.Combine(Path.GetTempPath(), "fengsync-move-" + Guid.NewGuid().ToString("N"));
        var leftRoot = Path.Combine(root, "left"); var rightRoot = Path.Combine(root, "right");
        try
        {
            Directory.CreateDirectory(leftRoot); Directory.CreateDirectory(rightRoot);
            await System.IO.File.WriteAllTextAsync(Path.Combine(leftRoot, "new.txt"), "contents");
            await System.IO.File.WriteAllTextAsync(Path.Combine(rightRoot, "old.txt"), "contents");
            var left = new LocalEndpoint(leftRoot); var right = new LocalEndpoint(rightRoot);
            var plan = new SyncPlan([new SyncOperation("new.txt", OperationKind.Move, "test", move: new(EndpointSide.Left, EndpointSide.Right, "old.txt", "new.txt", EntryKind.File, IdentityEvidenceKind.StrongDigest, MoveConfidence.High, EndpointMoveExecution.NativeRename, MoveFallback.CrossEndpointCopyDelete))]);
            var snapshot = await PlanSnapshot.CaptureAsync(plan, left, right);
            var result = await new FengSync.Core.Execution.SyncExecutorV2().ExecuteAsync(snapshot, left, right);
            Assert.True(result.Succeeded);
            Assert.False(System.IO.File.Exists(Path.Combine(rightRoot, "old.txt")));
            Assert.Equal("contents", await System.IO.File.ReadAllTextAsync(Path.Combine(rightRoot, "new.txt")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Move_executor_refuses_a_replaced_execute_endpoint_source()
    {
        var root = Path.Combine(Path.GetTempPath(), "fengsync-move-stale-" + Guid.NewGuid().ToString("N"));
        var leftRoot = Path.Combine(root, "left"); var rightRoot = Path.Combine(root, "right");
        try
        {
            Directory.CreateDirectory(leftRoot); Directory.CreateDirectory(rightRoot);
            await System.IO.File.WriteAllTextAsync(Path.Combine(leftRoot, "new.txt"), "contents");
            await System.IO.File.WriteAllTextAsync(Path.Combine(rightRoot, "old.txt"), "contents");
            var left = new LocalEndpoint(leftRoot); var right = new LocalEndpoint(rightRoot);
            var plan = new SyncPlan([new SyncOperation("new.txt", OperationKind.Move, "test", move: new(EndpointSide.Left, EndpointSide.Right, "old.txt", "new.txt", EntryKind.File, IdentityEvidenceKind.StrongDigest, MoveConfidence.High, EndpointMoveExecution.NativeRename, MoveFallback.CrossEndpointCopyDelete))]);
            var snapshot = await PlanSnapshot.CaptureAsync(plan, left, right);
            await System.IO.File.WriteAllTextAsync(Path.Combine(rightRoot, "old.txt"), "replaced-with-different-size");
            var result = await new FengSync.Core.Execution.SyncExecutorV2().ExecuteAsync(snapshot, left, right);
            Assert.False(result.Succeeded);
            Assert.True(System.IO.File.Exists(Path.Combine(rightRoot, "old.txt")));
            Assert.False(System.IO.File.Exists(Path.Combine(rightRoot, "new.txt")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Case_sensitive_snapshot_and_plan_snapshot_keep_both_case_variants()
    {
        var left = new CaseSensitiveEndpoint([File("A.txt", "a"), File("a.txt", "bb")]);
        var right = new CaseSensitiveEndpoint([]);
        var endpointSnapshot = await FengSync.Core.Scanning.EndpointSnapshotCapture.CaptureAsync(left);
        Assert.Equal(2, endpointSnapshot.ByPath.Count);
        Assert.True(endpointSnapshot.ByPath.ContainsKey("A.txt"));
        Assert.True(endpointSnapshot.ByPath.ContainsKey("a.txt"));

        var plan = new SyncPlan([
            new SyncOperation("A.txt", OperationKind.CopyLeftToRight, "test"),
            new SyncOperation("a.txt", OperationKind.CopyLeftToRight, "test")]);
        var planSnapshot = await PlanSnapshot.CaptureAsync(plan, left, right);
        Assert.All(plan.Operations, operation => Assert.NotNull(planSnapshot.SourceFingerprints[operation.OperationId]));
    }

    [Fact]
    public void Same_destination_moves_produce_an_internal_rekey_operation()
    {
        var baseline = new[] { new BaselineEntry("old.txt", File("old.txt", "x", "l1"), File("old.txt", "x", "r1")) };
        var plan = new ModePlanner().Build(SyncMode.TwoWay, [File("new.txt", "x", "l1")], [File("new.txt", "x", "r1")], baseline,
            leftCapabilities: new(false, true, true, TimeSpan.Zero), rightCapabilities: new(false, true, true, TimeSpan.Zero));
        var operation = Assert.Single(plan.Operations);
        Assert.Equal(OperationKind.Move, operation.Kind);
        Assert.True(operation.Selected);
        Assert.Equal(("old.txt", "new.txt"), (operation.Move!.FromPath, operation.Move.ToPath));
    }

    private sealed class CaseSensitiveEndpoint(IReadOnlyList<EntrySnapshot> entries) : IEndpoint
    {
        public EndpointProfile Profile { get; } = new(Guid.NewGuid(), EndpointType.S3, "test", "test");
        public EndpointCapabilities Capabilities { get; } = new(false, false, false, TimeSpan.Zero,
            Paths: new EndpointPathSemantics(true, System.Text.NormalizationForm.FormC));
        public Task<IReadOnlyList<EntrySnapshot>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult(entries);
        public Task<EntrySnapshot?> StatAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(entries.SingleOrDefault(x => x.Path == relativePath));
        public Task CopyToAsync(string relativePath, IEndpoint target, string temporaryPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MoveAsync(string from, string to, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string relativePath, bool directory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public void Divergent_moves_are_a_conflict()
    {
        var baseline = new[] { new BaselineEntry("old.txt", File("old.txt", "x", "l1"), File("old.txt", "x", "r1")) };
        var plan = new ModePlanner().Build(SyncMode.TwoWay, [File("left.txt", "x", "l1")], [File("right.txt", "x", "r1")], baseline);
        Assert.Equal(OperationKind.MoveConflict, Assert.Single(plan.Operations).Kind);
    }
}
