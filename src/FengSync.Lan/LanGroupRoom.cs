using System.IO.Compression;
using System.Text.Json;
using FengSync.Core;

namespace FengSync.Lan;

/// <summary>What one pull of a room entry decided.</summary>
/// <param name="Entry">The row the bytes belong to. Null when this copy has never heard of that id.</param>
/// <param name="LandedPath">
/// Where the tree or file is on this machine, when the pull succeeded. It is the receive directory's own path,
/// so the window can reveal it without composing anything.
/// </param>
/// <param name="Refusal">
/// <see cref="LanRefusal.None"/> when the bytes landed. Anything else is the reason nobody is holding them here
/// yet, which is what tells the window to keep the 接收 button rather than drop the row.
/// </param>
public sealed record LanRoomFetchEventArgs(LanGroupMessage? Entry, string? LandedPath, LanRefusal Refusal);

/// <summary>How far one pull has got, for a window that must show it rather than leave a button pressed into a silence.</summary>
/// <param name="TotalBytes">
/// The length the entry announced, or 0 when nothing measured it — a folder arrives as one archive whose size the
/// room never agreed on, and a window that was handed 0 has no percentage to draw.
/// </param>
/// <param name="SpeedBytesPerSecond">Measured by <see cref="LanTransferBoard"/>, which the page polls too.</param>
/// <param name="IsDone">The pull settled, either way.</param>
public sealed record LanRoomProgressEventArgs(
    LanGroupMessage Entry,
    long Bytes,
    long TotalBytes,
    double SpeedBytesPerSecond = 0,
    bool IsDone = false);

