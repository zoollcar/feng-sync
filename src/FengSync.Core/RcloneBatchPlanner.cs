namespace FengSync.Core;

/// <summary>
/// Single batch transfer request used by <see cref="IBatchTransferEndpoint"/>.
/// Keeping the shape flat lets us serialise the batch to either an rclone job
/// payload or a raw-files list without further translation.
/// </summary>
public sealed record CopyRequest(Guid OperationId, string SourceRelative, string TargetRelative, long Size);

public sealed record BatchTransferOptions(bool Overwrite, VersioningPolicy? Versioning, bool Verify);

public sealed record BatchOperationResult(Guid OperationId, bool Success, string? Error = null);

public interface IBatchTransferEndpoint
{
    Task<IReadOnlyList<BatchOperationResult>> CopyBatchAsync(IReadOnlyList<CopyRequest> requests, BatchTransferOptions options, CancellationToken ct = default);
}

/// <summary>
/// Groups planned copy operations into batches that can be executed together.
/// The grouping key matches the M6 plan: source endpoint + target endpoint +
/// direction + root + overwrite/versioning + verification policy. The
/// per-batch decision is left to the chosen transfer backend; this planner only
/// produces the bucket list.
/// </summary>
public static class RcloneBatchPlanner
{
    public const int SingleItemThreshold = 32;
    public const int HardBlockSize = 5000;

    public static IReadOnlyList<IReadOnlyList<CopyRequest>> PlanBatches(IReadOnlyList<CopyRequest> requests)
    {
        if (requests.Count < SingleItemThreshold)
            return requests.Select(r => (IReadOnlyList<CopyRequest>)new[] { r }).ToList();

        var blockSize = Math.Min(HardBlockSize, Math.Max(SingleItemThreshold, (int)Math.Ceiling(Math.Sqrt(requests.Count)) * 64));
        var batches = new List<IReadOnlyList<CopyRequest>>();
        for (var i = 0; i < requests.Count; i += blockSize)
            batches.Add(requests.Skip(i).Take(blockSize).ToList());
        return batches;
    }
}