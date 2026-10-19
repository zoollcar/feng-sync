namespace FengSync.Lan;

/// <summary>
/// One transfer as both surfaces read it: the window that drew it and the page that polls for it.
/// </summary>
/// <param name="Id">
/// The key this transfer was opened under. It is stable for the life of one transfer and is what a page uses to
/// attach a row to a file it just picked, so it must never be rebuilt from a display name — a received file can be
/// renamed on the way in, and the row would then never be closed.
/// </param>
/// <param name="TotalBytes">
/// 0 when nothing measured the length: a folder arrives as one archive whose size the room never agreed on, and a
/// bar drawn from a number nobody measured is a lie with a percentage on it.
/// </param>
/// <param name="BytesPerSecond">
/// Measured here and nowhere else. Two surfaces that each sampled their own clock would disagree by a few percent
/// and the user would have no way to know which one to believe.
/// </param>
public sealed record LanTransferSnapshot(
    string Id,
    LanTransferDirection Direction,
    string DeviceId,
    string DeviceName,
    string Path,
    long Bytes,
    long TotalBytes,
    double BytesPerSecond,
    long StartedUnixMilliseconds,
    string? Session,
    string? MessageId,
    bool Done,
    string? Error)
{
    /// <summary>The fraction done, or 0 when the total was never stated.</summary>
    public double Percentage => TotalBytes > 0 ? Math.Min(100d, 100d * Bytes / TotalBytes) : 0d;

    /// <summary>Seconds left, when both a total and a rate exist. Null is "nobody can say".</summary>
    public double? RemainingSeconds => TotalBytes > 0 && BytesPerSecond > 0
        ? Math.Max(0d, (TotalBytes - Bytes) / BytesPerSecond)
        : null;

    /// <summary>Whether a bar can show a fraction rather than an indefinite shimmer.</summary>
    public bool HasTotal => TotalBytes > 0;
}

/// <summary>
/// The one place a transfer's progress and speed are measured.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately passive: it holds no sockets and starts no work. The four paths that move bytes — a peer pushing a
/// file at this machine, this machine serving one back, a folder leaving as a streamed archive, and this machine
/// pulling an entry off the room — each call <see cref="Begin"/>, <see cref="Report"/> and <see cref="Complete"/>
/// as they go. Everything that draws reads from here, so the phone and the window cannot drift apart.
/// </para>
/// <para>
/// Speed is a sliding window rather than a total divided by elapsed time, because a total divided by elapsed time
/// only ever approaches the average: a transfer that has been running for a minute would report the same number
/// while it stalled. Samples are taken at most every <see cref="SampleInterval"/> and read at most every
/// <see cref="MinimumGap"/>, so a hundred-megabyte-per-second pull at 80 KiB a read — twelve hundred calls a second
/// — costs one queue of a couple of dozen numbers and four events.
/// </para>
/// <para>
/// A finished transfer stays readable for <see cref="KeepFinished"/> so a poller that is half a second behind still
/// sees it at 100% instead of watching it vanish at 97%. After that it is gone: this is a live board, not a log —
/// the log is <see cref="LanTransferHistory"/>.
/// </para>
/// </remarks>
public sealed class LanTransferBoard
{
    /// <summary>How far back a speed is measured over. Long enough to smooth a Wi-Fi hiccup, short enough to react.</summary>
    public static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(2);

    /// <summary>The shortest gap between two samples of one transfer.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The shortest gap between two events for one transfer. The same quarter second the listener already used, so
    /// a window lays out four times a second instead of twelve hundred.
    /// </summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a finished row keeps answering, so a poller sees it reach the end.</summary>
    public static readonly TimeSpan KeepFinished = TimeSpan.FromSeconds(8);

    /// <summary>How long a transfer that stopped reporting without finishing is kept before it is swept.</summary>
    public static readonly TimeSpan ForgetIdle = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<long> _clock;

