namespace FengSync.Core.Execution;

public enum ResourceKind { UnknownLocal, LocalVolume, Sftp, GoogleDrive, S3, OtherRemote }

/// <summary>
/// Identifies a physical or remote resource so the scheduler can share its
/// concurrency budget across every operation that touches it. Using the volume
/// root for local endpoints is a deliberate first-step simplification; the
/// second-step plan swaps it for a volume GUID and merges physical-disk peers.
/// </summary>
public readonly record struct ResourceKey(ResourceKind Kind, string Identity)
{
    public override string ToString() => $"{Kind}:{Identity}";

    public static ResourceKey For(IEndpoint endpoint) => endpoint switch
    {
        LocalEndpoint local => new(ResourceKind.LocalVolume, VolumeRootFor(local.Root)),
        RcloneEndpoint rclone when rclone.Profile.Type == EndpointType.Sftp => new(ResourceKind.Sftp, rclone.Profile.Remote ?? rclone.Profile.Identity ?? "sftp"),
        RcloneEndpoint rclone when rclone.Profile.Type == EndpointType.GoogleDrive => new(ResourceKind.GoogleDrive, rclone.Profile.Remote ?? "drive"),
        RcloneEndpoint rclone when rclone.Profile.Type == EndpointType.S3 => new(ResourceKind.S3, rclone.Profile.Remote ?? "s3"),
        RcloneEndpoint rclone => new(ResourceKind.OtherRemote, rclone.Profile.Remote ?? "remote"),
        _ => new(ResourceKind.UnknownLocal, endpoint.Profile.Id.ToString("N"))
    };

    private static string VolumeRootFor(string root)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(root)) ?? root; }
        catch { return root; }
    }
}

/// <summary>
/// Concurrency-budget tracker keyed by <see cref="ResourceKey"/>. Operations
/// acquire their source+target budget in a stable order to avoid deadlocks; a
/// single shared budget represents a single physical or remote resource.
/// </summary>
public sealed class ResourceGovernor
{
    private readonly Dictionary<ResourceKey, int> _budgets;
    private readonly Dictionary<ResourceKey, SemaphoreSlim> _gates = new();

    public ResourceGovernor(IReadOnlyDictionary<ResourceKind, int>? budgets = null)
    {
        _budgets = budgets is null
            ? DefaultBudgets().ToDictionary(kv => new ResourceKey(kv.Key, "*"), kv => kv.Value)
            : budgets.ToDictionary(kv => new ResourceKey(kv.Key, "*"), kv => kv.Value);
    }

    public async Task<IDisposable> AcquireAsync(IEnumerable<ResourceKey> keys, CancellationToken ct)
    {
        var ordered = keys.Distinct().OrderBy(k => k, ResourceKeyOrder.Instance).ToArray();
        var acquired = new List<SemaphoreSlim>(ordered.Length);
        try
        {
            foreach (var key in ordered)
            {
                var gate = GateFor(key);
                await gate.WaitAsync(ct);
                acquired.Add(gate);
            }
        }
        catch
        {
            foreach (var gate in acquired) gate.Release();
            throw;
        }
        return new Lease(this, acquired);
    }

    private SemaphoreSlim GateFor(ResourceKey key)
    {
        lock (_gates)
        {
            if (_gates.TryGetValue(key, out var existing)) return existing;
            var capacity = _budgets.TryGetValue(new ResourceKey(key.Kind, "*"), out var configured) ? configured : 1;
            var gate = new SemaphoreSlim(capacity, capacity);
            _gates[key] = gate;
            return gate;
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly ResourceGovernor _owner;
        private readonly List<SemaphoreSlim> _gates;
        public Lease(ResourceGovernor owner, List<SemaphoreSlim> gates) { _owner = owner; _gates = gates; }
        public void Dispose()
        {
            foreach (var gate in _gates) gate.Release();
        }
    }

    private static IReadOnlyDictionary<ResourceKind, int> DefaultBudgets() =>
        Enum.GetValues<ResourceKind>().ToDictionary(kind => kind, _ => 64);

    private sealed class ResourceKeyOrder : IComparer<ResourceKey>
    {
        public static readonly ResourceKeyOrder Instance = new();
        public int Compare(ResourceKey x, ResourceKey y)
        {
            var cmp = ((int)x.Kind).CompareTo((int)y.Kind);
            return cmp != 0 ? cmp : string.CompareOrdinal(x.Identity, y.Identity);
        }
    }
}
