using FengSync.Core;
using FengSync.Core.Execution;

namespace FengSync.Tests;

public sealed class DirectoryMoveAggregationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-dir-move-" + Guid.NewGuid().ToString("N"));
    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Left);
        Directory.CreateDirectory(Right);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Planner_generated_directory_rename_is_aggregated()
    {
        await CreateMatchingTreesAsync(includeSecondFile: true);
        var stamp = DateTimeOffset.UtcNow;
        EntrySnapshot FileEntry(string path, string id, int size) =>
            new(path, EntryKind.File, new(size, stamp, null), new EntryIdentity(StableObjectId: id));
        EntrySnapshot DirectoryEntry(string path) => new(path, EntryKind.Directory, null);
        var baseline = new[]
        {
            new BaselineEntry("old-dir", DirectoryEntry("old-dir"), DirectoryEntry("old-dir")),
            new BaselineEntry("old-dir/sub", DirectoryEntry("old-dir/sub"), DirectoryEntry("old-dir/sub")),
            new BaselineEntry("old-dir/a.txt", FileEntry("old-dir/a.txt", "left-a", 5), FileEntry("old-dir/a.txt", "right-a", 5)),
            new BaselineEntry("old-dir/sub/b.txt", FileEntry("old-dir/sub/b.txt", "left-b", 5), FileEntry("old-dir/sub/b.txt", "right-b", 5))
        };
        var currentLeft = new[]
        {
            DirectoryEntry("new-dir"), DirectoryEntry("new-dir/sub"),
            FileEntry("new-dir/a.txt", "left-a", 5), FileEntry("new-dir/sub/b.txt", "left-b", 5)
        };
        var currentRight = new[]
        {
            DirectoryEntry("old-dir"), DirectoryEntry("old-dir/sub"),
            FileEntry("old-dir/a.txt", "right-a", 5), FileEntry("old-dir/sub/b.txt", "right-b", 5)
        };
        var left = new RecordingEndpoint(new LocalEndpoint(Left));
        var right = new RecordingEndpoint(new LocalEndpoint(Right));
        var plan = new ModePlanner().Build(SyncMode.TwoWay, currentLeft, currentRight, baseline,
            leftCapabilities: left.Capabilities, rightCapabilities: right.Capabilities);

        Assert.Equal(2, plan.Operations.Count(x => x.Kind == OperationKind.Move && x.Selected));
        var result = await new SyncExecutorV2().ExecuteAsync(await PlanSnapshot.CaptureAsync(plan, left, right), left, right);

        Assert.True(result.Succeeded, Errors(result));
        Assert.Equal(1, right.DirectoryMoves);
        Assert.Equal(0, right.FileMoves);
        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(Right, "new-dir", "a.txt")));
        Assert.Equal("bravo", await File.ReadAllTextAsync(Path.Combine(Right, "new-dir", "sub", "b.txt")));
    }

    [Fact]
    public async Task Complete_selected_subtree_is_renamed_once_and_every_logical_operation_is_committed()
    {
        await CreateMatchingTreesAsync(includeSecondFile: true);
        Directory.CreateDirectory(Path.Combine(Left, "new-dir", "empty"));
        Directory.CreateDirectory(Path.Combine(Right, "old-dir", "empty"));
        var plan = BuildPlan(includeSecondMove: true, selectSecondMove: true, includeEmptyDirectory: true);
        var left = new RecordingEndpoint(new LocalEndpoint(Left));
        var right = new RecordingEndpoint(new LocalEndpoint(Right));

        var result = await new SyncExecutorV2().ExecuteAsync(await PlanSnapshot.CaptureAsync(plan, left, right), left, right);

        Assert.True(result.Succeeded, Errors(result));
        Assert.Equal(1, right.DirectoryMoves);
        Assert.Equal(0, right.FileMoves);
        Assert.Equal(plan.Operations.Count, result.SucceededOperations);
        Assert.False(Directory.Exists(Path.Combine(Right, "old-dir")));
        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(Right, "new-dir", "a.txt")));
        Assert.Equal("bravo", await File.ReadAllTextAsync(Path.Combine(Right, "new-dir", "sub", "b.txt")));
        Assert.True(Directory.Exists(Path.Combine(Right, "new-dir", "empty")));
    }

    [Fact]
    public async Task Deselected_file_disables_directory_aggregation_and_preserves_that_old_file()
    {
        await CreateMatchingTreesAsync(includeSecondFile: true);
        var plan = BuildPlan(includeSecondMove: true, selectSecondMove: false);
        var left = new RecordingEndpoint(new LocalEndpoint(Left));
        var right = new RecordingEndpoint(new LocalEndpoint(Right));

        var result = await new SyncExecutorV2().ExecuteAsync(await PlanSnapshot.CaptureAsync(plan, left, right), left, right);

        Assert.True(result.Succeeded, Errors(result));
        Assert.Equal(0, right.DirectoryMoves);
        Assert.Equal(1, right.FileMoves);
        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(Right, "new-dir", "a.txt")));
        Assert.True(File.Exists(Path.Combine(Right, "old-dir", "sub", "b.txt")));
    }

    [Fact]
    public async Task Native_directory_not_supported_falls_back_to_selected_file_moves()
    {
        await CreateMatchingTreesAsync(includeSecondFile: true);
        var plan = BuildPlan(includeSecondMove: true, selectSecondMove: true);
        var left = new RecordingEndpoint(new LocalEndpoint(Left));
        var right = new RecordingEndpoint(new LocalEndpoint(Right), rejectDirectoryMove: true);

        var result = await new SyncExecutorV2().ExecuteAsync(await PlanSnapshot.CaptureAsync(plan, left, right), left, right);

        Assert.True(result.Succeeded, Errors(result));
        Assert.Equal(1, right.DirectoryMoveAttempts);
        Assert.Equal(0, right.DirectoryMoves);
        Assert.Equal(2, right.FileMoves);
        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(Right, "new-dir", "a.txt")));
        Assert.Equal("bravo", await File.ReadAllTextAsync(Path.Combine(Right, "new-dir", "sub", "b.txt")));
    }

    private async Task CreateMatchingTreesAsync(bool includeSecondFile)
    {
        Directory.CreateDirectory(Path.Combine(Left, "new-dir", "sub"));
        Directory.CreateDirectory(Path.Combine(Right, "old-dir", "sub"));
        await File.WriteAllTextAsync(Path.Combine(Left, "new-dir", "a.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(Right, "old-dir", "a.txt"), "alpha");
        if (!includeSecondFile) return;
        await File.WriteAllTextAsync(Path.Combine(Left, "new-dir", "sub", "b.txt"), "bravo");
        await File.WriteAllTextAsync(Path.Combine(Right, "old-dir", "sub", "b.txt"), "bravo");
    }

    private static SyncPlan BuildPlan(bool includeSecondMove, bool selectSecondMove, bool includeEmptyDirectory = false)
    {
        var operations = new List<SyncOperation>
        {
            new("new-dir", OperationKind.CreateRightDirectory, "directory rename"),
            new("new-dir/sub", OperationKind.CreateRightDirectory, "directory rename"),
            new("old-dir/sub", OperationKind.DeleteRight, "directory rename"),
            new("old-dir", OperationKind.DeleteRight, "directory rename"),
            Move("old-dir/a.txt", "new-dir/a.txt")
        };
        if (includeEmptyDirectory)
        {
            operations.Add(new("new-dir/empty", OperationKind.CreateRightDirectory, "directory rename"));
            operations.Add(new("old-dir/empty", OperationKind.DeleteRight, "directory rename"));
        }
        if (includeSecondMove)
        {
            var second = Move("old-dir/sub/b.txt", "new-dir/sub/b.txt");
            second.Selected = selectSecondMove;
            operations.Add(second);
        }
        return new(operations);
    }

    private static SyncOperation Move(string from, string to) =>
        new(to, OperationKind.Move, "directory rename", move: new(
            EndpointSide.Left, EndpointSide.Right, from, to, EntryKind.File,
            IdentityEvidenceKind.WeakFingerprint, MoveConfidence.High,
            EndpointMoveExecution.NativeRename, MoveFallback.CrossEndpointCopyDelete));

    private static string Errors(SyncRunResult result) =>
        string.Join(Environment.NewLine, result.Operations.Select(x => $"{x.Kind} {x.Path}: {x.Error}"));

    private sealed class RecordingEndpoint(LocalEndpoint inner, bool rejectDirectoryMove = false) : IEndpoint
    {
        public int DirectoryMoveAttempts { get; private set; }
        public int DirectoryMoves { get; private set; }
        public int FileMoves { get; private set; }
        public EndpointProfile Profile => inner.Profile;
        public EndpointCapabilities Capabilities => inner.Capabilities;
        public Task<IReadOnlyList<EntrySnapshot>> ScanAsync(CancellationToken cancellationToken = default) =>
            inner.ScanAsync(cancellationToken);
        public Task<EntrySnapshot?> StatAsync(string relativePath, CancellationToken cancellationToken = default) =>
            inner.StatAsync(relativePath, cancellationToken);
        public Task CopyToAsync(string relativePath, IEndpoint target, string temporaryPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async Task MoveAsync(string from, string to, CancellationToken cancellationToken = default)
        {
            FileMoves++;
            await inner.MoveAsync(from, to, cancellationToken);
        }
        public async Task MoveDirectoryAsync(string from, string to, CancellationToken cancellationToken = default)
        {
            DirectoryMoveAttempts++;
            if (rejectDirectoryMove) throw new NotSupportedException("probe rejected");
            await inner.MoveDirectoryAsync(from, to, cancellationToken);
            DirectoryMoves++;
        }
        public Task DeleteAsync(string relativePath, bool directory, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(relativePath, directory, cancellationToken);
        public Task CreateDirectoryAsync(string relativePath, CancellationToken cancellationToken = default) =>
            inner.CreateDirectoryAsync(relativePath, cancellationToken);
    }
}
