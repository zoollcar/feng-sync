using FengSync.Core.Diagnostics;

namespace FengSync.Core.Scanning;

/// <summary>
/// Captures an endpoint's enumeration into a single immutable snapshot with an
/// O(1) path index. All callers downstream of the planner must consume the index
/// rather than calling FirstOrDefault on Entries, which would re-introduce the
/// O(N) lookup on every operation.
/// </summary>
public static class EndpointSnapshotCapture
{
    public static async Task<EndpointSnapshot> CaptureAsync(IEndpoint endpoint, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var entries = new List<EntrySnapshot>();
        var byPath = new Dictionary<string, EntrySnapshot>(endpoint.Capabilities.EffectivePaths.CreateComparer());
        await foreach (var entry in endpoint.ScanEntriesAsync(ct).ConfigureAwait(false))
        {
            entries.Add(entry);
            byPath[entry.Path] = entry;
        }
        return new EndpointSnapshot
        {
            Endpoint = endpoint.Profile,
            Paths = endpoint.Capabilities.EffectivePaths,
            StartedUtc = started,
            CompletedUtc = DateTimeOffset.UtcNow,
            Entries = entries,
            ByPath = byPath
        };
    }
}

/// <summary>
/// Captures a paired comparison snapshot. The snapshot is the single authority
/// passed to the planner; below this layer no module may call ScanAsync again.
/// </summary>
public sealed class ComparisonSnapshotBuilder
{
    public async Task<ComparisonSnapshot> CaptureAsync(
        IEndpoint left,
        IEndpoint right,
        ComparisonMode mode = ComparisonMode.TimeAndSize,
        TimeSpan timeTolerance = default,
        IReadOnlyList<BaselineEntry>? baseline = null,
        CancellationToken ct = default)
    {
        var captures = await Task.WhenAll(
            EndpointSnapshotCapture.CaptureAsync(left, ct),
            EndpointSnapshotCapture.CaptureAsync(right, ct)).ConfigureAwait(false);
        return new ComparisonSnapshot
        {
            SnapshotId = Guid.NewGuid(),
            Left = captures[0],
            Right = captures[1],
            Mode = mode,
            TimeTolerance = timeTolerance,
            Baseline = baseline,
        };
    }
}
