namespace FengSync.Core.Scanning;

public enum HashAlgorithmId { Sha256, Sha1, Md5 }
public sealed record ContentDigest(HashAlgorithmId Algorithm, string Hex);

/// <summary>
/// Default comparison strategy. The TimeAndSize default avoids reading file
/// content on initial comparison; Content forces a streaming hash for files with
/// matching size/time.
/// </summary>
public enum ComparisonMode { TimeAndSize, SizeOnly, Content }

/// <summary>
/// Endpoint-scoped snapshot. Contains the metadata produced by a single enumeration
/// of the endpoint plus an index for O(1) path lookup. Downstream code must not
/// iterate the entries list to find a path; that defeats the snapshot's purpose.
/// </summary>
public sealed class EndpointSnapshot
{
    public required EndpointProfile Endpoint { get; init; }
    public required EndpointPathSemantics Paths { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset CompletedUtc { get; init; }
    public required IReadOnlyList<EntrySnapshot> Entries { get; init; }
    public required IReadOnlyDictionary<string, EntrySnapshot> ByPath { get; init; }
}

/// <summary>
/// Plan-wide snapshot pair plus the loaded baseline. The comparison snapshot is
/// the single authority handed to the planner, verifier and baseline commit; no
/// stage below the planner is allowed to re-enumerate either endpoint.
/// </summary>
public sealed class ComparisonSnapshot
{
    public required Guid SnapshotId { get; init; }
    public required EndpointSnapshot Left { get; init; }
    public required EndpointSnapshot Right { get; init; }
    public required ComparisonMode Mode { get; init; }
    public required TimeSpan TimeTolerance { get; init; }
    public IReadOnlyList<BaselineEntry>? Baseline { get; init; }
    public SyncPlan? Plan { get; set; }
}
