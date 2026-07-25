using FengSync.Core.Configuration;

namespace FengSync.Core;

public enum EntryKind { File, Directory }
public enum Delta { Unchanged, Created, Modified, Deleted }
/// <summary>Compatibility modes modelled after FreeFileSync's synchronization settings.</summary>
public enum SyncMode { TwoWay, Mirror, Update, Custom }
public enum VersioningMode { None, RecycleBin, TimestampedArchive }
public enum OperationKind { CopyLeftToRight, CopyRightToLeft, DeleteLeft, DeleteRight, CreateLeftDirectory, CreateRightDirectory, Conflict, Blocked }
public sealed record Fingerprint(long Size, DateTimeOffset ModifiedUtc, string? Hash)
{
    public bool Matches(Fingerprint other) => Size == other.Size &&
        (Hash is not null && other.Hash is not null ? Hash == other.Hash : Math.Abs((ModifiedUtc - other.ModifiedUtc).TotalSeconds) < 2);
    public bool Matches(Fingerprint other, TimeSpan timestampTolerance) => Size == other.Size &&
        (Hash is not null && other.Hash is not null ? Hash == other.Hash : Math.Abs((ModifiedUtc - other.ModifiedUtc).TotalSeconds) <= timestampTolerance.TotalSeconds);
}
public sealed record EntrySnapshot(string Path, EntryKind Kind, Fingerprint? Fingerprint);
public sealed record BaselineEntry(string Path, EntrySnapshot? Left, EntrySnapshot? Right);
public sealed record SyncFilter(IReadOnlyList<string>? Include = null, IReadOnlyList<string>? Exclude = null, IReadOnlyList<FilterRule>? Rules = null)
{
    public static SyncFilter Empty { get; } = new();
    /// <summary>Converts the persisted simple lists into the shared ordered filtering model.
    /// Includes are evaluated first and exclusions last, so an exclusion always remains safe.</summary>
    public IReadOnlyList<FilterRule> ToRules()
    {
        if (Rules is { Count: > 0 }) return Rules;
        var rules = new List<FilterRule>();
        if (Include is { Count: > 0 })
        {
            // An include list changes the default from include-all to include-none.
            rules.Add(new(FilterRuleKind.Exclude, "**", "不在包含规则中"));
            rules.AddRange(Include.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => new FilterRule(FilterRuleKind.Include, x.Trim(), "包含规则")));
        }
        rules.AddRange((Exclude ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => new FilterRule(FilterRuleKind.Exclude, x.Trim(), "排除规则")));
        return rules;
    }
    public FilterEngine CreateEngine() => new(ToRules());
    public bool Includes(string relativePath) => CreateEngine().Evaluate(relativePath).Included;
}
public sealed record VersioningPolicy(VersioningMode Mode = VersioningMode.None, string? ArchiveDirectory = null, int? KeepDays = 30, int? MaxVersionsPerFile = null, long? MaxTotalBytes = null)
{
    public RetentionPolicy ToRetentionPolicy() => new(KeepDays, MaxVersionsPerFile, MaxTotalBytes);
}
public sealed record SyncProfile(
    string Id, string Name, string LeftPath, string RightPath,
    SyncMode Mode = SyncMode.TwoWay, SyncFilter? Filter = null,
    VersioningPolicy? Versioning = null, int MaxConcurrentCopies = 3,
    bool VerifyCopies = true, bool Enabled = true,
    ProfileSettings? Settings = null, string? Description = null,
    int MaxDeletes = int.MaxValue, double MaxDeleteRatio = 1)
{
    public static SyncProfile Create(string name, string left, string right) => new(Guid.NewGuid().ToString("N"), name, left, right);
}
public sealed class SyncOperation
{
    public SyncOperation(string path, OperationKind kind, string reason, bool selected = true, OperationKind? keepLeft = null, OperationKind? keepRight = null)
    { (Path, Kind, Reason, Selected, KeepLeft, KeepRight) = (path, kind, reason, selected, keepLeft, keepRight); IsConflict = kind is OperationKind.Conflict or OperationKind.Blocked; }
    public string Path { get; }
    public Guid OperationId { get; } = Guid.NewGuid();
    public OperationKind Kind { get; private set; }
    public string Reason { get; private set; }
    public bool Selected { get; set; }
    public OperationKind? KeepLeft { get; }
    public OperationKind? KeepRight { get; }
    public bool IsConflict { get; private set; }
    /// <summary>Resolve a conflict by choosing the left or right side as the winner.</summary>
    public void ResolveConflict(bool keepLeft)
    {
        if (!IsConflict || (keepLeft ? KeepLeft : KeepRight) is not { } resolution) throw new InvalidOperationException("此冲突无法按所选方向裁决。");
        Kind = resolution; IsConflict = false; Reason = keepLeft ? "冲突裁决：保留左侧" : "冲突裁决：保留右侧";
    }
    /// <summary>Override a planned operation by choosing the winning side.
    /// Copies flip direction, conflicts get resolved to the chosen side, and one-sided deletes
    /// behave as follows: the named winning side is treated as the source. If that source still
    /// exists, the missing side is restored from it; if that source has been deleted, the other
    /// side is also removed (so both sides end up deleted).
    /// When entry snapshots are supplied, "use the empty side to override" turns into a delete
    /// of the populated side so the result matches the winner's (empty) state.</summary>
    public void OverrideCopyDirection(bool keepLeft, EntrySnapshot? left = null, EntrySnapshot? right = null)
    {
        if (IsConflict) { ResolveConflict(keepLeft); return; }
        switch (Kind)
        {
            case OperationKind.DeleteLeft:
                // Left is missing. keepLeft=true → left "wins" but is gone → also delete right.
                // keepLeft=false → right wins and still exists → restore left from right.
                if (keepLeft) { Kind = OperationKind.DeleteRight; Reason = "用户覆盖：左侧覆盖右侧，左侧已删除；现也删除右侧"; return; }
                Kind = OperationKind.CopyRightToLeft; Reason = "用户覆盖：右侧覆盖左侧，恢复已删除的左文件"; return;
            case OperationKind.DeleteRight:
                // Right is missing. keepLeft=true → left wins and still exists → restore right from left.
                // keepLeft=false → right "wins" but is gone → also delete left.
                if (keepLeft) { Kind = OperationKind.CopyLeftToRight; Reason = "用户覆盖：左侧覆盖右侧，恢复已删除的右文件"; return; }
                Kind = OperationKind.DeleteLeft; Reason = "用户覆盖：右侧覆盖左侧，右侧已删除；现也删除左侧"; return;
            case OperationKind.CopyLeftToRight:
            case OperationKind.CopyRightToLeft:
                // If entry info is available, "the winner wins" should land on a delete when the
                // winner side currently has nothing — flipping direction would otherwise produce
                // a copy from an empty source. With no entry info we keep the legacy flip behavior.
                if (left is not null || right is not null)
                {
                    var winnerEmpty = keepLeft ? left is null : right is null;
                    if (winnerEmpty) { Kind = keepLeft ? OperationKind.DeleteRight : OperationKind.DeleteLeft; Reason = keepLeft ? "用户覆盖：左侧覆盖右侧，左侧为空；现也删除右侧" : "用户覆盖：右侧覆盖左侧，右侧为空；现也删除左侧"; return; }
                }
                Kind = keepLeft ? OperationKind.CopyLeftToRight : OperationKind.CopyRightToLeft;
                Reason = keepLeft ? "用户覆盖：左侧覆盖右侧" : "用户覆盖：右侧覆盖左侧";
                return;
            default:
                throw new InvalidOperationException("仅复制、删除与冲突项支持覆盖方向；目录创建或被屏蔽项无法覆盖。");
        }
    }
}
public sealed record SyncPlan(IReadOnlyList<SyncOperation> Operations)
{
    // An ignored conflict is intentionally left visible in the comparison list, but it
    // must not prevent the remaining selected operations from running.
    public bool CanExecute => Operations.Any(x => x.Selected) && Operations.Where(x => x.Selected).All(x => !x.IsConflict);
}
