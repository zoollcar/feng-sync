namespace FengSync.Core.Diagnostics;

/// <summary>
/// Lightweight in-process counters for sync runs. The M0 perf plan keeps all updates
/// interlocked and avoids string formatting on the hot path so enabling metrics has
/// negligible cost. The same instance flows from planning through execution so the
/// counters capture the entire run rather than each subsystem independently.
/// </summary>
public sealed class SyncRunMetrics
{
    private long _directoryScans;
    private long _entriesEnumerated;
    private long _statCalls;
    private long _rcRequests;

    public long DirectoryScans => Interlocked.Read(ref _directoryScans);
    public long EntriesEnumerated => Interlocked.Read(ref _entriesEnumerated);
    public long StatCalls => Interlocked.Read(ref _statCalls);
    public long RcRequests => Interlocked.Read(ref _rcRequests);

    public void IncrementDirectoryScan() => Interlocked.Increment(ref _directoryScans);
    public void AddEntriesEnumerated(long count) => Interlocked.Add(ref _entriesEnumerated, count);
    public void IncrementStatCall() => Interlocked.Increment(ref _statCalls);
    public void IncrementRcRequest() => Interlocked.Increment(ref _rcRequests);
}

/// <summary>
/// Process-wide metrics singleton. Tests can capture the snapshot and assert on
/// the counters; production code reports the snapshot through the run history
/// detail field but never blocks on it.
/// </summary>
public static class SyncRunMetricsHub
{
    private static readonly AsyncLocal<SyncRunMetrics?> _current = new();
    public static SyncRunMetrics Current => _current.Value ??= new SyncRunMetrics();
    public static IDisposable BeginScope(SyncRunMetrics metrics)
    {
        var previous = _current.Value;
        _current.Value = metrics;
        return new Restore(previous);
    }
    private sealed class Restore : IDisposable
    {
        private readonly SyncRunMetrics? _previous;
        public Restore(SyncRunMetrics? previous) { _previous = previous; }
        public void Dispose() => _current.Value = _previous;
    }
}
