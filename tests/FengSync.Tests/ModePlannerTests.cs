using FengSync.Core;

namespace FengSync.Tests;

public sealed class ModePlannerTests
{
    private static EntrySnapshot File(string path, string contents) => new(path, EntryKind.File, new(contents.Length, DateTimeOffset.UnixEpoch, contents));
    [Fact] public void Mirror_removes_destination_only_file() =>
        Assert.Equal(OperationKind.DeleteRight, Assert.Single(new ModePlanner().Build(SyncMode.Mirror, [], [File("old.txt", "x")]).Operations).Kind);
    [Fact] public void Update_preserves_destination_only_file() =>
        Assert.Empty(new ModePlanner().Build(SyncMode.Update, [], [File("old.txt", "x")]).Operations);
    [Fact] public void Update_copies_changed_left_file() =>
        Assert.Equal(OperationKind.CopyLeftToRight, Assert.Single(new ModePlanner().Build(SyncMode.Update, [File("a.txt", "new")], [File("a.txt", "old")]).Operations).Kind);
    [Fact] public void Filter_excludes_planned_path() =>
        Assert.Empty(new ModePlanner().Build(SyncMode.Mirror, [File("a.tmp", "x")], [], filter: new SyncFilter(Exclude: ["*.tmp"])).Operations);

    [Fact]
    public void Filtered_baseline_path_never_becomes_a_delete()
    {
        var baseline = new[] { new BaselineEntry("ignored.tmp", File("ignored.tmp", "old"), File("ignored.tmp", "old")) };
        var plan = new ModePlanner().Build(SyncMode.TwoWay, [], [], baseline, new SyncFilter(Exclude: ["*.tmp"]));
        Assert.Empty(plan.Operations);
    }
    [Fact] public async Task Profiles_round_trip_without_credentials()
    {
        var path = Path.Combine(Path.GetTempPath(), "fengsync-profile-" + Guid.NewGuid() + ".json");
        try { var store = new ProfileStore(path); var profile = SyncProfile.Create("Nightly", "L", "R") with { Mode = SyncMode.Mirror }; await store.SaveAsync([profile]); var loaded = Assert.Single(await store.LoadAsync()); Assert.Equal(SyncMode.Mirror, loaded.Mode); Assert.Equal("Nightly", loaded.Name); }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }
    [Fact] public async Task Profile_runner_executes_a_mirror_batch()
    {
        var root = Path.Combine(Path.GetTempPath(), "fengsync-runner-" + Guid.NewGuid()); var left = Path.Combine(root, "left"); var right = Path.Combine(root, "right");
        try { Directory.CreateDirectory(left); Directory.CreateDirectory(right); await System.IO.File.WriteAllTextAsync(Path.Combine(left, "a.txt"), "data"); var profile = SyncProfile.Create("Mirror", left, right) with { Mode = SyncMode.Mirror }; var result = await new ProfileRunner().RunAsync(profile); Assert.Equal(1, result.Executed); Assert.Equal("data", await System.IO.File.ReadAllTextAsync(Path.Combine(right, "a.txt"))); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
