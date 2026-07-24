using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using FengSync.Core.Diagnostics;

namespace FengSync.Core;

/// <summary>
/// Append-only WAL journal writer. The drain task only starts once
/// <see cref="BeginRunAsync"/> has opened the events stream, so events
/// appended after BeginAsync are guaranteed to be consumed before
/// <see cref="CompleteRunAsync"/> returns. A write failure surfaces as a
/// run fault and any further <see cref="AppendAsync"/> call throws.
/// </summary>
public sealed class JournalWalStore : IAsyncDisposable
{
    private readonly string _root;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly Channel<JournalEvent> _channel;
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private Task? _drain;
    private readonly CancellationTokenSource _cts = new();
    private FileStream? _eventsStream;
    private string? _currentHeaderPath;
    private long _appendCount;
    private long _flushCount;
    private long _sequence;
    private volatile bool _failed;
    private readonly ConcurrentQueue<JournalEvent> _completedBoundaries = new();

    public JournalWalStore(string? root = null, int batchSize = 64, TimeSpan? flushInterval = null)
    {
        _root = root ?? Path.Combine(AppDataPaths.Root, "journals");
        _batchSize = Math.Max(1, batchSize);
        _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(100);
        _channel = Channel.CreateBounded<JournalEvent>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public long AppendCount => Interlocked.Read(ref _appendCount);
    public long FlushCount => Interlocked.Read(ref _flushCount);
    public string? CurrentHeaderPath => _currentHeaderPath;
    public string EventsPath => _currentHeaderPath is null ? "" : JournalRecoveryReader.EventsPathForHeader(_currentHeaderPath);
    public bool IsFailed => _failed;

    public async Task BeginRunAsync(string runId, JournalHeader header, CancellationToken ct = default)
    {
        EnsureWritable();
        Directory.CreateDirectory(_root);
        var headerPath = Path.Combine(_root, runId + ".header.json");
        await File.WriteAllTextAsync(headerPath, JsonSerializer.Serialize(header), ct);
        _currentHeaderPath = headerPath;
        _eventsStream = new FileStream(Path.Combine(_root, runId + ".events.jsonl"),
            FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Interlocked.Exchange(ref _sequence, 0);
        // Lazily start the drain task now that the stream is open. The
        // constructor deliberately does not start it; if it did it would
        // return immediately because _eventsStream was null, and subsequent
        // Append calls would silently enqueue into a channel nobody reads.
        await _drainGate.WaitAsync(ct);
        try
        {
            if (_drain is null || _drain.IsCompleted)
                _drain = Task.Run(() => DrainAsync(_cts.Token));
        }
        finally { _drainGate.Release(); }
        SyncRunMetricsHub.Current.IncrementJournalFlush();
    }

    public async Task AppendAsync(JournalEvent ev, CancellationToken ct = default)
    {
        if (_eventsStream is null) throw new InvalidOperationException("Journal run has not been started; call BeginRunAsync first.");
        if (_failed) throw new InvalidOperationException("Journal writer is in a failed state; the run must be aborted and recovered.");
        ev.Seq = Interlocked.Increment(ref _sequence);
        await _channel.Writer.WriteAsync(ev, ct);
        Interlocked.Increment(ref _appendCount);
        SyncRunMetricsHub.Current.IncrementJournalAppend();
        if (ev.Kind is JournalEventKind.OperationCommitted or JournalEventKind.BaselineCommitted or JournalEventKind.RunCompleted)
        {
            // The drain loop signals the durability barrier after flushing.
            // We just record the boundary here.
            _completedBoundaries.Enqueue(ev);
        }
    }

    /// <summary>
    /// Awaits a durability barrier — the call returns only after every event
    /// already in the channel has been flushed to the events.jsonl file. Use
    /// it before publishing the baseline so the recovery journal is durable
    /// before the published state becomes visible.
    /// </summary>
    public Task AwaitDurabilityAsync(CancellationToken ct = default)
    {
        if (_eventsStream is null) return Task.CompletedTask;
        var tcs = new TaskCompletionSource();
        _barrierSignals.Enqueue(tcs);
        return Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, ct));
    }

    private void SignalBarrier()
    {
        while (_barrierSignals.TryDequeue(out var tcs))
            tcs.TrySetResult();
    }

    private readonly ConcurrentQueue<TaskCompletionSource> _barrierSignals = new();

