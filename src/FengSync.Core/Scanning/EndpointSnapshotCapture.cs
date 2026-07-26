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
        var entries = await endpoint.ScanAsync(ct).ConfigureAwait(false);
        var byPath = new Dictionary<string, EntrySnapshot>(entries.Count, endpoint.Capabilities.EffectivePaths.CreateComparer());
        foreach (var entry in entries) byPath[entry.Path] = entry;
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
    private readonly Func<IEndpoint, Task<IReadOnlyList<BaselineEntry>?>>? _baselineLoader;
    public ComparisonSnapshotBuilder(Func<IEndpoint, IEndpoint, Task<IReadOnlyList<BaselineEntry>?>>? baselineLoader = null)
    {
        if (baselineLoader is not null)
        {
            _baselineLoader = (endpoint) => baselineLoader(endpoint, endpoint);
        }
    }

    public async Task<ComparisonSnapshot> CaptureAsync(
        IEndpoint left,
        IEndpoint right,
        ComparisonMode mode = ComparisonMode.TimeAndSize,
        TimeSpan timeTolerance = default,
        IReadOnlyList<BaselineEntry>? baseline = null,
        CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var leftSnapshot = await EndpointSnapshotCapture.CaptureAsync(left, ct).ConfigureAwait(false);
        var rightSnapshot = await EndpointSnapshotCapture.CaptureAsync(right, ct).ConfigureAwait(false);
        return new ComparisonSnapshot
        {
            SnapshotId = Guid.NewGuid(),
            Left = leftSnapshot,
            Right = rightSnapshot,
            Mode = mode,
            TimeTolerance = timeTolerance,
            Baseline = baseline,
        };
    }
}

/// <summary>
/// Resolves the entries relevant to a single planned operation against a paired
/// comparison snapshot. The executor must call this instead of <see cref="IEndpoint.ScanAsync"/>
/// so pre-execution freshness checks do not trigger a full re-enumeration.
/// </summary>
public static class ComparisonSnapshotLookup
{
    public static EntrySnapshot? Source(this ComparisonSnapshot snapshot, Guid operationId, SyncOperation operation)
    {
        var isCopy = operation.Kind is OperationKind.CopyLeftToRight or OperationKind.CreateRightDirectory;
        var target = isCopy ? snapshot.Left : snapshot.Right;
        return Resolve(target, operation.Path);
    }

    public static EntrySnapshot? Target(this ComparisonSnapshot snapshot, Guid operationId, SyncOperation operation)
    {
        var isCopy = operation.Kind is OperationKind.CopyLeftToRight or OperationKind.CreateRightDirectory;
        var target = isCopy ? snapshot.Right : snapshot.Left;
        return Resolve(target, operation.Path);
    }

    public static EntrySnapshot? Resolve(EndpointSnapshot snapshot, string path)
        => snapshot.ByPath.TryGetValue(path, out var entry) ? entry : null;
}
