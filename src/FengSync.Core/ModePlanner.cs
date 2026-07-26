namespace FengSync.Core;

/// <summary>Creates directional plans for Mirror/Update/Custom modes. TwoWay delegates to the three-way planner.</summary>
public sealed class ModePlanner
{
    public SyncPlan Build(SyncMode mode, IEnumerable<EntrySnapshot> left, IEnumerable<EntrySnapshot> right,
        IEnumerable<BaselineEntry>? baseline = null, SyncFilter? filter = null,
        EndpointCapabilities? leftCapabilities = null, EndpointCapabilities? rightCapabilities = null,
        MoveDetectionSettings? moveSettings = null)
    {
        var engine = (filter ?? SyncFilter.Empty).CreateEngine();
        bool Included(EntrySnapshot x) => engine.Evaluate(x.Path, new FilterEntryAttributes(x.Fingerprint?.Size, x.Fingerprint?.ModifiedUtc)).Included;
        // If either endpoint is case-sensitive, preserve case-distinct names in
        // the common plan index. Folding them would make an S3/Drive pair lose an
        // object before move coordination even starts.
        var paths = PlanPathSemantics(leftCapabilities, rightCapabilities);
        var l = Index(left.Where(Included), paths);
        var r = Index(right.Where(Included), paths);
        // Filtering is a sync boundary, not a deletion request. Filtering baseline records
        // prevents a newly excluded historical path from being interpreted as missing.
        var filteredBaseline = baseline?.Where(x => engine.Evaluate(x.Path).Included);
        if (mode == SyncMode.TwoWay)
        {
            var plan = new ThreeWayPlanner().Build(l.Values, r.Values, filteredBaseline, paths);
            return MoveCoordinator.Apply(plan, mode, l, r, filteredBaseline?.ToList(), leftCapabilities, rightCapabilities, moveSettings, paths);
        }
        var result = new List<SyncOperation>();
        foreach (var key in l.Keys.Union(r.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            l.TryGetValue(key, out var source); r.TryGetValue(key, out var target);
            var path = source?.Path ?? target!.Path;
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
        return MoveCoordinator.Apply(new(result), mode, l, r, filteredBaseline?.ToList(), leftCapabilities, rightCapabilities, moveSettings, paths);
    }
    private static bool Same(EntrySnapshot a, EntrySnapshot b) => a.Kind == b.Kind &&
        (a.Kind == EntryKind.Directory || a.Fingerprint!.Matches(b.Fingerprint!));
    private static EndpointPathSemantics PlanPathSemantics(EndpointCapabilities? left, EndpointCapabilities? right)
    {
        var l = left?.EffectivePaths ?? new(false, System.Text.NormalizationForm.FormC);
        var r = right?.EffectivePaths ?? new(false, System.Text.NormalizationForm.FormC);
        return new(l.CaseSensitive || r.CaseSensitive, l.UnicodeNormalization, '/');
    }
    private static Dictionary<string, EntrySnapshot> Index(IEnumerable<EntrySnapshot> entries, EndpointPathSemantics paths)
        => entries.ToDictionary(x => paths.Canonicalize(x.Path), x => x, StringComparer.Ordinal);
}

internal static class MoveCoordinator
{
    public static SyncPlan Apply(SyncPlan plan, SyncMode mode, IReadOnlyDictionary<string, EntrySnapshot> left,
        IReadOnlyDictionary<string, EntrySnapshot> right, IReadOnlyList<BaselineEntry>? baseline,
        EndpointCapabilities? leftCapabilities, EndpointCapabilities? rightCapabilities, MoveDetectionSettings? settings, EndpointPathSemantics paths)
    {
        if (baseline is null || settings is { Enabled: false }) return plan;
        settings ??= new();
        var detector = new EndpointMoveDetector();
        var lf = detector.Detect(EndpointSide.Left, baseline, left.Values, settings, leftCapabilities?.EffectivePaths).ToDictionary(x => paths.Canonicalize(x.OldPath), StringComparer.Ordinal);
        var rf = detector.Detect(EndpointSide.Right, baseline, right.Values, settings, rightCapabilities?.EffectivePaths).ToDictionary(x => paths.Canonicalize(x.OldPath), StringComparer.Ordinal);
        if (lf.Count == 0 && rf.Count == 0) return plan;
        var output = plan.Operations.ToList();
        foreach (var key in lf.Keys.Union(rf.Keys, StringComparer.Ordinal))
        {
            lf.TryGetValue(key, out var lmove); rf.TryGetValue(key, out var rmove);
            var old = lmove?.OldPath ?? rmove!.OldPath;
            if (lmove is not null && rmove is not null)
            {
                RemovePathOperations(output, old, lmove.NewPath, rmove.NewPath);
                if (paths.Canonicalize(lmove.NewPath) == paths.Canonicalize(rmove.NewPath))
                {
                    // There is no endpoint I/O to run, but keep a selected, committed
                    // operation so baseline state is re-keyed from old to new.
                    output.Add(new(lmove.NewPath, OperationKind.Move, "两侧已移动到同一目标路径；更新同步基线", move: new(EndpointSide.Left, EndpointSide.Left, old, lmove.NewPath, lmove.Kind, lmove.Evidence, MoveConfidence.Certain, EndpointMoveExecution.None, MoveFallback.None)));
                    continue;
                }
                output.Add(new(lmove.NewPath, OperationKind.MoveConflict, "移动冲突：两侧移动到了不同目标路径", true));
                continue;
            }
            var changed = lmove ?? rmove!;
            var executeOn = changed.Side == EndpointSide.Left ? EndpointSide.Right : EndpointSide.Left;
            if (mode == SyncMode.Mirror && (!settings.PropagateMovesInMirror || changed.Side != EndpointSide.Left)) continue;
            if (mode == SyncMode.Update && (changed.Side != EndpointSide.Left || !settings.PropagateMovesInUpdate)) continue;
            var target = executeOn == EndpointSide.Left ? left : right;
            var baselineEntry = baseline.First(x => paths.Canonicalize(x.Path) == paths.Canonicalize(old));
            var targetOld = executeOn == EndpointSide.Left ? baselineEntry.Left : baselineEntry.Right;
            if (!target.TryGetValue(paths.Canonicalize(old), out var currentOld) || targetOld is null || !Same(currentOld, targetOld))
            {
                RemovePathOperations(output, old, changed.NewPath);
                output.Add(new(changed.NewPath, OperationKind.MoveConflict, "移动冲突：另一端旧路径已修改或删除", true));
                continue;
            }
            if (target.ContainsKey(paths.Canonicalize(changed.NewPath)))
            {
                RemovePathOperations(output, old, changed.NewPath);
                output.Add(new(changed.NewPath, OperationKind.MoveConflict, "移动冲突：目标路径已被占用", true));
                continue;
            }
            RemovePathOperations(output, old, changed.NewPath);
            var caps = executeOn == EndpointSide.Left ? leftCapabilities : rightCapabilities;
            var execution = caps?.EffectiveMove.FileExecution ?? EndpointMoveExecution.None;
            var fallback = execution == EndpointMoveExecution.NativeRename ? MoveFallback.CrossEndpointCopyDelete :
                execution == EndpointMoveExecution.ServerCopyDelete ? MoveFallback.None : MoveFallback.CrossEndpointCopyDelete;
            var descriptor = new MoveDescriptor(changed.Side, executeOn, old, changed.NewPath, changed.Kind, changed.Evidence,
                changed.Confidence, execution, fallback);
            output.Add(new(changed.NewPath, OperationKind.Move, "传播端点内移动", selected: changed.Confidence <= settings.MinimumAutoExecuteConfidence, move: descriptor));
        }
        return new(output.OrderBy(x => x.Path, StringComparer.Ordinal).ToList());
    }
    private static void RemovePathOperations(List<SyncOperation> operations, params string[] paths) =>
        operations.RemoveAll(x => paths.Any(p => string.Equals(p, x.Path, StringComparison.Ordinal)) &&
            x.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft or OperationKind.DeleteLeft or OperationKind.DeleteRight or OperationKind.Conflict);
    private static bool Same(EntrySnapshot a, EntrySnapshot b) => a.Kind == b.Kind &&
        (a.Kind == EntryKind.Directory || (a.Fingerprint is not null && b.Fingerprint is not null && a.Fingerprint.Matches(b.Fingerprint)));
}