    /// <summary>A board on the machine's own monotonic clock.</summary>
    public LanTransferBoard()
        : this(() => Environment.TickCount64)
    {
    }

    /// <summary>
    /// A board on a clock the caller owns, in milliseconds. Tests drive this by hand; nothing else should.
    /// </summary>
    public LanTransferBoard(Func<long> clock) => _clock = clock;

    /// <summary>
    /// One transfer changed enough to be worth drawing. Raised off the lock, on whichever thread moved the bytes,
    /// so a subscriber that touches a window must cross onto its own dispatcher.
    /// </summary>
    public event Action<LanTransferSnapshot>? Changed;

    /// <summary>The key a pushed file is filed under, which is the same one its completion closes.</summary>
    public static string InboundKey(string deviceId, string path) => "in|" + deviceId + "|" + path;

    /// <summary>The key an outbound delivery is filed under. A message id names one delivery, not one path.</summary>
    public static string OutboundKey(string messageId) => "out|" + messageId;

    /// <summary>The key a pull is filed under, which is the entry it is pulling.</summary>
    public static string PullKey(string messageId) => "pull|" + messageId;

    /// <summary>
    /// Opens a transfer. Calling it again for a key that is still open starts that transfer over rather than
    /// adding a second row: a retried upload is the same row with a fresh window, not two rows at half speed each.
    /// </summary>
    public LanTransferSnapshot Begin(
        string key,
        LanTransferDirection direction,
        LanContact contact,
        string path,
        long totalBytes,
        string? session = null,
        string? messageId = null)
    {
        var now = _clock();
        LanTransferSnapshot snapshot;
        lock (_gate)
        {
            Sweep(now);
            var entry = new Entry
            {
                Key = key,
                Direction = direction,
                DeviceId = contact.DeviceId,
                DeviceName = contact.DeviceName,
                Path = path,
                TotalBytes = Math.Max(0, totalBytes),
                Session = session,
                MessageId = messageId,
                StartedAt = now,
                StartedUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LastReportAt = now,
            };
            entry.Samples.Enqueue(new Sample(now, 0));
            entry.LastSampleAt = now;
            _entries[key] = entry;
            snapshot = Read(entry, now);
        }

        Changed?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Records how many bytes have moved, and answers the row to draw — or <see langword="null"/> when this tick
    /// is too soon after the last one to be worth an event.
    /// </summary>
    public LanTransferSnapshot? Report(string key, long bytes)
    {
        var now = _clock();
        LanTransferSnapshot snapshot;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            entry.LastBytes = Math.Max(entry.LastBytes, bytes);
            entry.LastReportAt = now;
            // A sample every hundred milliseconds is enough to integrate a rate over two seconds, and it keeps the
            // queue bounded no matter how fast the wire is.
            if (now - entry.LastSampleAt >= (long)SampleInterval.TotalMilliseconds)
            {
                entry.Samples.Enqueue(new Sample(now, entry.LastBytes));
                entry.LastSampleAt = now;
                while (entry.Samples.Count > 2 && now - entry.Samples.Peek().At > SpeedWindow.TotalMilliseconds)
                    entry.Samples.Dequeue();
            }

            // The first tick, the last tick, and one every quarter second. A transfer that reported nothing until
            // it finished is not progress, and a bar one step short of the end looks broken.
            var first = entry.Raised == 0;
            var last = entry.TotalBytes > 0 && entry.LastBytes >= entry.TotalBytes;
            var due = now - entry.LastRaisedAt >= (long)MinimumGap.TotalMilliseconds;
            if (!(first || last || due)) return null;

            entry.Raised++;
            entry.LastRaisedAt = now;
            snapshot = Read(entry, now);
        }

        Changed?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Closes a transfer. Always raises, because the row that reaches the end is the one a person is waiting for;
    /// it stays readable for <see cref="KeepFinished"/> and is then swept.
    /// </summary>
    public LanTransferSnapshot? Complete(string key, long bytes, string? error = null)
    {
        var now = _clock();
        LanTransferSnapshot snapshot;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return null;
            entry.LastBytes = Math.Max(entry.LastBytes, bytes);
            entry.LastReportAt = now;
            entry.Done = true;
            entry.Error = error;
            entry.FinishedAt = now;
            entry.Samples.Clear();
            snapshot = Read(entry, now);
        }

        Changed?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Everything worth drawing right now, oldest first. Speed is recomputed against the clock at the moment of
    /// the call, so a transfer that has gone quiet reads as 0 instead of as its last confident number.
    /// </summary>
    public IReadOnlyList<LanTransferSnapshot> Snapshot()
    {
        var now = _clock();
        lock (_gate)
        {
            Sweep(now);
            return [.. _entries.Values.OrderBy(entry => entry.StartedAt).ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => Read(entry, now))];
        }
    }

