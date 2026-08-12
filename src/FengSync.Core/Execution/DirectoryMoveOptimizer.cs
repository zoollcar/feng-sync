namespace FengSync.Core.Execution;

internal sealed record DirectoryMoveGroup(
    EndpointSide ExecuteOn,
    EndpointSide ChangedOn,
    string FromDirectory,
    string ToDirectory,
    IReadOnlyList<SyncOperation> FileOperations,
    IReadOnlyList<SyncOperation> StructuralOperations);

/// <summary>
/// Finds file Move operations that represent a complete directory rename.
/// The optimizer is intentionally conservative: any missing, filtered,
/// deselected, occupied or otherwise affected path disables the aggregation.
/// </summary>
internal static class DirectoryMoveOptimizer
{
    public static IReadOnlyList<DirectoryMoveGroup> Find(PlanSnapshot snapshot, IEndpoint left, IEndpoint right)
    {
        var candidates = snapshot.Plan.Operations
            .Where(x => x.Selected && x.Kind == OperationKind.Move && x.Move is
            {
                Kind: EntryKind.File,
                PreferredExecution: not EndpointMoveExecution.None
            } && x.Move.ChangedOn != x.Move.ExecuteOn)
            .Select(x => CreateCandidate(x))
            .Where(x => x is not null)
            .Cast<Candidate>()
            .ToList();

        var validated = new List<DirectoryMoveGroup>();
        foreach (var bucket in candidates.GroupBy(x => (x.Move.ExecuteOn, x.Move.ChangedOn, x.FromDirectory, x.ToDirectory)))
        {
            var executeOn = bucket.Key.ExecuteOn;
            var target = executeOn == EndpointSide.Left ? left : right;
            if (target.Capabilities.EffectiveMove.DirectoryExecution != EndpointMoveExecution.NativeRename) continue;

            var operations = bucket.Select(x => x.Operation).ToList();
            var group = Validate(snapshot, bucket.Key.ExecuteOn, bucket.Key.ChangedOn,
                bucket.Key.FromDirectory, bucket.Key.ToDirectory, operations);
            if (group is not null) validated.Add(group);
        }

        var output = new List<DirectoryMoveGroup>();
        foreach (var group in validated
                     .OrderByDescending(x => x.FileOperations.Count)
                     .ThenBy(x => x.FromDirectory.Count(c => c == '/'))
                     .ThenBy(x => x.FromDirectory, StringComparer.Ordinal))
        {
            // Never aggregate overlapping groups. Prefer the group covering more
            // files, then the shallower root for deterministic behavior.
            if (output.Any(x => x.ExecuteOn == group.ExecuteOn &&
                (Overlaps(x.FromDirectory, group.FromDirectory) || Overlaps(x.ToDirectory, group.ToDirectory))))
                continue;
            output.Add(group);
        }
        return output;
    }

