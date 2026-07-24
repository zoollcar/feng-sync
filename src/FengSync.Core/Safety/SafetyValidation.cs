using System.Collections.Concurrent;
using FengSync.Core.Scanning;

namespace FengSync.Core;

public enum SafetySeverity { Warning, Blocking }
public sealed record SafetyIssue(string Code, string Message, SafetySeverity Severity, string? Path = null);
public sealed record SafetyValidationResult(IReadOnlyList<SafetyIssue> Issues)
{
    public static SafetyValidationResult Pass { get; } = new([]);
    public bool HasBlockingIssues => Issues.Any(x => x.Severity == SafetySeverity.Blocking);
    public SafetyValidationResult Combine(SafetyValidationResult other) => new(Issues.Concat(other.Issues).ToList());
}

/// <summary>Human-readable totals shown before executing a potentially destructive plan.</summary>
public sealed record SyncRiskSummary(int Copies, int Overwrites, int Deletes, long TransferBytes)
{
    public static SyncRiskSummary Create(SyncPlan plan, IReadOnlyDictionary<string, EntrySnapshot> left, IReadOnlyDictionary<string, EntrySnapshot> right)
    {
        var selected = plan.Operations.Where(x => x.Selected).ToArray();
        var copies = selected.Count(x => x.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft);
        var overwrites = selected.Count(x => x.Kind == OperationKind.CopyLeftToRight ? right.ContainsKey(x.Path) : x.Kind == OperationKind.CopyRightToLeft && left.ContainsKey(x.Path));
        var bytes = selected.Sum(x => x.Kind == OperationKind.CopyLeftToRight ? left.GetValueOrDefault(x.Path)?.Fingerprint?.Size ?? 0 : x.Kind == OperationKind.CopyRightToLeft ? right.GetValueOrDefault(x.Path)?.Fingerprint?.Size ?? 0 : 0);
        return new(copies, overwrites, selected.Count(x => x.Kind is OperationKind.DeleteLeft or OperationKind.DeleteRight), bytes);
    }
}

public static class SyncConfirmationPolicy
{
    public static bool RequiresConfirmation(SyncRiskSummary summary) => summary.Deletes > 0 || summary.Overwrites > 0;
    public static bool CanOverrideWithProfileName(SafetyValidationResult validation) => validation.Issues.Count > 0 && validation.Issues.All(x => x.Code is "delete.count" or "delete.ratio");
}

public static class PathTopologyValidator
{
    public static SafetyValidationResult Validate(string leftRoot, string rightRoot)
    {
        var left = Canonical(leftRoot); var right = Canonical(rightRoot);
        if (StringComparer.OrdinalIgnoreCase.Equals(left, right)) return Block("endpoint.same", "左右端点不能是同一目录。", left);
        if (Contains(left, right) || Contains(right, left)) return Block("endpoint.nested", "左右端点不能相互嵌套。", left);
        return SafetyValidationResult.Pass;
    }
    internal static bool Contains(string parent, string child) => child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal static SafetyValidationResult Block(string code, string message, string? path = null) => new([new(code, message, SafetySeverity.Blocking, path)]);
}

public static class DeletionGuard
{
    public static SafetyValidationResult Validate(SyncPlan plan, int sourceItemCount, int targetItemCount, SyncMode mode, int maxDeletes, double maxDeleteRatio)
    {
        var deletions = plan.Operations.Count(x => x.Selected && x.Kind is (OperationKind.DeleteLeft or OperationKind.DeleteRight));
        var issues = new List<SafetyIssue>();
        if (mode == SyncMode.Mirror && sourceItemCount == 0 && targetItemCount > 0)
            issues.Add(new("delete.empty-source", "空源镜像会删除目标内容，已阻断。", SafetySeverity.Blocking));
        if (deletions > maxDeletes)
            issues.Add(new("delete.count", $"删除数量 {deletions} 超过阈值 {maxDeletes}。", SafetySeverity.Blocking));
        var ratio = targetItemCount == 0 ? 0 : (double)deletions / targetItemCount;
        if (ratio > maxDeleteRatio)
            issues.Add(new("delete.ratio", $"删除比例 {ratio:P0} 超过阈值 {maxDeleteRatio:P0}。", SafetySeverity.Blocking));
        return new(issues);
    }
}

