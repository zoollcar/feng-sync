using FengSync.Core;

namespace FengSync.Tests;

public sealed class LocalIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-test-" + Guid.NewGuid().ToString("N"));
    private string Left => Path.Combine(_root, "left"); private string Right => Path.Combine(_root, "right"); private string Jobs => Path.Combine(_root, "jobs");
    public Task InitializeAsync() { Directory.CreateDirectory(Left); Directory.CreateDirectory(Right); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(_root)) Directory.Delete(_root, true); return Task.CompletedTask; }

    [Fact] public async Task Copy_executes_through_a_temporary_file_and_commits_journal()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "hello"); var l = new LocalEndpoint(Left); var r = new LocalEndpoint(Right);
        var plan = new ThreeWayPlanner().Build(l.Scan(), r.Scan(), null); await new LocalExecutor().ExecuteAsync(plan, l, r, journals: new TaskJournalStore(Jobs));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(Right, "a.txt"))); Assert.Empty(Directory.EnumerateFiles(Right, "*.partial", SearchOption.AllDirectories)); Assert.Empty(await new TaskJournalStore(Jobs).LoadIncompleteAsync());
    }
    [Fact] public async Task Multiple_files_can_be_copied_with_configured_concurrency()
    {
        for (var i = 0; i < 8; i++) await File.WriteAllTextAsync(Path.Combine(Left, $"file-{i}.txt"), new string((char)('a' + i), 2048));
        var l = new LocalEndpoint(Left); var r = new LocalEndpoint(Right); var plan = new ThreeWayPlanner().Build(l.Scan(), r.Scan(), null);
        await new LocalExecutor().ExecuteAsync(plan, l, r, maxConcurrentCopies: 3);
        Assert.Equal(8, Directory.EnumerateFiles(Right, "*.txt").Count());
    }
    [Fact] public async Task One_hundred_folders_with_one_file_each_are_compared_and_copied()
    {
        for (var i = 1; i <= 100; i++)
        {
            var folder = Path.Combine(Left, $"folder-{i:D3}"); Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "item.txt"), $"fixture {i}");
        }
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var plan = new ThreeWayPlanner().Build(left.Scan(), right.Scan(), null);
        await new LocalExecutor().ExecuteAsync(plan, left, right, maxConcurrentCopies: 8);
        Assert.Equal(100, Directory.EnumerateFiles(Right, "item.txt", SearchOption.AllDirectories).Count());
        Assert.Equal("fixture 100", await File.ReadAllTextAsync(Path.Combine(Right, "folder-100", "item.txt")));
    }
    [Fact] public async Task One_hundred_flat_files_are_compared_and_copied()
    {
        for (var i = 1; i <= 100; i++) await File.WriteAllTextAsync(Path.Combine(Left, $"file-{i:D3}.txt"), $"fixture {i}");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        var plan = new ThreeWayPlanner().Build(left.Scan(), right.Scan(), null);
        await new LocalExecutor().ExecuteAsync(plan, left, right, maxConcurrentCopies: 8);
        Assert.Equal(100, Directory.EnumerateFiles(Right, "*.txt").Count());
        Assert.Equal("fixture 100", await File.ReadAllTextAsync(Path.Combine(Right, "file-100.txt")));
    }
    [Fact] public async Task Baseline_round_trips_as_identical_sqlite_files()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "hello"); await File.WriteAllTextAsync(Path.Combine(Right, "a.txt"), "hello"); var l = new LocalEndpoint(Left); var r = new LocalEndpoint(Right); var store = new BaselineStore();
        await store.CommitAsync(l, r); var loaded = await store.LoadAsync(l, r);
        Assert.Single(loaded!); Assert.Equal("a.txt", loaded![0].Path); Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(Left, "sync.fengdb")), await File.ReadAllBytesAsync(Path.Combine(Right, "sync.fengdb")));
    }
    [Fact] public async Task One_sided_database_is_a_safety_error()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "sync.fengdb"), "not a database");
        await Assert.ThrowsAsync<InvalidDataException>(() => new BaselineStore().LoadAsync(new LocalEndpoint(Left), new LocalEndpoint(Right)));
    }
    [Fact] public void Scanner_excludes_internal_database_and_partial_files()
    {
        File.WriteAllText(Path.Combine(Left, "sync.fengdb"), "x"); File.WriteAllText(Path.Combine(Left, "a.fengsync-x.partial"), "x"); File.WriteAllText(Path.Combine(Left, "real.txt"), "x");
        Assert.Single(new LocalEndpoint(Left).Scan());
    }
    [Fact] public async Task Paired_endpoint_archive_uses_opposite_fragments_and_survives_side_swap()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(Right, "a.txt"), "same");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right); var store = new EndpointBaselineStore();
        await store.CommitAsync(left, right);
        Assert.NotEqual(await File.ReadAllBytesAsync(Path.Combine(Left, "sync.fengdb")), await File.ReadAllBytesAsync(Path.Combine(Right, "sync.fengdb")));
        Assert.Single((await store.LoadAsync(left, right))!);
        Assert.Single((await store.LoadAsync(right, left))!);
    }
    [Fact] public async Task Lone_paired_archive_is_not_a_deletion_baseline()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(Right, "a.txt"), "same");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right); var store = new EndpointBaselineStore();
        await store.CommitAsync(left, right); File.Delete(Path.Combine(Right, "sync.fengdb"));
        Assert.Null(await store.LoadAsync(left, right));
    }
    [Fact] public async Task Legacy_state_is_reported_as_untrusted_and_rebuilt_on_commit()
    {
        await File.WriteAllTextAsync(Path.Combine(Left, "a.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(Right, "a.txt"), "same");
        var left = new LocalEndpoint(Left); var right = new LocalEndpoint(Right);
        await new BaselineStore().CommitAsync(left, right); // previous single-snapshot format
        var store = new EndpointBaselineStore();
        Assert.Null(await store.LoadAsync(left, right));
        Assert.NotNull(store.LastLoadWarning);
        await store.CommitAsync(left, right);
        Assert.Single((await store.LoadAsync(left, right))!);
    }
}
