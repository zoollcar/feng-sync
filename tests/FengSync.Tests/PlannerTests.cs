using FengSync.Core;

namespace FengSync.Tests;

public sealed class PlannerTests
{
    private readonly ThreeWayPlanner _planner = new();
    private static EntrySnapshot File(string path, string content) => new(path, EntryKind.File, new(content.Length, DateTimeOffset.UnixEpoch, content));
    private static IReadOnlyList<BaselineEntry> Baseline(string content = "A") => [new("a.txt", File("a.txt", content), File("a.txt", content))];

    [Fact] public void First_sync_copies_a_one_sided_file() => Assert.Equal(OperationKind.CopyLeftToRight, Assert.Single(_planner.Build([File("a.txt", "A")], [], null).Operations).Kind);
    [Fact] public void First_sync_never_infers_a_delete() => Assert.Empty(_planner.Build([], [], null).Operations);
    [Fact] public void One_side_modification_propagates() => Assert.Equal(OperationKind.CopyLeftToRight, Assert.Single(_planner.Build([File("a.txt", "B")], [File("a.txt", "A")], Baseline()).Operations).Kind);
    [Fact] public void One_side_delete_propagates_only_with_baseline() => Assert.Equal(OperationKind.DeleteRight, Assert.Single(_planner.Build([], [File("a.txt", "A")], Baseline()).Operations).Kind);
    [Fact] public void Equal_two_sided_change_merges_without_action() => Assert.Empty(_planner.Build([File("a.txt", "B")], [File("a.txt", "B")], Baseline()).Operations);
    [Fact] public void Different_two_sided_change_is_unresolved_conflict()
    {
        var operation = Assert.Single(_planner.Build([File("a.txt", "B")], [File("a.txt", "C")], Baseline()).Operations);
        Assert.True(operation.IsConflict); Assert.False(new SyncPlan([operation]).CanExecute);
    }
    [Fact] public void Conflict_can_be_explicitly_resolved_to_left()
    {
        var operation = Assert.Single(_planner.Build([File("a.txt", "B")], [File("a.txt", "C")], Baseline()).Operations);
        operation.Resolve(true); Assert.False(operation.IsConflict); Assert.Equal(OperationKind.CopyLeftToRight, operation.Kind); Assert.True(new SyncPlan([operation]).CanExecute);
    }
    [Fact] public void Delete_modify_conflict_can_resolve_to_delete()
    {
        var operation = Assert.Single(_planner.Build([], [File("a.txt", "B")], Baseline()).Operations);
        operation.Resolve(true); Assert.Equal(OperationKind.DeleteRight, operation.Kind);
    }
    [Theory] [InlineData("CON.txt")] [InlineData("name. ")] [InlineData("dir/../x")]
    public void Invalid_windows_path_blocks_plan(string path) => Assert.Contains(_planner.Build([File(path, "A")], [], null).Operations, x => x.Kind == OperationKind.Blocked);
    [Fact] public void Existing_baseline_with_new_path_copies_new_path() => Assert.Equal(OperationKind.CopyLeftToRight, Assert.Single(_planner.Build([File("a.txt", "A"), File("b.txt", "B")], [File("a.txt", "A")], Baseline()).Operations).Kind);
    [Fact] public void A_normal_copy_can_be_overridden_to_the_other_direction()
    {
        var operation = new SyncOperation("a.txt", OperationKind.CopyLeftToRight, "default");
        operation.OverrideCopyDirection(false);
        Assert.Equal(OperationKind.CopyRightToLeft, operation.Kind); Assert.Equal("用户覆盖：右侧覆盖左侧", operation.Reason);
    }
    [Fact] public void A_delete_cannot_be_reversed_as_a_copy() => Assert.Throws<InvalidOperationException>(() => new SyncOperation("a.txt", OperationKind.DeleteRight, "delete").OverrideCopyDirection(false));
}
