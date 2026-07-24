using System.Diagnostics;

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
    private long _hashFiles;
    private long _hashBytes;
    private long _rcRequests;
    private long _journalAppends;
    private long _journalFlushes;
    private long _baselineReads;
    private long _baselineWrites;
    private long _bytesRead;
    private long _bytesWritten;

    public long DirectoryScans => Interlocked.Read(ref _directoryScans);
    public long EntriesEnumerated => Interlocked.Read(ref _entriesEnumerated);
    public long StatCalls => Interlocked.Read(ref _statCalls);
    public long HashFiles => Interlocked.Read(ref _hashFiles);
    public long HashBytes => Interlocked.Read(ref _hashBytes);
    public long RcRequests => Interlocked.Read(ref _rcRequests);
    public long JournalAppends => Interlocked.Read(ref _journalAppends);
    public long JournalFlushes => Interlocked.Read(ref _journalFlushes);
    public long BaselineReads => Interlocked.Read(ref _baselineReads);
    public long BaselineWrites => Interlocked.Read(ref _baselineWrites);
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public void IncrementDirectoryScan() => Interlocked.Increment(ref _directoryScans);
    public void AddEntriesEnumerated(long count) => Interlocked.Add(ref _entriesEnumerated, count);
    public void IncrementStatCall() => Interlocked.Increment(ref _statCalls);
    public void IncrementHashFile() => Interlocked.Increment(ref _hashFiles);
    public void AddHashBytes(long count) => Interlocked.Add(ref _hashBytes, count);
    public void IncrementRcRequest() => Interlocked.Increment(ref _rcRequests);
    public void IncrementJournalAppend() => Interlocked.Increment(ref _journalAppends);
    public void IncrementJournalFlush() => Interlocked.Increment(ref _journalFlushes);
    public void IncrementBaselineRead() => Interlocked.Increment(ref _baselineReads);
    public void IncrementBaselineWrite() => Interlocked.Increment(ref _baselineWrites);
    public void AddBytesRead(long count) => Interlocked.Add(ref _bytesRead, count);
    public void AddBytesWritten(long count) => Interlocked.Add(ref _bytesWritten, count);

    public SyncRunMetricsSnapshot Snapshot() => new(
        DirectoryScans, EntriesEnumerated, StatCalls, HashFiles, HashBytes, RcRequests,
        JournalAppends, JournalFlushes, BaselineReads, BaselineWrites, BytesRead, BytesWritten);

    public void Reset()
    {
        Interlocked.Exchange(ref _directoryScans, 0);
        Interlocked.Exchange(ref _entriesEnumerated, 0);
        Interlocked.Exchange(ref _statCalls, 0);
        Interlocked.Exchange(ref _hashFiles, 0);
        Interlocked.Exchange(ref _hashBytes, 0);
        Interlocked.Exchange(ref _rcRequests, 0);
        Interlocked.Exchange(ref _journalAppends, 0);
        Interlocked.Exchange(ref _journalFlushes, 0);
        Interlocked.Exchange(ref _baselineReads, 0);
        Interlocked.Exchange(ref _baselineWrites, 0);
        Interlocked.Exchange(ref _bytesRead, 0);
        Interlocked.Exchange(ref _bytesWritten, 0);
    }
}

public sealed record SyncRunMetricsSnapshot(
    long DirectoryScans,
    long EntriesEnumerated,
    long StatCalls,
    long HashFiles,
    long HashBytes,
    long RcRequests,
    long JournalAppends,
    long JournalFlushes,
    long BaselineReads,
    long BaselineWrites,
    long BytesRead,
    long BytesWritten);

/// <summary>
/// Fixed phase identifiers shared between the planner, executor, journal writer
/// and history records. Phase names are part of the diagnostic contract and must
/// remain stable across releases so historical comparisons stay meaningful.
/// </summary>
public static class SyncPhaseNames
{
    public const string EndpointOpen = "endpoint.open";
    public const string ScanLeft = "scan.left";
    public const string ScanRight = "scan.right";
    public const string BaselineLoad = "baseline.load";
    public const string ComparePlan = "compare.plan";
    public const string SafetyValidate = "safety.validate";
    public const string FreshnessValidate = "freshness.validate";
    public const string Transfer = "transfer";
    public const string Verify = "verify";
    public const string Delete = "delete";
    public const string BaselineCommit = "baseline.commit";
    public const string JournalFinalize = "journal.finalize";
}

/// <summary>
/// Stopwatch-based phase timer. Uses <see cref="Stopwatch.GetTimestamp"/> to avoid
/// allocation, and never formats the elapsed string — the consumer decides whether
/// and how to display it.
/// </summary>
public struct PhaseTimer
{
    private long _startTicks;
    public string Phase { get; }
    public PhaseTimer(string phase) { Phase = phase; _startTicks = Stopwatch.GetTimestamp(); }
    public TimeSpan Elapsed()
    {
        var elapsed = Stopwatch.GetElapsedTime(_startTicks);
        // Reset so a single timer can be read multiple times in tests without losing accuracy.
        _startTicks = Stopwatch.GetTimestamp();
        return elapsed;
    }
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