public sealed record PlanSnapshot(
    SyncPlan Plan,
    IReadOnlyDictionary<Guid, Fingerprint?> SourceFingerprints,
    IReadOnlyDictionary<Guid, Fingerprint?> LeftFingerprints,
    IReadOnlyDictionary<Guid, Fingerprint?> RightFingerprints,
    DateTimeOffset CapturedUtc)
{
    public static async Task<PlanSnapshot> CaptureAsync(SyncPlan plan, IEndpoint left, IEndpoint right, CancellationToken ct = default)
    {
        var scans = await Task.WhenAll(left.ScanAsync(ct), right.ScanAsync(ct));
        var leftEntries = scans[0].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var rightEntries = scans[1].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<Guid, Fingerprint?>();
        var leftFingerprints = new Dictionary<Guid, Fingerprint?>();
        var rightFingerprints = new Dictionary<Guid, Fingerprint?>();
        foreach (var op in plan.Operations)
        {
            leftFingerprints[op.OperationId] = leftEntries.GetValueOrDefault(op.Path)?.Fingerprint;
            rightFingerprints[op.OperationId] = rightEntries.GetValueOrDefault(op.Path)?.Fingerprint;
            if (op.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft)
                sources[op.OperationId] = (op.Kind == OperationKind.CopyLeftToRight ? leftEntries : rightEntries).GetValueOrDefault(op.Path)?.Fingerprint;
        }
        return new(plan, sources, leftFingerprints, rightFingerprints, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Captures the fingerprint map from an existing <see cref="ComparisonSnapshot"/>
    /// rather than re-enumerating either endpoint. This is the recommended path for
    /// every caller below the planner; the legacy ScanAsync overload remains so the
    /// CLI/test plumbing can keep working unchanged.
    /// </summary>
    public static PlanSnapshot FromComparison(SyncPlan plan, ComparisonSnapshot comparison)
    {
        var sources = new Dictionary<Guid, Fingerprint?>();
        var leftFingerprints = new Dictionary<Guid, Fingerprint?>();
        var rightFingerprints = new Dictionary<Guid, Fingerprint?>();
        foreach (var op in plan.Operations)
        {
            leftFingerprints[op.OperationId] = comparison.Left.ByPath.TryGetValue(op.Path, out var l) ? l.Fingerprint : null;
            rightFingerprints[op.OperationId] = comparison.Right.ByPath.TryGetValue(op.Path, out var r) ? r.Fingerprint : null;
            if (op.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft)
            {
                var sourceSide = op.Kind == OperationKind.CopyLeftToRight ? comparison.Left : comparison.Right;
                sources[op.OperationId] = sourceSide.ByPath.TryGetValue(op.Path, out var s) ? s.Fingerprint : null;
            }
        }
        return new(plan, sources, leftFingerprints, rightFingerprints, DateTimeOffset.UtcNow);
    }
}

public sealed class PlanFreshnessValidator
{
    public async Task<SafetyValidationResult> ValidateAsync(PlanSnapshot snapshot, IEndpoint left, IEndpoint right, CancellationToken ct = default)
    {
        // Path of last resort: when the snapshot was not built from a paired
        // ComparisonSnapshot, fall back to full-tree enumeration. The M2 work
        // ensures this branch is only taken by legacy call sites.
        var scans = await Task.WhenAll(left.ScanAsync(ct), right.ScanAsync(ct));
        var leftEntries = scans[0].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var rightEntries = scans[1].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var issues = new List<SafetyIssue>();
        foreach (var op in snapshot.Plan.Operations.Where(x => x.Selected && x.Kind is (OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft)))
        {
            var sourceIsLeft = op.Kind == OperationKind.CopyLeftToRight;
            var current = (sourceIsLeft ? leftEntries : rightEntries).GetValueOrDefault(op.Path)?.Fingerprint;
            var expected = (sourceIsLeft ? snapshot.LeftFingerprints : snapshot.RightFingerprints).GetValueOrDefault(op.OperationId);
            // Drive/SFTP listings can round a modification timestamp differently on two consecutive
            // requests. Hashes still compare exactly; for hashless remote files accept the provider's
            // advertised precision plus a small API serialization margin.
            var source = sourceIsLeft ? left : right;
            var tolerance = source is RcloneEndpoint ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2);
            if (expected is null || current is null || !expected.Matches(current, tolerance))
                issues.Add(new("plan.stale", "源文件在比较后发生变化，请重新比较。", SafetySeverity.Blocking, op.Path));
        }
        return new(issues);
    }

    /// <summary>
    /// Freshness check using only per-path StatAsync calls. The selected source
    /// list is small compared to the full tree so this keeps the M2 guarantee:
    /// the freshness validator must not double the directory scan cost.
    /// </summary>
    public async Task<SafetyValidationResult> ValidateStatAsync(PlanSnapshot snapshot, IEndpoint left, IEndpoint right, int maxParallel = 4, CancellationToken ct = default)
    {
        var selectedCopies = snapshot.Plan.Operations
            .Where(x => x.Selected && x.Kind is (OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft))
            .ToList();
        if (selectedCopies.Count == 0) return SafetyValidationResult.Pass;
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel), Math.Max(1, maxParallel));
        var issues = new ConcurrentBag<SafetyIssue>();
        await Task.WhenAll(selectedCopies.Select(async op =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var sourceIsLeft = op.Kind == OperationKind.CopyLeftToRight;
                var source = sourceIsLeft ? left : right;
                var current = await source.StatAsync(op.Path, ct);
                var expected = (sourceIsLeft ? snapshot.LeftFingerprints : snapshot.RightFingerprints).GetValueOrDefault(op.OperationId);
                var tolerance = source is RcloneEndpoint ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2);
                if (expected is null || current?.Fingerprint is null || !expected.Matches(current.Fingerprint, tolerance))
                    issues.Add(new("plan.stale", "源文件在比较后发生变化，请重新比较。", SafetySeverity.Blocking, op.Path));
            }
            finally { gate.Release(); }
        }));
        return new(issues.ToList());
    }
}

