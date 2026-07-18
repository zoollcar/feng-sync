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
    public void Resolve(bool keepLeft)
    {
        if (!IsConflict || (keepLeft ? KeepLeft : KeepRight) is not { } resolution) throw new InvalidOperationException("此冲突无法按所选方向裁决。");
        Kind = resolution; IsConflict = false; Reason = keepLeft ? "冲突裁决：保留左侧" : "冲突裁决：保留右侧";
    }
    /// <summary>Lets the user override the planner's default copy direction without turning a copy into a deletion.</summary>
    public void OverrideCopyDirection(bool keepLeft)
    {
        if (IsConflict) { Resolve(keepLeft); return; }
        if (Kind is not (OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft)) throw new InvalidOperationException("只有文件覆盖操作可以修改方向；删除和目录操作不能反转。");
        Kind = keepLeft ? OperationKind.CopyLeftToRight : OperationKind.CopyRightToLeft;
        Reason = keepLeft ? "用户覆盖：左侧覆盖右侧" : "用户覆盖：右侧覆盖左侧";
    }
}
public sealed record SyncPlan(IReadOnlyList<SyncOperation> Operations)
{
    public bool CanExecute => Operations.Any(x => x.Selected) && Operations.All(x => !x.IsConflict);
}
