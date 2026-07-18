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

public sealed record PlanSnapshot(SyncPlan Plan, IReadOnlyDictionary<Guid, Fingerprint?> SourceFingerprints, DateTimeOffset CapturedUtc)
{
    public static async Task<PlanSnapshot> CaptureAsync(SyncPlan plan, IEndpoint left, IEndpoint right, CancellationToken ct = default)
    {
        var scans = await Task.WhenAll(left.ScanAsync(ct), right.ScanAsync(ct));
        var leftEntries = scans[0].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var rightEntries = scans[1].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<Guid, Fingerprint?>();
        foreach (var op in plan.Operations.Where(x => x.Kind is OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft))
        {
            var entries = op.Kind == OperationKind.CopyLeftToRight ? leftEntries : rightEntries;
            sources[op.OperationId] = entries.GetValueOrDefault(op.Path)?.Fingerprint;
        }
        return new(plan, sources, DateTimeOffset.UtcNow);
    }
}

public sealed class PlanFreshnessValidator
{
    public async Task<SafetyValidationResult> ValidateAsync(PlanSnapshot snapshot, IEndpoint left, IEndpoint right, CancellationToken ct = default)
    {
        var scans = await Task.WhenAll(left.ScanAsync(ct), right.ScanAsync(ct));
        var leftEntries = scans[0].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var rightEntries = scans[1].ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var issues = new List<SafetyIssue>();
        foreach (var op in snapshot.Plan.Operations.Where(x => x.Selected && x.Kind is (OperationKind.CopyLeftToRight or OperationKind.CopyRightToLeft)))
        {
            var current = (op.Kind == OperationKind.CopyLeftToRight ? leftEntries : rightEntries).GetValueOrDefault(op.Path)?.Fingerprint;
            if (!snapshot.SourceFingerprints.TryGetValue(op.OperationId, out var expected) || expected is null || current is null || !expected.Matches(current))
                issues.Add(new("plan.stale", "源文件在比较后发生变化，请重新比较。", SafetySeverity.Blocking, op.Path));
        }
        return new(issues);
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