    public async Task CompleteRunAsync(JournalSummary summary, CancellationToken ct = default)
    {
        if (_currentHeaderPath is null) return;
        try
        {
            // Drain the channel and flush all queued events before publishing
            // the summary. The drain loop sees channel completion as the
            // signal to exit, so completing here is safe.
            _channel.Writer.TryComplete();
            if (_drain is not null)
            {
                try { await _drain.ConfigureAwait(false); } catch { /* recorded as failure below */ }
            }
            if (_failed) throw new InvalidOperationException("Journal writer failed before completion; the run must be recovered.");
            var summaryPath = Path.Combine(_root, Path.GetFileNameWithoutExtension(_currentHeaderPath) + ".summary.json.tmp");
            var finalSummaryPath = Path.Combine(_root, Path.GetFileNameWithoutExtension(_currentHeaderPath) + ".summary.json");
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary), ct);
            File.Move(summaryPath, finalSummaryPath, true);
        }
        finally
        {
            if (_eventsStream is not null) { try { await _eventsStream.DisposeAsync(); } catch { } _eventsStream = null; }
            _completedBoundaries.Clear();
            _currentHeaderPath = null;
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        if (_eventsStream is null) return;
        var stream = _eventsStream;
        var buffer = new List<JournalEvent>(_batchSize);
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct))
            {
                buffer.Clear();
                while (buffer.Count < _batchSize && _channel.Reader.TryRead(out var ev)) buffer.Add(ev);
                foreach (var ev in buffer)
                {
                    var line = JsonSerializer.Serialize(ev) + "\n";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    await stream.WriteAsync(bytes, ct);
                }
                if (buffer.Count > 0)
                {
                    await stream.FlushAsync(ct);
                    Interlocked.Increment(ref _flushCount);
                    SyncRunMetricsHub.Current.IncrementJournalFlush();
                    SignalBarrier();
                }
                await Task.Delay(_flushInterval, ct);
            }
            // Final flush on graceful shutdown.
            await stream.FlushAsync(ct);
            Interlocked.Increment(ref _flushCount);
            SyncRunMetricsHub.Current.IncrementJournalFlush();
            SignalBarrier();
        }
        catch (Exception ex)
        {
            _failed = true;
            // Push the failure into any awaiters so the run aborts rather than
            // pretending the journal is durable.
            SignalBarrier(fault: ex);
        }
    }

    private void SignalBarrier(Exception? fault = null)
    {
        while (_barrierSignals.TryDequeue(out var tcs))
        {
            if (fault is null) tcs.TrySetResult();
            else tcs.TrySetException(fault);
        }
    }

    private void EnsureWritable()
    {
        if (_currentHeaderPath is not null) throw new InvalidOperationException("Journal run is already active; complete it before starting another.");
        if (_failed) throw new InvalidOperationException("Journal writer previously failed; restart the run with a fresh store.");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        if (_drain is not null) { try { await _drain; } catch { } }
        if (_eventsStream is not null) { try { await _eventsStream.DisposeAsync(); } catch { } }
        _cts.Dispose();
        _drainGate.Dispose();
    }
}

public enum JournalEventKind
{
    RunStarted,
    OperationStarted,
    TemporaryCreated,
    OperationCommitted,
    OperationFailed,
    OperationCancelled,
    BaselineStarted,
    BaselineCommitted,
    RunCompleted
}

public sealed record JournalHeader(
    int FormatVersion,
    string RunId,
    DateTimeOffset CreatedUtc,
    string? ProfileId,
    EndpointIdentity Left,
    EndpointIdentity Right,
    string SnapshotId,
    IReadOnlyList<JournalOperation> Operations);

public sealed record JournalOperation(string Id, string Path, string Kind, long Size);

public sealed record JournalEvent
{
    public long Seq { get; set; }
    public DateTimeOffset Utc { get; init; } = DateTimeOffset.UtcNow;
    public JournalEventKind Kind { get; init; }
    public string? OperationId { get; init; }
    public string? TemporaryPath { get; init; }
    public long? Bytes { get; init; }
    public string? Error { get; init; }
}

public sealed record JournalSummary(string RunId, DateTimeOffset CompletedUtc, int Succeeded, int Failed, int Cancelled);

