using FengSync.Core;

namespace FengSync.Tests;

public sealed class NewFeatureIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-new-features-" + Guid.NewGuid().ToString("N"));
    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");
    private string Archive => Path.Combine(_root, "archive");
    public Task InitializeAsync() { Directory.CreateDirectory(Left); Directory.CreateDirectory(Right); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(_root)) Directory.Delete(_root, true); return Task.CompletedTask; }

    [Fact]
    public async Task Mirror_batch_blocks_empty_source_deletion()
    {
        await File.WriteAllTextAsync(Path.Combine(Right, "obsolete.txt"), "old");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProfileRunner().RunAsync(SyncProfile.Create("mirror", Left, Right) with { Mode = SyncMode.Mirror }));
        Assert.True(File.Exists(Path.Combine(Right, "obsolete.txt")));
    }

    [Fact]
    public async Task Filtered_profile_does_not_copy_or_delete_excluded_entries()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "keep.txt"), "keep");
        await File.WriteAllTextAsync(Path.Combine(Left, "skip.tmp"), "left");
        await File.WriteAllTextAsync(Path.Combine(Right, "skip.tmp"), "right");
        var profile = SyncProfile.Create("filtered", Left, Right) with { Mode = SyncMode.Mirror, Filter = new SyncFilter(Exclude: ["*.tmp"]) };
        var result = await new ProfileRunner().RunAsync(profile);
        Assert.Equal(1, result.Executed); Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(Right, "keep.txt"))); Assert.Equal("right", await File.ReadAllTextAsync(Path.Combine(Right, "skip.tmp")));
    }

    [Fact]
    public async Task Timestamped_archive_keeps_deleted_file_in_version_folder()
    {
        Directory.CreateDirectory(Path.Combine(Right, "history")); await File.WriteAllTextAsync(Path.Combine(Right, "history", "old.txt"), "preserve me");
        var plan = new SyncPlan([new SyncOperation("history/old.txt", OperationKind.DeleteRight, "test")]);
        await new LocalExecutor().ExecuteAsync(plan, new LocalEndpoint(Left), new LocalEndpoint(Right), versioning: new VersioningPolicy(VersioningMode.TimestampedArchive, Archive));
        Assert.False(File.Exists(Path.Combine(Right, "history", "old.txt")));
        var archived = Assert.Single(Directory.EnumerateFiles(Archive, "old.txt", SearchOption.AllDirectories));
        Assert.Equal("preserve me", await File.ReadAllTextAsync(archived));
    }

    [Fact]
    public async Task Archive_policy_requires_an_archive_path()
    {
        await File.WriteAllTextAsync(Path.Combine(Right, "old.txt"), "x");
        var plan = new SyncPlan([new SyncOperation("old.txt", OperationKind.DeleteRight, "test")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalExecutor().ExecuteAsync(plan, new LocalEndpoint(Left), new LocalEndpoint(Right), versioning: new VersioningPolicy(VersioningMode.TimestampedArchive)));
        Assert.True(File.Exists(Path.Combine(Right, "old.txt")));
    }

    [Fact]
    public async Task Two_way_batch_commits_a_baseline_after_execution()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "content");
        var result = await new ProfileRunner().RunAsync(SyncProfile.Create("two way", Left, Right));
        Assert.Equal(1, result.Executed); Assert.NotNull(await new BaselineStore().LoadAsync(new LocalEndpoint(Left), new LocalEndpoint(Right)));
    }

    [Fact]
    public async Task Disabled_profile_cannot_be_run()
    {
        var profile = SyncProfile.Create("disabled", Left, Right) with { Enabled = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProfileRunner().RunAsync(profile));
    }

    [Fact]
    public async Task Batch_runner_executes_multiple_profiles_in_parallel()
    {
        var left2 = Path.Combine(_root, "left2"); var right2 = Path.Combine(_root, "right2"); Directory.CreateDirectory(left2); Directory.CreateDirectory(right2);
        await File.WriteAllTextAsync(Path.Combine(Left, "first.txt"), "first"); await File.WriteAllTextAsync(Path.Combine(left2, "second.txt"), "second");
        var results = await new BatchRunner().RunAsync([SyncProfile.Create("one", Left, Right) with { Mode = SyncMode.Mirror }, SyncProfile.Create("two", left2, right2) with { Mode = SyncMode.Mirror }]);
        Assert.Equal(2, results.Count); Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(Right, "first.txt"))); Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(right2, "second.txt")));
    }

    [Fact]
    public async Task Endpoint_neutral_synchronizer_executes_local_endpoints()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "portable.txt"), "portable");
        var plan = await new EndpointSynchronizer().SynchronizeAsync(new LocalEndpoint(Left), new LocalEndpoint(Right), SyncMode.Update);
        Assert.Equal(OperationKind.CopyLeftToRight, Assert.Single(plan.Operations).Kind);
        Assert.Equal("portable", await File.ReadAllTextAsync(Path.Combine(Right, "portable.txt")));
    }

    [Fact]
    public async Task Realtime_monitor_debounces_local_file_changes()
    {
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new RealtimeMonitor(Left, Right, () => fired.TrySetResult(), TimeSpan.FromMilliseconds(80));
        await File.WriteAllTextAsync(Path.Combine(Left, "watch.txt"), "changed");
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }
}