    /// <summary>Whether anything is still moving, which is what decides how often a page should ask.</summary>
    public bool AnyRunning
    {
        get
        {
            var now = _clock();
            lock (_gate) return _entries.Values.Any(entry => !entry.Done && now - entry.LastReportAt <= 1000);
        }
    }

    private static LanTransferSnapshot Read(Entry entry, long now) => new(
        entry.Key,
        entry.Direction,
        entry.DeviceId,
        entry.DeviceName,
        entry.Path,
        entry.LastBytes,
        entry.TotalBytes,
        entry.Done ? 0d : Rate(entry, now),
        entry.StartedUnixMilliseconds,
        entry.Session,
        entry.MessageId,
        entry.Done,
        entry.Error);

    /// <summary>
    /// Bytes per second over the sampling window, or 0 when the transfer has gone quiet.
    /// </summary>
    /// <remarks>
    /// The quiet test is the half that makes the number honest. A phone that walked out of Wi-Fi range leaves the
    /// socket open and the last reading is still whatever it was when the bytes stopped; without this, a dead
    /// transfer would show a healthy 12 MB/s forever.
    /// </remarks>
    private static double Rate(Entry entry, long now)
    {
        if (entry.Samples.Count < 2) return 0d;
        if (now - entry.LastReportAt > 1000) return 0d;

        var first = entry.Samples.Peek();
        var last = entry.Samples.Last();
        var seconds = (last.At - first.At) / 1000d;
        return seconds <= 0d ? 0d : Math.Max(0d, (last.Bytes - first.Bytes) / seconds);
    }

    /// <summary>Drops what has outlived its use. Called from every entry point so nothing has to own a timer.</summary>
    private void Sweep(long now)
    {
        List<string>? gone = null;
        foreach (var (key, entry) in _entries)
        {
            var expired = entry.Done
                ? now - entry.FinishedAt > (long)KeepFinished.TotalMilliseconds
                : now - entry.LastReportAt > (long)ForgetIdle.TotalMilliseconds;
            if (!expired) continue;
            (gone ??= []).Add(key);
        }

        if (gone is null) return;
        foreach (var key in gone) _entries.Remove(key);
    }

    private readonly record struct Sample(long At, long Bytes);

    private sealed class Entry
    {
        public required string Key { get; init; }

        public required LanTransferDirection Direction { get; init; }

        public required string DeviceId { get; init; }

        public required string DeviceName { get; init; }

        public required string Path { get; init; }

        public required long StartedAt { get; init; }

        public required long StartedUnixMilliseconds { get; init; }

        public long TotalBytes { get; init; }

        public string? Session { get; init; }

        public string? MessageId { get; init; }

        public long LastBytes { get; set; }

        public long LastReportAt { get; set; }

        public long LastSampleAt { get; set; }

        public long LastRaisedAt { get; set; }

        public long FinishedAt { get; set; }

        public int Raised { get; set; }

        public bool Done { get; set; }

        public string? Error { get; set; }

        public Queue<Sample> Samples { get; } = new();
    }
}
