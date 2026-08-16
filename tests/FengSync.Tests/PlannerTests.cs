using FengSync.Core;

namespace FengSync.Tests;

public sealed class PlannerTests
{
    private readonly ThreeWayPlanner _planner = new();
    private static EntrySnapshot File(string path, string content) => new(path, EntryKind.File, new(content.Length, DateTimeOffset.UnixEpoch, content));
    private static IReadOnlyList<BaselineEntry> Baseline(string content = "A") => [new("a.txt", File("a.txt", content), File("a.txt", content))];

    [Fact] public void First_sync_copies_a_one_sided_file() => Assert.Equal(OperationKind.CopyLeftToRight, Assert.Single(_planner.Build([File("a.txt", "A")], [], null).Operations).Kind);
    [Fact] public void First_sync_never_infers_a_delete() => Assert.Empty(_planner.Build([], [], null).Operations);
    [Fact] public void First_sync_both_different_emits_a_resolvable_conflict()
    {
        // No baseline, both files present, both different → Conflict with explicit resolutions so the toolbar
        // "cover the other side" buttons succeed rather than failing with a missing-resolution error.
        var operation = Assert.Single(_planner.Build([File("a.txt", "B")], [File("a.txt", "C")], null).Operations);
        Assert.True(operation.IsConflict);
        Assert.NotNull(operation.KeepLeft); Assert.NotNull(operation.KeepRight);
        operation.ResolveConflict(true); Assert.Equal(OperationKind.CopyLeftToRight, operation.Kind);
        var second = Assert.Single(_planner.Build([File("a.txt", "B")], [File("a.txt", "C")], null).Operations);
        second.ResolveConflict(false); Assert.Equal(OperationKind.CopyRightToLeft, second.Kind);
    }
    [Fact] public void File_directory_conflict_stays_unresolved_until_the_type_collision_is_manually_handled()
    {
        var directory = new EntrySnapshot("photos", EntryKind.Directory, null);
        var operation = Assert.Single(_planner.Build([directory], [File("photos", "old file")], null).Operations);
        Assert.True(operation.IsConflict);
        Assert.Null(operation.KeepLeft); Assert.Null(operation.KeepRight);
        Assert.Throws<InvalidOperationException>(() => operation.ResolveConflict(true));
    }
    [Fact] public void One_side_modification_propagates() => Assert.Equal(OperationKind.CopyLeftToRight, Assert.Single(_planner.Build([File("a.txt", "B")], [File("a.txt", "A")], Baseline()).Operations).Kind);
    [Fact] public void One_side_delete_propagates_only_with_baseline() => Assert.Equal(OperationKind.DeleteRight, Assert.Single(_planner.Build([], [File("a.txt", "A")], Baseline()).Operations).Kind);
    [Fact]
    public void One_side_empty_directory_delete_is_a_visible_sync_operation()
    {
        var directory = new EntrySnapshot("empty", EntryKind.Directory, null);
        var baseline = new[] { new BaselineEntry("empty", directory, directory) };

        var operation = Assert.Single(_planner.Build([], [directory], baseline).Operations);

        Assert.Equal("empty", operation.Path);
        Assert.Equal(OperationKind.DeleteRight, operation.Kind);
    }
    [Fact] public void Equal_two_sided_change_merges_without_action() => Assert.Empty(_planner.Build([File("a.txt", "B")], [File("a.txt", "B")], Baseline()).Operations);
    [Fact] public void Equal_two_sided_delete_merges_without_action() => Assert.Empty(_planner.Build([], [], Baseline()).Operations);
    [Fact] public void Different_two_sided_change_is_unresolved_conflict()
    {
        var operation = Assert.Single(_planner.Build([File("a.txt", "B")], [File("a.txt", "C")], Baseline()).Operations);
        Assert.True(operation.IsConflict); Assert.False(new SyncPlan([operation]).CanExecute);
    }
    [Fact] public void Conflict_can_be_explicitly_resolved_to_left()
    {
        var operation = Assert.Single(_planner.Build([File("a.txt", "B")], [File("a.txt", "C")], Baseline()).Operations);
        operation.ResolveConflict(true); Assert.False(operation.IsConflict); Assert.Equal(OperationKind.CopyLeftToRight, operation.Kind); Assert.True(new SyncPlan([operation]).CanExecute);
    }
    [Fact] public void Conflict_can_be_explicitly_resolved_to_right()
    {
        var operation = Assert.Single(_planner.Build([File("a.txt", "B")], [File("a.txt", "C")], Baseline()).Operations);
        operation.ResolveConflict(false); Assert.False(operation.IsConflict); Assert.Equal(OperationKind.CopyRightToLeft, operation.Kind);
    }
    [Fact] public void Delete_modify_conflict_can_resolve_to_delete()
    {
        var operation = Assert.Single(_planner.Build([], [File("a.txt", "B")], Baseline()).Operations);
        operation.ResolveConflict(true); Assert.Equal(OperationKind.DeleteRight, operation.Kind);
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
    [Fact] public void KeepLeft_on_DeleteRight_restores_right_from_left()
    {
        var operation = new SyncOperation("a.txt", OperationKind.DeleteRight, "delete");
        operation.OverrideCopyDirection(true);
        Assert.Equal(OperationKind.CopyLeftToRight, operation.Kind);
    }
    [Fact] public void KeepRight_on_DeleteRight_also_deletes_the_still_present_left()
    {
        var operation = new SyncOperation("a.txt", OperationKind.DeleteRight, "delete");
        operation.OverrideCopyDirection(false);
        Assert.Equal(OperationKind.DeleteLeft, operation.Kind);
    }
    [Fact] public void KeepLeft_on_DeleteLeft_also_deletes_the_still_present_right()
    {
        var operation = new SyncOperation("a.txt", OperationKind.DeleteLeft, "delete");
        operation.OverrideCopyDirection(true);
        Assert.Equal(OperationKind.DeleteRight, operation.Kind);
    }
    [Fact] public void KeepRight_on_DeleteLeft_restores_left_from_right()
    {
        var operation = new SyncOperation("a.txt", OperationKind.DeleteLeft, "delete");
        operation.OverrideCopyDirection(false);
        Assert.Equal(OperationKind.CopyRightToLeft, operation.Kind);
    }
    [Fact] public void OverrideCopyDirection_rejects_unrelated_kinds()
    {
        var create = new SyncOperation("dir", OperationKind.CreateRightDirectory, "create");
        Assert.Throws<InvalidOperationException>(() => create.OverrideCopyDirection(true));
    }
    [Fact] public void KeepLeft_on_copy_with_empty_left_collapses_to_delete_right()
    {
        // Right has the file, left is empty: planner proposes CopyRightToLeft. Picking "left wins" should
        // delete the right-side file so the (empty) winner's state propagates, not flip to a copy-from-empty.
        var operation = new SyncOperation("a.txt", OperationKind.CopyRightToLeft, "default");
        operation.OverrideCopyDirection(true, left: null, right: File("a.txt", "B"));
        Assert.Equal(OperationKind.DeleteRight, operation.Kind);
    }
    [Fact] public void KeepRight_on_copy_with_empty_right_collapses_to_delete_left()
    {
        var operation = new SyncOperation("a.txt", OperationKind.CopyLeftToRight, "default");
        operation.OverrideCopyDirection(false, left: File("a.txt", "B"), right: null);
        Assert.Equal(OperationKind.DeleteLeft, operation.Kind);
    }
    [Fact] public void OverrideCopyDirection_with_both_sides_present_keeps_legacy_flip()
    {
        // Without empty-side asymmetry, flip semantics still apply so the existing tests stay intact.
        var operation = new SyncOperation("a.txt", OperationKind.CopyLeftToRight, "default");
        operation.OverrideCopyDirection(false, left: File("a.txt", "A"), right: File("a.txt", "B"));
        Assert.Equal(OperationKind.CopyRightToLeft, operation.Kind);
    }
    [Fact] public void Blocked_rows_fall_through_to_conflict_resolution()
    {
        // Blocked rows are conflicts (IsConflict=true) with KeepLeft/KeepRight provided,
        // so OverrideCopyDirection must take the ResolveConflict path and succeed.
        var blocked = new SyncOperation("a.txt", OperationKind.Blocked, "blocked", true, OperationKind.CopyLeftToRight, OperationKind.CopyRightToLeft);
        Assert.True(blocked.IsConflict);
        blocked.OverrideCopyDirection(true);
        Assert.False(blocked.IsConflict); Assert.Equal(OperationKind.CopyLeftToRight, blocked.Kind);
    }
}
