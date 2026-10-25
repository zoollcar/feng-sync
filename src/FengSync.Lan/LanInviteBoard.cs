namespace FengSync.Lan;

/// <summary>
/// What became of one invitation, from the point of view of the machine that received it.
/// </summary>
/// <remarks>
/// <see cref="Pending"/> is a state rather than the absence of one, because the board genuinely holds it: an
/// invitation sits there being unanswered for up to ninety seconds and every reader has to be able to say so.
/// The other four are final, and the board keeps them for a while after closing so a second answer or a late
/// withdrawal is answered honestly instead of being reported as an id nobody has heard of.
/// </remarks>
public enum LanInviteDecision
{
    Pending = 0,
    Accepted,
    Declined,
    Expired,
    Withdrawn,
}

/// <summary>
/// One invitation this machine is holding for a person to answer.
/// </summary>
/// <param name="From">Who is asking. Its <see cref="LanContact.Port"/> is where it listens, so accepting can dial it.</param>
/// <param name="TargetDeviceId">
/// Who the invitation names. Equal to this machine's own id for an invitation addressed here; another device's id
/// for one a member relayed, which is the shape a phone's invitation always takes.
/// </param>
/// <param name="GrantToken">
/// The credential the inviter minted for this machine <b>before</b> anybody agreed. It is held in memory only:
/// nothing is written to disk until a person says yes, so a decline, a withdrawal, or an expiry leaves no
/// credential behind on either side.
/// </param>
public sealed record LanPendingInvite(
    string InviteId,
    LanContact From,
    string TargetDeviceId,
    string GrantToken,
    DateTimeOffset ReceivedUtc,
    DateTimeOffset ExpiresUtc)
{
    /// <summary>Whether this invitation was addressed to <paramref name="deviceId"/> itself rather than relayed to it.</summary>
    public bool IsFor(string? deviceId) =>
        string.Equals(TargetDeviceId, deviceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether it is still worth showing to a person.</summary>
    public bool IsOpenAt(DateTimeOffset now) => now < ExpiresUtc;
}

/// <summary>What opening an invitation decided, and which invitation that was.</summary>
/// <param name="Decision">
/// <see cref="LanInviteDecision.Pending"/> for a question that was just raised, and the recorded outcome when the
/// same id arrives a second time — which is what makes a retried request idempotent rather than a second question
/// on somebody's screen.
/// </param>
public sealed record LanInviteOpenResult(LanRefusal Refusal, LanInviteDecision Decision, LanPendingInvite? Invite)
{
    public bool Opened => Refusal == LanRefusal.None;

    public static LanInviteOpenResult Refused(LanRefusal refusal) => new(refusal, LanInviteDecision.Pending, null);
}

/// <summary>What answering an invitation decided.</summary>
/// <param name="Fresh">
/// Whether <i>this</i> call is the one that decided it. A second answer to the same invitation reports the
/// outcome that already stood, and must not write a second credential for it.
/// </param>
public sealed record LanInviteAnswerResult(LanRefusal Refusal, LanInviteDecision Decision, bool Fresh, LanPendingInvite? Invite);

/// <summary>
/// The invitations this machine is holding: who asked, what they minted for this machine, and whether anybody has
/// said yes yet.
/// </summary>
/// <remarks>
/// <para>
/// It exists because an invitation is the one thing in this protocol that is answered by a person rather than by
/// a credential, and a person takes time. The request that carries it therefore has to wait, which means somebody
/// has to own the question between the request arriving and the click — and that is this class, and nothing else.
/// </para>
/// <para>
/// Every rule that bounds it is here rather than in the route, so a harness can prove them without a socket: an
/// invitation lives <see cref="LanProtocol.InviteLifetimeSeconds"/> seconds, one source may open
/// <see cref="LanProtocol.MaximumInvitesPerSource"/> of them per minute, and no more than
/// <see cref="LanProtocol.MaximumPendingInvites"/> may be unanswered at once. The last two are what stop an
/// unauthenticated stranger from filling somebody's screen — the global cap is the real bound, because the source
/// of a direct invitation is a claim and not a proof.
/// </para>
/// <para>
/// It writes nothing. The grant token an invitation carries stays in memory until a person says yes, which is
/// the whole reason a decline costs the inviter nothing but a released token.
/// </para>
/// </remarks>
public sealed class LanInviteBoard(
    TimeSpan? lifetime = null,
    int maximumPending = LanProtocol.MaximumPendingInvites,
    int maximumPerSource = LanProtocol.MaximumInvitesPerSource,
    TimeSpan? sourceWindow = null)
{
    /// <summary>How long an invitation stays answerable. Ninety seconds — see <see cref="LanProtocol.InviteLifetimeSeconds"/>.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(LanProtocol.InviteLifetimeSeconds);

    /// <summary>The window one source's invitations are counted in.</summary>
    public static readonly TimeSpan DefaultSourceWindow = TimeSpan.FromSeconds(LanProtocol.InviteWindowSeconds);

    /// <summary>
    /// How long a caller keeps waiting for a yes that is already being carried out. It covers the two credential
    /// writes and the peer row, which are the only work an answer ever does.
    /// </summary>
    private static readonly TimeSpan CommitGrace = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTimeOffset>> _bySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _lifetime = lifetime ?? DefaultLifetime;
    private readonly TimeSpan _sourceWindow = sourceWindow ?? DefaultSourceWindow;
    private readonly int _maximumPending = Math.Max(1, maximumPending);
    private readonly int _maximumPerSource = Math.Max(1, maximumPerSource);

    /// <summary>
    /// A question just arrived and somebody should be asked it. Raised off the lock, on whichever thread opened
    /// the invitation — which is a request thread, so a window that paints from this must cross onto its own.
    /// </summary>
    public event Action<LanPendingInvite>? Opened;

    /// <summary>An invitation stopped being answerable without anybody answering it: it aged out, or the asker left.</summary>
    public event Action<LanPendingInvite>? Lapsed;

    /// <summary>How many invitations are unanswered right now, which is what the global cap is measured against.</summary>
    public int PendingCount
    {
        get { lock (_gate) return _entries.Values.Count(entry => entry.Decision == LanInviteDecision.Pending); }
    }

    /// <summary>
    /// Takes an invitation and leaves it waiting for a person.
    /// </summary>
    /// <remarks>
    /// The same id twice is one question: a caller that retried because its own timeout fired must not put two
    /// rows on somebody's screen, and the second answer is the first question's state. The id arriving from a
    /// <i>different</i> device is a collision rather than a retry, and is refused instead of being merged — two
    /// machines claiming one slot is the one case where guessing which one meant it would hand a stranger the
    /// answer to somebody else's invitation.
    /// </remarks>
    public LanInviteOpenResult Open(LanInviteRequest? request, string address, int port, DateTimeOffset now)
    {
        if (request is null) return LanInviteOpenResult.Refused(LanRefusal.BadRequest);
        if (!LanProtocol.IsInviteId(request.InviteId)) return LanInviteOpenResult.Refused(LanRefusal.BadRequest);
        if (!LanProtocol.IsPeerDeviceId(request.DeviceId)) return LanInviteOpenResult.Refused(LanRefusal.BadRequest);
        // A grant is not optional: the invited machine has to be handed the token it will dial back with, and a
        // request that carries none would leave it holding a row it cannot use — the half-duplex pairing again.
        if (!LanProtocol.IsToken(request.GrantToken)) return LanInviteOpenResult.Refused(LanRefusal.BadRequest);
        if (string.IsNullOrWhiteSpace(request.TargetDeviceId)) return LanInviteOpenResult.Refused(LanRefusal.BadRequest);

        var invite = new LanPendingInvite(
            request.InviteId,
            new LanContact(request.DeviceId, LanProtocol.SanitizePeerName(request.DeviceName),
                KindOf(request.Kind), address, PortOf(request, port)),
            request.TargetDeviceId,
            request.GrantToken,
            now,
            now + _lifetime);

        var raise = false;
        LanInviteOpenResult result;
        lock (_gate)
        {
            if (_entries.TryGetValue(invite.InviteId, out var existing))
            {
                // A retry from the same machine is the same question. From another machine it is a collision.
                return string.Equals(existing.Invite.From.DeviceId, invite.From.DeviceId, StringComparison.OrdinalIgnoreCase)
                    ? new LanInviteOpenResult(LanRefusal.None, existing.Decision, existing.Invite)
                    : LanInviteOpenResult.Refused(LanRefusal.InviteConflict);
            }

            if (!Admit(invite.From.DeviceId, now)) return LanInviteOpenResult.Refused(LanRefusal.RateLimited);

            // The cap is measured on live questions only: an invitation that has been answered is not holding a
            // slot, and counting closed rows would make a busy room refuse a second invitation forever.
            if (_entries.Values.Count(entry => entry.Decision == LanInviteDecision.Pending) >= _maximumPending)
                return LanInviteOpenResult.Refused(LanRefusal.InviteConflict);

            _entries[invite.InviteId] = new Entry(invite);
            raise = true;
            result = new LanInviteOpenResult(LanRefusal.None, LanInviteDecision.Pending, invite);
        }

        if (raise) Opened?.Invoke(invite);
        return result;
    }

    /// <summary>Every invitation still waiting, oldest first, without counting the ones that have aged out.</summary>
    public IReadOnlyList<LanPendingInvite> Pending(DateTimeOffset now)
    {
        lock (_gate)
            return [.. _entries.Values
                .Where(entry => entry.Decision == LanInviteDecision.Pending && entry.Invite.IsOpenAt(now))
                .Select(entry => entry.Invite)
                .OrderBy(invite => invite.ReceivedUtc)];
    }

    /// <summary>
    /// The invitations addressed to one device.
    /// </summary>
    /// <remarks>
    /// Filtered by target rather than by "everything this machine holds", because a relayed invitation is one
    /// member's question to somebody else's phone: the member holding it must not answer on that phone's behalf,
    /// and the phone must not learn about invitations that were never for it.
    /// </remarks>
    public IReadOnlyList<LanPendingInvite> For(string targetDeviceId, DateTimeOffset now) =>
        [.. Pending(now).Where(invite => invite.IsFor(targetDeviceId))];

    /// <summary>One invitation by id, whether or not it is still open — a window redrawing a row asks this way.</summary>
    public LanPendingInvite? Find(string? inviteId)
    {
        if (!LanProtocol.IsInviteId(inviteId)) return null;
        lock (_gate) return _entries.TryGetValue(inviteId!, out var entry) ? entry.Invite : null;
    }

    /// <summary>
    /// Says yes or no to an invitation.
    /// </summary>
    /// <param name="commit">
    /// What a yes has to do before it counts — for this application, mint a credential and write the member row.
    /// It runs between the decision being reserved and the decision being announced, which is the only ordering
    /// that is honest in both directions: the caller waiting on the answer is not told "accepted" before the
    /// credentials exist, and a second click cannot start the same work twice. A commit that throws turns the
    /// answer into <see cref="LanInviteDecision.Declined"/>, because a yes nobody could carry out is a no.
    /// </param>
    /// <remarks>
    /// Answering twice is not an error and not a second decision: the recorded outcome stands,
    /// <see cref="LanInviteAnswerResult.Fresh"/> says that this call is not the one that decided it, and a caller
    /// that writes credentials keys on that flag so a double click cannot mint two tokens for one membership.
    /// </remarks>
    public async Task<LanInviteAnswerResult> AnswerAsync(string? inviteId, bool accept, DateTimeOffset now,
        Func<LanPendingInvite, Task>? commit = null, CancellationToken ct = default)
    {
        if (!LanProtocol.IsInviteId(inviteId)) return new(LanRefusal.BadRequest, LanInviteDecision.Pending, false, null);

        Entry entry;
        var fresh = false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(inviteId!, out entry!))
                return new(LanRefusal.NotFound, LanInviteDecision.Pending, false, null);

            if (entry.Decision == LanInviteDecision.Pending)
            {
                // Aged out between the row being drawn and the click landing. Closed as expired rather than
                // answered, because the inviter stopped waiting a moment ago and a yes now is a credential nobody
                // is left to collect.
                if (!entry.Invite.IsOpenAt(now))
                {
                    Close(entry, LanInviteDecision.Expired, now);
                }
                else
                {
                    // Reserved, not settled: <see cref="Entry.Decided"/> stays open until the work is done.
                    entry.Decision = accept ? LanInviteDecision.Accepted : LanInviteDecision.Declined;
                    entry.ClosedUtc = now;
                    fresh = true;
                }
            }
        }

        if (!fresh)
        {
            // Either it was already answered, or it just aged out. Waiting for the settlement is what makes this
            // correct for the racing second click: it leaves only once the first click's work is really finished.
            await entry.Decided.Task.WaitAsync(ct).ConfigureAwait(false);
            var refusal = entry.Decision == LanInviteDecision.Expired ? LanRefusal.InviteExpired : LanRefusal.None;
            return new(refusal, entry.Decision, false, entry.Invite);
        }

        var failed = false;
        try
        {
            if (accept && commit is not null) await commit(entry.Invite).ConfigureAwait(false);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            if (failed)
            {
                lock (_gate) entry.Decision = LanInviteDecision.Declined;
                entry.Decided.TrySetResult(LanInviteDecision.Declined);
            }
            else
            {
                entry.Decided.TrySetResult(entry.Decision);
            }
        }

        return new(LanRefusal.None, entry.Decision, true, entry.Invite);
    }

    /// <summary>
    /// Takes back an invitation the caller itself opened, because it stopped waiting.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> for an invitation this caller did not open, which is the whole authorization rule
    /// for this verb: a member may withdraw its own question and nobody else's.
    /// </returns>
    public bool Withdraw(string? inviteId, string fromDeviceId, DateTimeOffset now)
    {
        if (!LanProtocol.IsInviteId(inviteId)) return false;
        LanPendingInvite? lapsed = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(inviteId!, out var entry)) return false;
            if (!string.Equals(entry.Invite.From.DeviceId, fromDeviceId, StringComparison.OrdinalIgnoreCase)) return false;
            if (entry.Decision != LanInviteDecision.Pending) return false;
            Close(entry, LanInviteDecision.Withdrawn, now);
            lapsed = entry.Invite;
        }

        if (lapsed is not null) Lapsed?.Invoke(lapsed);
        return true;
    }

    /// <summary>
    /// Waits for a person to answer, and reports what happened.
    /// </summary>
    /// <remarks>
    /// The deadline is the invitation's own, so the wait and the board cannot disagree about how long a question
    /// lives. A request that goes away first takes its question with it: nobody is going to collect the answer,
    /// and a row left on somebody's screen about a caller who has gone is a question with no right answer.
    /// </remarks>
    public async Task<LanInviteDecision> AwaitAsync(string? inviteId, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!LanProtocol.IsInviteId(inviteId)) return LanInviteDecision.Expired;

        Task<LanInviteDecision> waiting;
        TimeSpan remaining;
        lock (_gate)
        {
            if (!_entries.TryGetValue(inviteId!, out var entry)) return LanInviteDecision.Expired;
            // Always the settlement rather than the decision, because a reserved answer still has work to finish:
            // returning "accepted" before the credential exists would have the caller store a token it never got.
            waiting = entry.Decided.Task;
            remaining = entry.Invite.ExpiresUtc - now
                + (entry.Decision == LanInviteDecision.Pending ? TimeSpan.Zero : CommitGrace);
        }

        if (remaining <= TimeSpan.Zero) return Expire(inviteId!);
        try
        {
            return await waiting.WaitAsync(remaining, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Expire(inviteId!);
        }
        catch (OperationCanceledException)
        {
            // The caller hung up. Its invitation goes with it, so the person who was about to be asked is not.
            Expire(inviteId!);
            throw;
        }
    }

    /// <summary>
    /// Closes every invitation whose deadline has passed, and forgets the ones closed long enough ago.
    /// </summary>
    /// <remarks>
    /// Driven by a caller's clock rather than a timer, the same way <see cref="LanRoomPresence"/> is: a window
    /// already ticks once a second, and a second timer inside a library is one more thing to start and stop. The
    /// sweep is what turns an expiry into an event, because a caller that is waiting has its own deadline and
    /// would otherwise be the only one who ever learns.
    /// </remarks>
    public IReadOnlyList<LanPendingInvite> Sweep(DateTimeOffset now)
    {
        List<LanPendingInvite> lapsed = [];
        lock (_gate)
        {
            foreach (var entry in _entries.Values.Where(entry => entry.Decision == LanInviteDecision.Pending && !entry.Invite.IsOpenAt(now)).ToList())
            {
                Close(entry, LanInviteDecision.Expired, now);
                lapsed.Add(entry.Invite);
            }

            // A closed row is kept for one more lifetime so a late answer or a late withdrawal is answered with
            // what really happened instead of "never heard of it". After that it is only a memory.
            foreach (var stale in _entries.Values
                .Where(entry => entry.Decision != LanInviteDecision.Pending && now - entry.ClosedUtc > _lifetime)
                .Select(entry => entry.Invite.InviteId).ToList())
                _entries.Remove(stale);
        }

        foreach (var invite in lapsed) Lapsed?.Invoke(invite);
        return Pending(now);
    }

    /// <summary>
    /// Closes every unanswered invitation from one device.
    /// </summary>
    /// <remarks>
    /// For 取消信任: the questions that device has on this machine are answered by a person who has just decided it
    /// may not come back, and leaving them on screen would offer a yes that immediately undoes the no.
    /// </remarks>
    public IReadOnlyList<LanPendingInvite> Forget(string fromDeviceId, DateTimeOffset now)
    {
        List<LanPendingInvite> dropped = [];
        lock (_gate)
        {
            foreach (var entry in _entries.Values
                .Where(entry => entry.Decision == LanInviteDecision.Pending
                    && string.Equals(entry.Invite.From.DeviceId, fromDeviceId, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Close(entry, LanInviteDecision.Withdrawn, now);
                dropped.Add(entry.Invite);
            }
        }

        foreach (var invite in dropped) Lapsed?.Invoke(invite);
        return dropped;
    }

    /// <summary>
    /// Drops every invitation without narrating anything.
    /// </summary>
    /// <remarks>
    /// For the window closing: every question in here was going to be answered by that window, and the callers
    /// waiting on them are owed the one answer that writes nothing. A lapsed event would be a lie about what the
    /// user did, so this closes as withdrawn and says nothing.
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in _entries.Values.Where(entry => entry.Decision == LanInviteDecision.Pending).ToList())
                Close(entry, LanInviteDecision.Withdrawn, now);
            _entries.Clear();
            _bySource.Clear();
        }
    }

    /// <summary>
    /// Whether one source may open another invitation right now, recording this one when it may.
    /// </summary>
    /// <remarks>
    /// A source is a device id, which for a direct invitation is a claim rather than a proof — so this is a speed
    /// bump and not a wall. The global cap is the wall.
    /// </remarks>
    private bool Admit(string sourceDeviceId, DateTimeOffset now)
    {
        if (!_bySource.TryGetValue(sourceDeviceId, out var times))
        {
            times = [];
            _bySource[sourceDeviceId] = times;
        }

        times.RemoveAll(seen => now - seen > _sourceWindow);
        if (times.Count >= _maximumPerSource) return false;
        times.Add(now);

        // A source that has been quiet for longer than the window is not worth remembering at all, and a map that
        // only ever grows is a slow leak on a network with a lot of machines on it.
        foreach (var idle in _bySource.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToList())
            _bySource.Remove(idle);
        return true;
    }

    private LanInviteDecision Expire(string inviteId)
    {
        LanPendingInvite? lapsed = null;
        lock (_gate)
        {
            if (_entries.TryGetValue(inviteId, out var entry) && entry.Decision == LanInviteDecision.Pending)
            {
                Close(entry, LanInviteDecision.Expired, DateTimeOffset.UtcNow);
                lapsed = entry.Invite;
            }
            else if (entry is not null)
            {
                return entry.Decision;
            }
        }

        if (lapsed is not null) Lapsed?.Invoke(lapsed);
        return LanInviteDecision.Expired;
    }

    /// <summary>Records the outcome and releases whoever is waiting on it. Called under the lock, and never awaits.</summary>
    private void Close(Entry entry, LanInviteDecision decision, DateTimeOffset now)
    {
        entry.Decision = decision;
        entry.ClosedUtc = now;
        // Asynchronous continuations, so the request thread that answered does not end up running the whole
        // accepting path — which writes two credential files — inside this lock.
        entry.Decided.TrySetResult(decision);
    }

    /// <summary>An unrecognised kind is kept as "unknown" rather than believed, exactly as a pairing request is judged.</summary>
    private static string KindOf(string? claimed) => claimed switch
    {
        LanProtocol.KindWindows => LanProtocol.KindWindows,
        LanProtocol.KindPhone => LanProtocol.KindPhone,
        _ => LanProtocol.KindUnknown,
    };

    /// <summary>
    /// Where the inviting machine listens: what its request declared, or — when it declared nothing — what its own
    /// body claimed.
    /// </summary>
    /// <remarks>
    /// The connection wins because it is the only one of the two this side observed rather than was told. The body's
    /// claim is still worth falling back on, because a caller that cannot set a header is a caller that would
    /// otherwise be recorded at port 0 — a member nothing can dial, which is the one-way room again. A body that
    /// claims a port the connection did not corroborate is no worse than the address it came from, which is equally
    /// unproven: the person answering the invitation is the one deciding whether to trust either.
    /// </remarks>
    private static int PortOf(LanInviteRequest request, int observed) =>
        observed is >= LanOptions.MinimumPort and <= LanOptions.MaximumPort ? observed : request.Port;

    private sealed class Entry(LanPendingInvite invite)
    {
        public LanPendingInvite Invite { get; } = invite;

        public LanInviteDecision Decision { get; set; } = LanInviteDecision.Pending;

        public DateTimeOffset ClosedUtc { get; set; }

        public TaskCompletionSource<LanInviteDecision> Decided { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
