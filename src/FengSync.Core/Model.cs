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
public sealed record SyncFilter(IReadOnlyList<string>? Include = null, IReadOnlyList<string>? Exclude = null)
{
    public static SyncFilter Empty { get; } = new();
    public bool Includes(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        bool Match(string pattern) => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern.Replace('\\', '/'), path, ignoreCase: true);
        return (Include is null || Include.Count == 0 || Include.Any(Match)) && !(Exclude?.Any(Match) ?? false);
    }
}
public sealed record VersioningPolicy(VersioningMode Mode = VersioningMode.None, string? ArchiveDirectory = null, int KeepDays = 30);
public sealed record SyncProfile(
    string Id, string Name, string LeftPath, string RightPath,
    SyncMode Mode = SyncMode.TwoWay, SyncFilter? Filter = null,
    VersioningPolicy? Versioning = null, int MaxConcurrentCopies = 3,
    bool VerifyCopies = true, bool Enabled = true)
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
}
public sealed record SyncPlan(IReadOnlyList<SyncOperation> Operations)
{
    public bool CanExecute => Operations.Any(x => x.Selected) && Operations.All(x => !x.IsConflict);
}