    private static DirectoryMoveGroup? Validate(PlanSnapshot snapshot, EndpointSide executeOn, EndpointSide changedOn,
        string fromDirectory, string toDirectory, IReadOnlyList<SyncOperation> operations)
    {
        if (string.Equals(fromDirectory, toDirectory, StringComparison.Ordinal) ||
            Overlaps(fromDirectory, toDirectory))
            return null;

        var targetEntries = executeOn == EndpointSide.Left ? snapshot.LeftEntries : snapshot.RightEntries;
        var sourceEntries = changedOn == EndpointSide.Left ? snapshot.LeftEntries : snapshot.RightEntries;
        var targetTree = targetEntries.Values.Where(x => AtOrBelow(x.Path, fromDirectory)).ToList();
        var sourceTree = sourceEntries.Values.Where(x => AtOrBelow(x.Path, toDirectory)).ToList();
        if (!targetTree.Any(x => x.Path == fromDirectory && x.Kind == EntryKind.Directory) ||
            !sourceTree.Any(x => x.Path == toDirectory && x.Kind == EntryKind.Directory))
            return null;

        // The new destination must be completely absent on the execution endpoint,
        // and the old directory must be absent on the endpoint that was renamed.
        if (targetEntries.Values.Any(x => AtOrBelow(x.Path, toDirectory)) ||
            sourceEntries.Values.Any(x => AtOrBelow(x.Path, fromDirectory)))
            return null;

        var targetByRelative = targetTree.ToDictionary(x => Relative(fromDirectory, x.Path), StringComparer.Ordinal);
        var sourceByRelative = sourceTree.ToDictionary(x => Relative(toDirectory, x.Path), StringComparer.Ordinal);
        if (targetByRelative.Count != sourceByRelative.Count) return null;
        foreach (var (relative, oldEntry) in targetByRelative)
        {
            if (!sourceByRelative.TryGetValue(relative, out var newEntry) || oldEntry.Kind != newEntry.Kind)
                return null;
            if (oldEntry.Kind == EntryKind.File &&
                (oldEntry.Fingerprint is null || newEntry.Fingerprint is null ||
                 !oldEntry.Fingerprint.Matches(newEntry.Fingerprint, TimeSpan.FromSeconds(5))))
                return null;
        }

        var oldFiles = targetTree.Where(x => x.Kind == EntryKind.File).Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        var newFiles = sourceTree.Where(x => x.Kind == EntryKind.File).Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        var moveOldFiles = operations.Select(x => x.Move!.FromPath).ToHashSet(StringComparer.Ordinal);
        var moveNewFiles = operations.Select(x => x.Move!.ToPath).ToHashSet(StringComparer.Ordinal);
        if (!oldFiles.SetEquals(moveOldFiles) || !newFiles.SetEquals(moveNewFiles))
            return null;

        var operationIds = operations.Select(x => x.OperationId).ToHashSet();
        var structural = new List<SyncOperation>();
        foreach (var operation in snapshot.Plan.Operations.Where(x => !operationIds.Contains(x.OperationId) &&
                     (AtOrBelow(x.Path, fromDirectory) || AtOrBelow(x.Path, toDirectory))))
        {
            var expectedCreate = executeOn == EndpointSide.Left ? OperationKind.CreateLeftDirectory : OperationKind.CreateRightDirectory;
            var expectedDelete = executeOn == EndpointSide.Left ? OperationKind.DeleteLeft : OperationKind.DeleteRight;
            var isStructural = (operation.Kind == expectedCreate && AtOrBelow(operation.Path, toDirectory)) ||
                               (operation.Kind == expectedDelete && AtOrBelow(operation.Path, fromDirectory));
            if (!isStructural || !operation.Selected) return null;
            structural.Add(operation);
        }

        return new(executeOn, changedOn, fromDirectory, toDirectory, operations, structural);
    }

    private static Candidate? CreateCandidate(SyncOperation operation)
    {
        var move = operation.Move!;
        var oldParts = move.FromPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var newParts = move.ToPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var commonSuffix = 0;
        while (commonSuffix < oldParts.Length && commonSuffix < newParts.Length &&
               string.Equals(oldParts[oldParts.Length - commonSuffix - 1],
                   newParts[newParts.Length - commonSuffix - 1], StringComparison.Ordinal))
            commonSuffix++;
        if (commonSuffix == 0 || commonSuffix == oldParts.Length || commonSuffix == newParts.Length)
            return null;

        var fromDirectory = string.Join('/', oldParts.Take(oldParts.Length - commonSuffix));
        var toDirectory = string.Join('/', newParts.Take(newParts.Length - commonSuffix));
        return fromDirectory.Length == 0 || toDirectory.Length == 0
            ? null
            : new(operation, move, fromDirectory, toDirectory);
    }

    private static bool AtOrBelow(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal) ||
        path.StartsWith(root + "/", StringComparison.Ordinal);

    private static string Relative(string root, string path) =>
        path.Length == root.Length ? string.Empty : path[(root.Length + 1)..];

    private static bool Overlaps(string left, string right) =>
        AtOrBelow(left, right) || AtOrBelow(right, left);

    private sealed record Candidate(SyncOperation Operation, MoveDescriptor Move, string FromDirectory, string ToDirectory);
}