/// <summary>
/// Reads both the legacy single-file <see cref="SyncJournal"/> and the WAL
/// header/events pair. Truncated tail lines are skipped silently, but any
/// non-tail corruption, repeated sequence number or sequence gap surfaces as a
/// recovery entry so the UI can refuse to commit destructive cleanup.
/// </summary>
public static class JournalRecoveryReader
{
    public static async Task<IReadOnlyList<SyncJournal>> LoadIncompleteAsync(string? root = null, CancellationToken ct = default)
    {
        var directory = root ?? Path.Combine(AppDataPaths.Root, "jobs");
        var walRoot = root ?? Path.Combine(AppDataPaths.Root, "journals");
        var results = new List<SyncJournal>();
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    var item = JsonSerializer.Deserialize<SyncJournal>(await File.ReadAllTextAsync(path, ct));
                    if (item?.Items?.Any(x => x.State is not JournalState.Committed) == true)
                        results.Add(item);
                }
                catch (InvalidDataException) { /* skip corrupted file; recovery surfaces the issue */ }
            }
        }
        if (Directory.Exists(walRoot))
        {
            foreach (var headerPath in Directory.EnumerateFiles(walRoot, "*.header.json"))
            {
                try
                {
                    var header = JsonSerializer.Deserialize<JournalHeader>(await File.ReadAllTextAsync(headerPath, ct));
                    if (header is null) continue;
                    var eventsPath = EventsPathForHeader(headerPath);
                    var (items, fault) = await ReadWalEventsAsync(eventsPath, header, ct);
                    if (fault is not null)
                        items = WithRecoveryFault(items, fault);
                    if (items.Any(x => x.State is not JournalState.Committed))
                        results.Add(new SyncJournal(Guid.Parse(header.RunId), header.CreatedUtc, items, null));
                }
                catch (InvalidDataException) { }
            }
        }
        return results;
    }

    private static IReadOnlyList<JournalItem> WithRecoveryFault(IReadOnlyList<JournalItem> items, string fault)
    {
        if (items.Count == 0)
            return [new JournalItem(Guid.Empty, "(journal)", default, JournalState.Failed, fault)];
        var copy = items.ToList();
        copy[0] = copy[0] with { State = JournalState.Failed, Error = fault };
        return copy;
    }

    private static async Task<(IReadOnlyList<JournalItem> Items, string? Fault)> ReadWalEventsAsync(string eventsPath, JournalHeader header, CancellationToken ct)
    {
        var latestState = header.Operations.ToDictionary(x => x.Id, _ => JournalState.Pending, StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long previousSeq = 0;
        bool seqGap = false;
        bool seqDup = false;
        if (File.Exists(eventsPath))
        {
            var endsWithNewline = await EndsWithNewlineAsync(eventsPath, ct);
            using var reader = new StreamReader(eventsPath);
            string? line;
            var pending = new List<string>();
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                pending.Add(line);
            }
            // Process all-but-last only when the file ends without a newline:
            // the tail line might be a torn write that the next Append will
            // rewrite. The rest are accepted as authoritative.
            var trailing = pending.Count > 0 && !endsWithNewline
                ? new List<string> { pending[^1] }
                : new List<string>();
            var authoritative = trailing.Count > 0 ? pending.Take(pending.Count - 1).ToList() : pending;
            foreach (var raw in authoritative)
            {
                JournalEvent? ev;
                try { ev = JsonSerializer.Deserialize<JournalEvent>(raw); }
                catch (JsonException) { return (header.Operations.Select(_ => new JournalItem(Guid.Empty, "", default, JournalState.Pending)).ToList(), "事件行损坏，无法解析。"); }
                if (ev is null) continue;
                if (ev.Seq <= previousSeq) seqDup = true;
                else if (ev.Seq != previousSeq + 1) seqGap = true;
                previousSeq = ev.Seq;
                if (ev.OperationId is null || !latestState.ContainsKey(ev.OperationId)) continue;
                latestState[ev.OperationId] = ev.Kind switch
                {
                    JournalEventKind.OperationStarted => JournalState.Running,
                    JournalEventKind.TemporaryCreated => JournalState.Running,
                    JournalEventKind.OperationCommitted => JournalState.Committed,
                    JournalEventKind.OperationFailed => JournalState.Failed,
                    JournalEventKind.OperationCancelled => JournalState.Cancelled,
                    _ => latestState[ev.OperationId]
                };
                if (!string.IsNullOrEmpty(ev.Error)) errors[ev.OperationId] = ev.Error!;
            }
        }
        var opKind = new Dictionary<string, OperationKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in header.Operations)
            if (Enum.TryParse<OperationKind>(op.Kind, out var k)) opKind[op.Id] = k;
        var list = new List<JournalItem>(header.Operations.Count);
        foreach (var op in header.Operations)
        {
            list.Add(new JournalItem(Guid.Parse(op.Id), op.Path, opKind.GetValueOrDefault(op.Id),
                latestState.GetValueOrDefault(op.Id, JournalState.Pending), errors.GetValueOrDefault(op.Id)));
        }
        string? fault = null;
        if (seqGap) fault = "事件序号跳跃，无法重建确定状态。";
        else if (seqDup) fault = "事件序号重复，journal 可能被破坏。";
        return (list, fault);
    }

    private static async Task<bool> EndsWithNewlineAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length == 0) return true;
        stream.Seek(-1, SeekOrigin.End);
        var buffer = new byte[1];
        return await stream.ReadAsync(buffer, ct) == 1 && buffer[0] == (byte)'\n';
    }

    internal static string EventsPathForHeader(string headerPath)
    {
        const string headerSuffix = ".header.json";
        return headerPath.EndsWith(headerSuffix, StringComparison.OrdinalIgnoreCase)
            ? headerPath[..^headerSuffix.Length] + ".events.jsonl"
            : Path.ChangeExtension(headerPath, ".events.jsonl");
    }
}