/// <summary>
/// The reading half of the room: what this machine does on its own to keep up with what the other members said,
/// and to get the bytes behind what they offered.
/// </summary>
/// <remarks>
/// <para>
/// There is no member that owns the history, so every member reads everybody else and converges by itself. One
/// timer per window asks each dialable member for the entries past the number <i>that</i> member's own copy had
/// reached last time, merges what comes back into this machine's log, and remembers the new number only when the
/// reply arrived — a silent member is then re-read from the same place on the next beat rather than having been
/// skipped past.
/// </para>
/// <para>
/// Only a Windows member can be dialled. A phone has no listener, so its sentences reach this copy through the
/// machine it paired with, which is also the machine it pushes its files to.
/// </para>
/// <para>
/// A pull is a read, not a delivery: the entry says a file exists and somebody may hold it, and the bytes are
/// fetched only from here on. They land through <see cref="LanReceiveSink"/>, so an auto-received tree passes the
/// same path rules as a pushed one and an interrupted pull leaves a <c>.fengsync.partial</c> rather than a
/// plausible-looking file.
/// </para>
/// </remarks>
public sealed class LanGroupRoom(
    LanDeviceIdentity self,
    LanPeerClient client,
    LanPeerStore peers,
    LanGroupLog group,
    LanReceiveSink sink,
    LanRoomPresence? presence = null,
    LanTransferBoard? board = null) : IAsyncDisposable
{
    /// <summary>
    /// How often the room is re-read. Short enough that a sentence typed on another machine appears while both
    /// windows are open, long enough that a beat costs one small request per member rather than a conversation.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long one member's report about the room is believed.
    /// </summary>
    /// <remarks>
    /// Five beats, so a member that missed one or two — a busy machine, a dropped packet — does not empty the list
    /// of devices it had told this one about, while a member whose window closed stops being a source at all
    /// within a quarter of a minute.
    /// </remarks>
    public static readonly TimeSpan RelayedMemberLifetime = PollInterval * 5;

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _watermarks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RelayedReport> _reports = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private string _published = "";

    /// <summary>
    /// Where a pull's rate is measured, so the window and the page read the same number. A room built without one
    /// still works and still reports; it simply keeps its own board nobody else can see.
    /// </summary>
    private readonly LanTransferBoard _board = board ?? new LanTransferBoard();

    /// <summary>
    /// One pull at a time. It also closes the one race this class has: a beat that auto-receives an entry and a
    /// person clicking 接收 on the same row would otherwise write the same file twice under two names, because the
    /// first pull's marking is what tells the second it need not happen.
    /// </summary>
    private readonly SemaphoreSlim _pullGate = new(1, 1);

    private Task? _loop;

    /// <summary>
    /// Whether a file or folder another member offered lands here as soon as it is heard of. Off means the entry
    /// waits for a person to press 接收, which is the same distinction the push path makes with its 自动同意 switch
    /// and for the same reason: bytes arriving in the user's directory unasked is the one effect a window cannot undo.
    /// </summary>
    public bool AutoReceive { get; set; }

    /// <summary>
    /// A file or folder this copy just heard about and does not hold yet — the row a 接收 button belongs on. It
    /// fires whether or not auto-receive is on, because a pull that has not been asked for is still an offer.
    /// </summary>
    public event Action<LanGroupMessage>? Offered;

    /// <summary>One pull settled, with the path it landed at or the reason it did not.</summary>
    public event Action<LanRoomFetchEventArgs>? Fetched;

    /// <summary>
    /// How far a pull has got. Raised from the thread that is reading the peer's bytes, so a window that shows it
    /// must cross onto its own; a folder reports its bytes without a total, because a tree announces no size and
    /// the archive that carries it is not the thing the room named.
    /// </summary>
    public event Action<LanRoomProgressEventArgs>? Progress;

    /// <summary>
    /// The members this machine has been told about by the members it reads, rather than by its own agreement or
    /// its own ears. Raised when that set changes, which is what turns another machine's room into an invitation
    /// on this one's screen: a device a member reports is a device worth asking, not one to add quietly.
    /// </summary>
    public event Action? MembersChanged;

    /// <summary>
    /// The union of what each member last reported about the room, or an empty list when nobody has said anything.
    /// </summary>
    /// <remarks>
    /// A report is per-member and replaced whole: if a member stops naming a device, that device leaves the room as
    /// far as that member is concerned. A member that has gone quiet contributes nothing rather than contributing
    /// its last word forever, which is what keeps a device that left from lingering as an invitation.
    /// </remarks>
    public IReadOnlyList<LanMemberWire> RelayedMembers
    {
        get
        {
            lock (_gate)
            {
                var cutoff = DateTimeOffset.UtcNow - RelayedMemberLifetime;
                var merged = new Dictionary<string, LanMemberWire>(StringComparer.OrdinalIgnoreCase);
                foreach (var report in _reports.Values)
                {
                    if (report.HeardUtc < cutoff) continue;
                    foreach (var member in report.Members)
                    {
                        if (string.Equals(member.DeviceId, self.DeviceId, StringComparison.OrdinalIgnoreCase)) continue;
                        merged[member.DeviceId] = member;
                    }
                }

                return [.. merged.Values
                    .OrderBy(member => member.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(member => member.DeviceId, StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    /// <summary>Starts reading the room. Returns immediately; a window that wants its past before the first beat awaits <see cref="PollOnceAsync"/> itself.</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>
    /// Reads every dialable member once, in this machine's order, and pulls what auto-receive asks for.
    /// </summary>
    /// <remarks>
    /// Exposed rather than only reachable from the timer, because the first thing a person wants when a window
    /// opens is the history the room already has, and a poll-then-wait loop gives it to them three seconds late.
    /// </remarks>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        foreach (var row in await MembersAsync(ct).ConfigureAwait(false))
        {
            var member = EndpointFor(row);
            if (member is null) continue;

            LanLogReply reply;
            long watermark;
            lock (_gate) watermark = _watermarks.GetValueOrDefault(row.DeviceId);
            try
            {
                reply = await client.LogAsync(member, watermark, ct).ConfigureAwait(false);
            }
            // A member that is gone, unpaired, or slower than its own timeout is one row of the room missing this
            // beat. It is not the room's failure, and its watermark stays where it was so nothing is skipped.
            catch (LanTransferException) { continue; }
            catch (HttpRequestException) { continue; }
            catch (JsonException) { continue; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }

            foreach (var wire in reply.Entries)
            {
                // False is the normal answer when two members were told the same thing, and a merged text entry
                // has nothing beyond the sentence itself.
                if (!group.TryMerge(wire)) continue;
                if (wire.Kind == LanGroupKind.Text) continue;
                if (string.Equals(wire.AuthorDeviceId, self.DeviceId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!group.TryFind(wire.MessageId, out var entry)) continue;

                Offered?.Invoke(entry);
                if (AutoReceive) await ReceiveAsync(entry.MessageId, ct).ConfigureAwait(false);
            }

            lock (_gate) _watermarks[row.DeviceId] = reply.Head;
            // The reply is this machine's own evidence that the member is up: it answered, on this address, a
            // moment ago. Renewing the lease here is what keeps a computer whose own window is idle in the room —
            // and it is deliberately after the watermark, so a member that answered is never recorded as present
            // on the strength of a read that failed.
            presence?.Seen(new LanRoomMember(row.DeviceId, row.DeviceName, row.Kind, member.Address, member.Port,
                DateTimeOffset.UtcNow, Discovered: false));

            // What this member says it could invite. Kept per member rather than merged on arrival, because the
            // useful fact is "B still offers C", and a union of every report ever received could never forget
            // anybody. Only the invitable half travels: who is already in a room is not something this machine
            // needs told, since it either agreed with them itself or did not.
            if (reply.Invitable is { Count: > 0 } reported)
                lock (_gate) _reports[row.DeviceId] = new RelayedReport(reported, DateTimeOffset.UtcNow);
            else
                lock (_gate) _reports.Remove(row.DeviceId);
        }

        PublishRelayed();
    }

    /// <summary>
    /// Ask the room for the bytes behind one entry and land them, returning what the asking decided.
    /// </summary>
    /// <remarks>
    /// The author is asked first, because it is the member that said it had them. After that every other dialable
    /// member is asked in turn, which is what makes a file survivable when its author has closed its window: an
    /// announcement says what was sent, not who still holds a copy.
    /// </remarks>
    public async Task<LanRoomFetchEventArgs> ReceiveAsync(string messageId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _pullGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!group.TryFind(messageId, out var entry))
                return new LanRoomFetchEventArgs(null, null, LanRefusal.NotFound);

            // Already here is a success rather than a second pull. The path is the one this machine published, so
            // a repeated 接收 cannot hand the user a duplicate under a renamed name.
            if (entry.HeldHere) return new LanRoomFetchEventArgs(entry, entry.LocalPath, LanRefusal.None);

            var result = await PullAsync(entry, ct).ConfigureAwait(false);
            Fetched?.Invoke(result);
            return result;
        }
        finally { _pullGate.Release(); }
    }

    /// <summary>
    /// Every member this machine can reach, as endpoints rather than as records: the same list the poll reads, for
    /// a caller that wants to say something to the room instead of hearing from it.
    /// </summary>
    /// <remarks>
    /// One rule for who is dialable, because a fan-out that reached a member the poll could not read would leave
    /// that member's copy of the room behind, and the two lists would then disagree about what the room is.
    /// </remarks>
    public async Task<IReadOnlyList<LanPeerEndpoint>> ReachableMembersAsync(CancellationToken ct = default)
    {
        var rows = await MembersAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(EndpointFor).Where(member => member is not null).Cast<LanPeerEndpoint>()];
    }

    /// <summary>The members this machine can reach: paired, Windows, and remembered at some address.</summary>
    private async Task<IReadOnlyList<LanPeerRecord>> MembersAsync(CancellationToken ct)
    {
        var rows = await peers.LoadAsync(ct).ConfigureAwait(false);
        return [.. rows.Where(row => row.Kind == LanProtocol.KindWindows
            && !string.Equals(row.DeviceId, self.DeviceId, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// Where to dial one peer row, or <see langword="null"/> when there is nowhere to dial.
    /// </summary>
    /// <remarks>
    /// Newest last is the store's own rule, so the address suggested here is the one that device answered from most
    /// recently — a phone or laptop that moved between access points is still the same member. A row without a
    /// listener port is not dialable at all, which is the honest answer for a phone and for the one-way pairings
    /// this protocol used to write; a caller that needs to know the difference asks about
    /// <see cref="LanPeerRecord.Kind"/> rather than about this method's silence.
    /// </remarks>
    public static LanPeerEndpoint? EndpointFor(LanPeerRecord row) =>
        row.SeenAddresses.Count == 0 || row.Port is < LanOptions.MinimumPort or > LanOptions.MaximumPort
            ? null
            : new LanPeerEndpoint(row.DeviceId, row.SeenAddresses[^1], row.Port, row.DeviceName);

    private async Task<LanRoomFetchEventArgs> PullAsync(LanGroupMessage entry, CancellationToken ct)
    {
        var asked = LanRefusal.NotFound;
        foreach (var member in await HoldersAsync(entry, ct).ConfigureAwait(false))
        {
            try
            {
                var landed = entry.IsDirectory
                    ? await UnpackAsync(member, entry, ct).ConfigureAwait(false)
                    : await TakeFileAsync(member, entry, ct).ConfigureAwait(false);
                var path = sink.AbsolutePathOf(landed);
                group.MarkHeld(entry.MessageId, path);
                return new LanRoomFetchEventArgs(entry, path, LanRefusal.None);
            }
            // The member would not or could not hand the bytes over: the next one gets asked. Anything else is
            // this machine's own answer, and putting the same question to a disk that just said no would only
            // duplicate the refusal under whoever answered last.
            catch (LanTransferException ex) when (RefusesHere(ex.Refusal)) { return new LanRoomFetchEventArgs(entry, null, ex.Refusal); }
            catch (LanTransferException ex) { asked = ex.Refusal; }
            catch (HttpRequestException) { asked = LanRefusal.NotFound; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { asked = LanRefusal.NotFound; }
            // The destination path is longer than this filesystem can publish, which is the receive directory's
            // answer rather than the member's.
            catch (PathTooLongException) { return new LanRoomFetchEventArgs(entry, null, LanRefusal.PathTooLong); }
            // Something in the receive directory already holds that name as something else. That is this machine's
            // answer, and it is named as a conflict rather than left as an exception nobody translates.
            catch (IOException) { return new LanRoomFetchEventArgs(entry, null, LanRefusal.Conflict); }
        }

        return new LanRoomFetchEventArgs(entry, null, asked);
    }

    /// <summary>Every dialable member, the one that spoke the entry first.</summary>
    private async Task<IReadOnlyList<LanPeerEndpoint>> HoldersAsync(LanGroupMessage entry, CancellationToken ct)
    {
        var rows = await MembersAsync(ct).ConfigureAwait(false);
        return [.. rows
            .OrderBy(row => string.Equals(row.DeviceId, entry.AuthorDeviceId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(EndpointFor)
            .Where(member => member is not null)
            .Cast<LanPeerEndpoint>()];
    }

    private Task<string> TakeFileAsync(LanPeerEndpoint member, LanGroupMessage entry, CancellationToken ct) =>
        client.ReadAttachmentAsync(member, entry.MessageId, archive: false, async stream =>
        {
            var progress = ReportFor(member, entry, entry.Bytes);
            try
            {
                // The announced length is the sender's own claim, and the sink treats 0 as "not stated" — the two
                // checks it can make without a claim are the publish and the post-publish size.
                var landed = await sink.ReceiveAsync(stream, entry.RelativePath ?? entry.FileName,
                    entry.Bytes, sourceWriteUtc: null, progress, ct).ConfigureAwait(false);
                progress?.Finish(landed.Bytes, null);
                return landed.RelativePath;
            }
            catch (Exception ex)
            {
                // A pull that broke is still a row on screen, and a row that never closes reads as a transfer that
                // is still going. The refusal the caller answers with is unchanged.
                progress?.Finish(0, ex.Message);
                throw;
            }
        }, ct);

    /// <summary>
    /// Takes a folder as the one archive it was announced to be and lands it entry by entry through the sink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is deliberately no unzip path here. A directory entry becomes <see cref="LanReceiveSink.EnsureDirectoryAsync"/>
    /// and a file entry becomes <see cref="LanReceiveSink.ReceiveAsync"/>, which is the whole reason a pulled tree
    /// cannot escape the receive directory, keep a name already taken, or skip the staging contract — a
    /// <c>ZipArchiveEntry.ExtractToFile</c> would have quietly grown a second, weaker set of those rules.
    /// </para>
    /// <para>
    /// The archive is put on disk before it is opened, because measured against this toolkit a <c>ZipArchive</c>
    /// handed a stream that cannot seek reads the whole stream first: the source position was already at the end of
    /// a 592-byte archive before its first entry was named. A folder pulled over the wire would therefore cost
    /// this process its full size in memory, which is not a thing a desk has. On disk the archive can seek, the
    /// entries come one at a time, and the temporary is the same hidden <c>.fengsync.partial</c> name every other
    /// interrupted write in this feature leaves behind.
    /// </para>
    /// </remarks>
    private Task<string> UnpackAsync(LanPeerEndpoint member, LanGroupMessage entry, CancellationToken ct) =>
        client.ReadAttachmentAsync(member, entry.MessageId, archive: true, async stream =>
        {
            var root = LanRelativePath.RootOf(entry.RelativePath ?? entry.FileName ?? "");
            var staged = await StageArchiveAsync(member, stream, entry, root, ct).ConfigureAwait(false);
            try
            {
                using var file = File.OpenRead(staged);
                using var packed = new ZipArchive(file, ZipArchiveMode.Read);
                foreach (var item in packed.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (item.FullName.EndsWith('/'))
                    {
                        var name = item.FullName[..^1];
                        if (name.Length > 0) await sink.EnsureDirectoryAsync(name, ct).ConfigureAwait(false);
                        continue;
                    }

                    await using var body = item.Open();
                    // No declared length and no timestamp: a pulled file's own mtime belongs to the machine that
                    // made it, and this side has the archive's word for the size rather than the sender's.
                    await sink.ReceiveAsync(body, item.FullName, declaredBytes: 0, sourceWriteUtc: null,
                        progress: null, ct).ConfigureAwait(false);
                }

                return root;
            }
            finally { TryDeleteArchive(staged); }
        }, ct);

    /// <summary>Writes the archive a member is handing over into the receive directory, under a hidden name.</summary>
    private async Task<string> StageArchiveAsync(LanPeerEndpoint member, Stream body, LanGroupMessage entry, string root, CancellationToken ct)
    {
        var full = sink.AbsolutePathOf(SyncInternalPaths.TransferTemporary($"{root}.zip"));
        RoomProgress? reported = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await using var file = File.Create(full);
            // The tree's own announced size is 0, so this reports what has been read against nothing: a window
            // draws it as a pull in flight rather than as a bar stuck at a percentage nobody measured.
            reported = ReportFor(member, entry, totalBytes: 0);
            var buffer = new byte[81920];
            long read = 0;
            int got;
            while ((got = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, got), ct).ConfigureAwait(false);
                read += got;
                reported?.Report(read);
            }

            reported?.Finish(read, null);
            return full;
        }
        catch (Exception ex)
        {
            TryDeleteArchive(full);
            reported?.Finish(0, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// The callback one pull reports into, or <see langword="null"/> when nobody is watching.
    /// </summary>
    /// <remarks>
    /// The rate is sampled by <see cref="LanTransferBoard"/> and this raises only when the board says a tick is due.
    /// That is a change of behaviour worth naming: this used to fire once per 80 KiB read, which is twelve hundred
    /// dispatcher posts a second on a fast wire — the window would spend the transfer laying out instead of
    /// receiving, which is the failure the push path's throttle already existed to prevent.
    /// </remarks>
    private RoomProgress? ReportFor(LanPeerEndpoint member, LanGroupMessage entry, long totalBytes)
    {
        if (Progress is null) return null;

        var key = LanTransferBoard.PullKey(entry.MessageId);
        var contact = new LanContact(member.DeviceId, entry.AuthorName, LanProtocol.KindWindows, member.Address, member.Port);
        _board.Begin(key, LanTransferDirection.Received, contact, entry.FileName ?? entry.RelativePath ?? "",
            totalBytes, messageId: entry.MessageId);
        return new RoomProgress(this, entry, key);
    }

    private sealed class RoomProgress(LanGroupRoom room, LanGroupMessage entry, string key) : IProgress<long>
    {
        public void Report(long value)
        {
            if (room._board.Report(key, value) is not { } tick) return;
            room.Progress?.Invoke(new LanRoomProgressEventArgs(entry, tick.Bytes, tick.TotalBytes,
                tick.BytesPerSecond, tick.Done));
        }

        public void Finish(long bytes, string? error)
        {
            if (room._board.Complete(key, bytes, error) is not { } tick) return;
            room.Progress?.Invoke(new LanRoomProgressEventArgs(entry, tick.Bytes, tick.TotalBytes, 0d, true));
        }
    }

    private static void TryDeleteArchive(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A temporary this application made and could not remove is hidden from every scan by its own name,
            // so it costs a little disk and nothing else.
        }
    }

    /// <summary>Refusals that are this machine's answer rather than a member's, so the next holder is not asked.</summary>
    private static bool RefusesHere(LanRefusal refusal) => refusal
        is LanRefusal.PathUnsafe or LanRefusal.PathTooLong or LanRefusal.NameReserved
        or LanRefusal.Conflict or LanRefusal.VerificationFailed;

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        // Beat first, then wait: a window that opened into a room already talking should show that conversation
        // rather than an empty stream for the length of one interval.
        while (true)
        {
            await PollOnceAsync(ct).ConfigureAwait(false);
            if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) return;
        }
    }

    /// <summary>One member's last word about who is in the room, and when it said it.</summary>
    private sealed record RelayedReport(IReadOnlyList<LanMemberWire> Members, DateTimeOffset HeardUtc);

    /// <summary>
    /// Raises <see cref="MembersChanged"/> when the union of reports is not the one already published.
    /// </summary>
    /// <remarks>
    /// The comparison is over a string of ids and names rather than over the lists, because the beat runs every
    /// three seconds forever and a window that redrew its device list on every beat would be redrawing it for
    /// nothing. Raised outside the lock: a handler is the window's, and it will do whatever it likes.
    /// </remarks>
    private void PublishRelayed()
    {
        var current = RelayedMembers;
        var signature = string.Join('\n', current.Select(member => $"{member.DeviceId}\u0000{member.DeviceName}"));
        lock (_gate)
        {
            if (string.Equals(signature, _published, StringComparison.Ordinal)) return;
            _published = signature;
        }

        MembersChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        // A pull cut short by this cancels inside its own await, and the sink deletes the staging file it had
        // open. The entry keeps its sentence and loses its claim, so a later window may ask again.
        _stop.Cancel();
        if (_loop is { } loop)
        {
            try { await loop.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or LanTransferException) { }
        }

        // Deliberately not disposed: a request still unwinding from the cancelled beat may still read its
        // cancellation token, and an ObjectDisposedException out of a window the user already closed is a crash
        // bought for nothing.
    }
}
