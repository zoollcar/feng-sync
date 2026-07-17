namespace FengSync.Core;

/// <summary>Creates directional plans for Mirror/Update/Custom modes. TwoWay delegates to the three-way planner.</summary>
public sealed class ModePlanner
{
    public SyncPlan Build(SyncMode mode, IEnumerable<EntrySnapshot> left, IEnumerable<EntrySnapshot> right,
        IEnumerable<BaselineEntry>? baseline = null, SyncFilter? filter = null)
    {
        filter ??= SyncFilter.Empty;
        var l = left.Where(x => filter.Includes(x.Path)).ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var r = right.Where(x => filter.Includes(x.Path)).ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        if (mode == SyncMode.TwoWay) return new ThreeWayPlanner().Build(l.Values, r.Values, baseline);
        var result = new List<SyncOperation>();
        foreach (var path in l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            l.TryGetValue(path, out var source); r.TryGetValue(path, out var target);
            if (source is null)
            {
                if (mode == SyncMode.Mirror && target is not null)
                    result.Add(new(path, OperationKind.DeleteRight, "镜像：左侧不存在，删除右侧"));
                continue; // Update and Custom default preserve destination-only entries.
            }
            if (target is null || !Same(source, target))
                result.Add(new(path, source.Kind == EntryKind.Directory ? OperationKind.CreateRightDirectory : OperationKind.CopyLeftToRight,
                    mode == SyncMode.Mirror ? "镜像：以左侧为准" : mode == SyncMode.Update ? "更新：左侧新增或更新" : "自定义：左侧→右侧"));
        }
        result.InsertRange(0, PathRules.FindBlockers(l.Values, r.Values));
        return new(result);
    }
    private static bool Same(EntrySnapshot a, EntrySnapshot b) => a.Kind == b.Kind &&
        (a.Kind == EntryKind.Directory || a.Fingerprint!.Matches(b.Fingerprint!));
}