public sealed class StorageCapacityChecker
{
    public SafetyValidationResult ValidateLocalTarget(string targetRoot, long bytesRequired)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(targetRoot));
        var drive = DriveInfo.GetDrives().FirstOrDefault(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, root));
        return drive is not null && drive.AvailableFreeSpace < bytesRequired
            ? PathTopologyValidator.Block("storage.insufficient", "目标磁盘可用空间不足。", targetRoot) : SafetyValidationResult.Pass;
    }
}

public sealed class SafetyValidator
{
    public SafetyValidationResult ValidateConfiguration(string leftRoot, string rightRoot, string? archiveDirectory = null) =>
        PathTopologyValidator.Validate(leftRoot, rightRoot).Combine(string.IsNullOrWhiteSpace(archiveDirectory) ? SafetyValidationResult.Pass : ArchivePathValidator.Validate(archiveDirectory, [leftRoot, rightRoot]));
    public SafetyValidationResult ValidatePlan(SyncPlan plan, int sourceItems, int targetItems, SyncMode mode, int maxDeletes = int.MaxValue, double maxDeleteRatio = 1) =>
        DeletionGuard.Validate(plan, sourceItems, targetItems, mode, maxDeletes, maxDeleteRatio);

    /// <summary>Estimates required space from the immutable plan and checks each local copy target.</summary>
    public SafetyValidationResult ValidateCapacity(SyncPlan plan, IReadOnlyDictionary<string, EntrySnapshot> leftEntries,
        IReadOnlyDictionary<string, EntrySnapshot> rightEntries, IEndpoint left, IEndpoint right)
    {
        long requiredLeft = 0, requiredRight = 0;
        foreach (var operation in plan.Operations.Where(x => x.Selected))
        {
            if (operation.Kind == OperationKind.CopyLeftToRight)
                requiredRight += leftEntries.GetValueOrDefault(operation.Path)?.Fingerprint?.Size ?? 0;
            else if (operation.Kind == OperationKind.CopyRightToLeft)
                requiredLeft += rightEntries.GetValueOrDefault(operation.Path)?.Fingerprint?.Size ?? 0;
        }
        var checker = new StorageCapacityChecker();
        var leftResult = left is LocalEndpoint localLeft ? checker.ValidateLocalTarget(localLeft.Root, requiredLeft) : SafetyValidationResult.Pass;
        var rightResult = right is LocalEndpoint localRight ? checker.ValidateLocalTarget(localRight.Root, requiredRight) : SafetyValidationResult.Pass;
        return leftResult.Combine(rightResult);
    }
}
