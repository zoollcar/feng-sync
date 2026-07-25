namespace FengSync.Core;

/// <summary>Pure three-way comparison. A missing baseline means first sync and never propagates deletion.</summary>
public sealed class ThreeWayPlanner
{
    public SyncPlan Build(IEnumerable<EntrySnapshot> left, IEnumerable<EntrySnapshot> right, IEnumerable<BaselineEntry>? baseline)
    {
        var leftEntries = left.ToList(); var rightEntries = right.ToList();
        var l = Index(leftEntries); var r = Index(rightEntries);
        var b = baseline?.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var baselinePaths = b is null ? Enumerable.Empty<string>() : b.Keys;
        var paths = l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(baselinePaths, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var output = new List<SyncOperation>();
        foreach (var path in paths)
        {
            l.TryGetValue(path, out var nowL); r.TryGetValue(path, out var nowR);
            BaselineEntry? old = null;
            if (b is not null) b.TryGetValue(path, out old);
            // A known database may still have a newly created path that has no individual baseline.
            if (b is null || old is null) { First(path, nowL, nowR, output); continue; }
            Decide(path, nowL, nowR, old, output);
        }
        output.InsertRange(0, PathRules.FindBlockers(leftEntries, rightEntries));
        return new(output);
    }
    private static Dictionary<string, EntrySnapshot> Index(IEnumerable<EntrySnapshot> entries) =>
        entries.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
    private static Delta Change(EntrySnapshot? current, EntrySnapshot? old) =>
        old is null ? (current is null ? Delta.Unchanged : Delta.Created) : current is null ? Delta.Deleted :
        current.Kind == old.Kind && (current.Kind == EntryKind.Directory || current.Fingerprint!.Matches(old.Fingerprint!)) ? Delta.Unchanged : Delta.Modified;
    private static bool Same(EntrySnapshot a, EntrySnapshot b) => a.Kind == b.Kind &&
        (a.Kind == EntryKind.Directory || a.Fingerprint!.Matches(b.Fingerprint!));
    private static void First(string p, EntrySnapshot? l, EntrySnapshot? r, List<SyncOperation> o)
    {
        if (l is null && r is not null) o.Add(new(p, r.Kind == EntryKind.Directory ? OperationKind.CreateLeftDirectory : OperationKind.CopyRightToLeft, "首次同步：仅右侧存在"));
        else if (r is null && l is not null) o.Add(new(p, l.Kind == EntryKind.Directory ? OperationKind.CreateRightDirectory : OperationKind.CopyLeftToRight, "首次同步：仅左侧存在"));
        else if (l is not null && r is not null && !Same(l, r)) o.Add(Conflict(p, "首次同步：两侧内容不同", l, r));
    }
    private static void Decide(string p, EntrySnapshot? l, EntrySnapshot? r, BaselineEntry b, List<SyncOperation> o)
    {
        var dl = Change(l, b.Left); var dr = Change(r, b.Right);
        if (dl == Delta.Unchanged && dr == Delta.Unchanged) return;
        if (dl == Delta.Unchanged) { Propagate(p, r!, true, dr, o); return; }
        if (dr == Delta.Unchanged) { Propagate(p, l!, false, dl, o); return; }
        if (l is not null && r is not null && Same(l, r)) return;
        o.Add(Conflict(p, $"两侧均已变更（左：{dl}；右：{dr}）", l, r));
    }
    private static SyncOperation Conflict(string path, string reason, EntrySnapshot? left, EntrySnapshot? right)
    {
        // Replacing a file with a directory (or the reverse) also requires removing a
        // whole conflicting subtree and replanning its children. A single operation cannot
        // safely express that transaction, so leave it unresolved instead of emitting a
        // directory creation that will fail on the existing file.
        if (left is not null && right is not null && left.Kind != right.Kind)
            return new(path, OperationKind.Conflict, reason + "（文件与目录类型不一致，请手动处理后重新比较）");
        return new(path, OperationKind.Conflict, reason, true, Resolution(left, true), Resolution(right, false));
    }
    private static OperationKind Resolution(EntrySnapshot? source, bool sourceIsLeft) => source is null
        ? (sourceIsLeft ? OperationKind.DeleteRight : OperationKind.DeleteLeft)
        : source.Kind == EntryKind.Directory
            ? (sourceIsLeft ? OperationKind.CreateRightDirectory : OperationKind.CreateLeftDirectory)
            : (sourceIsLeft ? OperationKind.CopyLeftToRight : OperationKind.CopyRightToLeft);
    private static void Propagate(string p, EntrySnapshot changed, bool toLeft, Delta delta, List<SyncOperation> o)
    {
        var kind = delta == Delta.Deleted ? (toLeft ? OperationKind.DeleteLeft : OperationKind.DeleteRight) :
            changed.Kind == EntryKind.Directory ? (toLeft ? OperationKind.CreateLeftDirectory : OperationKind.CreateRightDirectory) :
            (toLeft ? OperationKind.CopyRightToLeft : OperationKind.CopyLeftToRight);
        o.Add(new(p, kind, delta == Delta.Deleted ? "传播已确认的删除" : "传播单侧变更"));
    }
}
