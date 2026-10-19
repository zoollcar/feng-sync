using FengSync.Core;

namespace FengSync.Lan;

/// <summary>A peer, as this session knows it: who it is, what it said it was called, and where it just spoke from.</summary>
public sealed record LanContact(string DeviceId, string DeviceName, string Kind, string Address, int Port);

/// <summary>One write a peer is asking this machine to perform, and therefore one thing a person may say no to.</summary>
public sealed record LanInboundOffer(LanContact Contact, string Path, long DeclaredBytes, bool IsDirectory, string? Session);

/// <summary>The answer to "may this request proceed as itself".</summary>
/// <param name="Refusal">
/// <see cref="LanRefusal.Unpaired"/> for an unknown device and
/// <see cref="LanRefusal.DeviceIdentityChanged"/> for a known one presenting somebody else's credential — the
/// difference is the difference between "please pair" and "your device was re-paired from elsewhere".
/// </param>
public sealed record LanAuthResult(LanRefusal Refusal, LanContact? Contact)
{
    public bool Allowed => Contact is not null;

    public static LanAuthResult Rejected(LanRefusal refusal) => new(refusal, null);
}

/// <summary>What pairing decided. A refusal still carries the contact, because the window reports who tried.</summary>
public sealed record LanPairResult(LanRefusal Refusal, LanContact Contact, string? Token, LanPeerRecord? Peer)
{
    public bool Accepted => Refusal == LanRefusal.None && Token is not null;
}

/// <summary>One local file or folder, described for serving: the name, the length, the directory flag.</summary>
/// <param name="Bytes">
/// The file's own length. A directory reports 0 and is labelled as a folder, because counting a subtree is a
/// walk nobody asked for at the moment it is served — the same reason the cloud 属性 panel leaves a folder's
/// size 未统计 until the user presses the button that measures it.
/// </param>
public sealed record LanSendItem(string Name, string AbsolutePath, bool IsDirectory, long Bytes, DateTimeOffset? ModifiedUtc)
{
    /// <summary>Describes a path that exists right now. The caller has already decided it may be shared.</summary>
    public static LanSendItem Of(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        if (Directory.Exists(full))
            return new(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), full, true, 0,
                Directory.GetLastWriteTimeUtc(full));
        var file = new FileInfo(full);
        if (!file.Exists) throw new FileNotFoundException(null, full);
        return new(file.Name, full, false, file.Length, file.LastWriteTimeUtc);
    }
}

/// <summary>
/// The decisions a listener makes about the peers talking to it: who is authenticated, what a code buys, and
/// whether a stranger's bytes may land.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from the HTTP shell so every one of those judgements is testable without a socket, and so the
/// window can read the same state the server is enforcing rather than a copy of it.
/// </para>
/// <para>
/// The confirmation rule is the one worth reading twice. A device that was already in
/// <c>lan-peers.json</c> when this window opened is trusted and its bytes land unasked; a device that pairs
/// while the window is open is a stranger until a person has agreed to its first write, and that agreement
/// lasts for the rest of the session. With <see cref="AutoAcceptFromTrusted"/> off, even a
/// returning device is asked once per session — the switch means "ask me again", not "ask me per file".
/// </para>
/// </remarks>
public sealed class LanSession(
    LanOptions options,
    LanPeerStore peers,
    LanPeerSecretStore secrets,
    LanPairingState pairing,
    Func<LanInboundOffer, CancellationToken, Task<bool>>? confirmInbound = null,
    LanGroupLog? group = null,
    LanRoomPresence? presence = null)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _agreedThisSession = new(StringComparer.OrdinalIgnoreCase);

    // Snapshot of who was trusted before this window opened, taken by the listener through
    // PrimeTrustSnapshotAsync before it answers a single request. It is captured once and never refreshed: a
    // device that pairs mid-session is new by definition, and re-reading the store would mark it as already
    // known from the row its own pairing just wrote.
    private volatile HashSet<string>? _trustedBeforeThisSession;

    public LanOptions Options { get; } = options;
    public LanPeerStore Peers { get; } = peers;

    /// <summary>
    /// What this machine knows the room said. On the session rather than on the window or the listener because
    /// both of them write it and neither may own it: a peer's poll merges into it, the user's own sentence lands
    /// in it, and the window draws whatever is in it. It is memory, so closing the window is leaving the room.
    /// </summary>
    public LanGroupLog Group { get; } = group ?? new LanGroupLog();

    /// <summary>
    /// Who is in the room right now.
    /// </summary>
    /// <remarks>
    /// Fed from here because this is the one place every authenticated request passes through, which makes it the
    /// only honest source for "that device is still here": a phone polling the room every three seconds renews its
    /// own lease as a side effect of asking, and one whose browser was closed stops renewing it and ages out
    /// without anybody having to notice an absence.
    /// </remarks>
    public LanRoomPresence Presence { get; } = presence ?? new LanRoomPresence();

    /// <summary>
    /// Whether a device that was already trusted before this window opened may write without being asked.
    /// </summary>
    /// <remarks>
    /// Read per write rather than frozen into <see cref="Options"/>, because the window's 收到文件时自动同意
    /// switch is a choice the user makes while a transfer may already be running, and a listener that only
    /// takes it on restart would keep accepting after the person has turned it off.
    /// </remarks>
    public bool AutoAcceptFromTrusted { get; set; } = options.AutoAcceptFromTrusted;

    /// <summary>The answer to "is there a Feng Sync listening here", and the only body an unpaired stranger can read.</summary>
    public LanHelloReply Hello(LanDeviceIdentity self) =>
        new(LanApi.WireVersion, self.DeviceId, self.DeviceName, self.Kind, pairing.IsAccepting);

    /// <param name="credential">
    /// The peer's <c>deviceId:token</c>, from the <c>Authorization</c> header or from the cookie a pairing reply
    /// set — see <see cref="TrySplitCredential"/> for what either form may carry.
    /// </param>
    public async Task<LanAuthResult> AuthenticateAsync(string? credential, string? claimedName, string? claimedKind, string address, int port, CancellationToken ct = default)
    {
        if (!TrySplitCredential(credential, out var deviceId, out var token)) return LanAuthResult.Rejected(LanRefusal.Unpaired);

        var records = await Peers.LoadAsync(ct).ConfigureAwait(false);
        var record = records.FirstOrDefault(candidate => string.Equals(candidate.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        if (!await secrets.VerifyAsync(deviceId, token, ct).ConfigureAwait(false))
        {
            // A device id this list already holds, presenting a token that is not the one issued to it. That is
            // not a stranger asking to pair — it is a device re-paired from somewhere else, and the window has
            // to be able to name the difference.
            return LanAuthResult.Rejected(record is null ? LanRefusal.Unpaired : LanRefusal.DeviceIdentityChanged);
        }

        // The name and kind come from the record when there is one, because that is what a person read and
        // agreed to. A credential with no record behind it falls back to what the peer claims about itself,
        // which it can only ever do for itself.
        var contact = new LanContact(deviceId,
            string.IsNullOrWhiteSpace(record?.DeviceName) ? LanProtocol.SanitizePeerName(claimedName) : record!.DeviceName,
            record?.Kind ?? KindOf(claimedKind),
            address, port);

        // Only a device already in the list has its sighting recorded; an unknown id was refused above, so this
        // cannot grow the trusted list out of a header.
        await Peers.RememberContactAsync(deviceId, address, port, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        // …and the same sighting is what keeps it in the room. The address and port are this request's, so a
        // device that moved between access points is in the list at the place it can actually be reached.
        Presence.Seen(new LanRoomMember(contact.DeviceId, contact.DeviceName, contact.Kind, address, port,
            DateTimeOffset.UtcNow, Discovered: false));
        return new LanAuthResult(LanRefusal.None, contact);
    }

    /// <summary>
    /// Trades a one-time code for this peer's long-lived token.
    /// </summary>
    /// <remarks>
    /// All three pairing paths — a scanned QR, a typed code, and a discovered computer — arrive here as the same
    /// request, so there is one place where a code is spent and one place where a device becomes trusted.
    /// </remarks>
    public async Task<LanPairResult> PairAsync(LanPairRequest? request, string address, int port, CancellationToken ct = default)
    {
        var deviceId = request?.DeviceId?.Trim() ?? "";
        if (!LanProtocol.IsPeerDeviceId(deviceId))
            return new LanPairResult(LanRefusal.BadRequest, new LanContact(deviceId, "", "unknown", address, port), null, null);

        var contact = new LanContact(deviceId, LanProtocol.SanitizePeerName(request!.DeviceName), KindOf(request.Kind), address, port);
        var verdict = pairing.TryConsume(request.Code, address);
        if (verdict != LanPairVerdict.Accepted)
            return new LanPairResult(verdict switch
            {
                LanPairVerdict.BadCode => LanRefusal.BadCode,
                LanPairVerdict.CodeExpired => LanRefusal.CodeExpired,
                _ => LanRefusal.RateLimited,
            }, contact, null, null);

        var token = await secrets.IssueAsync(deviceId, ct).ConfigureAwait(false);
        var peer = await Peers.RememberAsync(new LanPeerRecord(deviceId, contact.DeviceName, contact.Kind,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, port, [address]), ct).ConfigureAwait(false);

        // A device that just spent a code is in the room from this moment, and its own first poll will renew
        // that. Marking it here rather than waiting for the poll is what makes the join line and the device row
        // appear together instead of a beat apart.
        Presence.Seen(new LanRoomMember(contact.DeviceId, contact.DeviceName, contact.Kind, address, port,
            DateTimeOffset.UtcNow, Discovered: false));

        // A device that pairs mid-session has not been agreed to yet, even though it now has a token: pairing
        // proves it read the code, not that a person wants its files.
        return new LanPairResult(LanRefusal.None, contact, token, peer);
    }

    /// <summary>
    /// Whether one write from one peer may land, asking a person first when it must.
    /// </summary>
    /// <returns>
    /// <see cref="LanRefusal.None"/> to proceed, <see cref="LanRefusal.Declined"/> to refuse with nothing
    /// written. Only two outcomes, because every other judgement on this path belongs to the sink.
    /// </returns>
    public async Task<LanRefusal> AdmitWriteAsync(LanInboundOffer offer, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_agreedThisSession.Contains(offer.Contact.DeviceId)) return LanRefusal.None;
        }

        if (AutoAcceptFromTrusted && (await TrustedBeforeThisSessionAsync(ct).ConfigureAwait(false)).Contains(offer.Contact.DeviceId))
        {
            // No prompt and no permanent note: a returning device is accepted for this file, and the next
            // window opening will make the same decision from the same list.
            return LanRefusal.None;
        }

        // Nobody to ask is not the same as somebody who said yes. A write that could not be confirmed is
        // refused, because bytes landing in the user's Downloads folder is the one effect here that cannot be
        // undone by closing a window.
        if (confirmInbound is null) return LanRefusal.Declined;

        if (!await confirmInbound(offer, ct).ConfigureAwait(false)) return LanRefusal.Declined;

        lock (_gate) _agreedThisSession.Add(offer.Contact.DeviceId);
        return LanRefusal.None;
    }

    /// <summary>Un-trusts a device and deletes the credential behind it. The window's 取消信任.</summary>
    public async Task<bool> ForgetAsync(string deviceId, CancellationToken ct = default)
    {
        var forgotten = await Peers.ForgetAsync(deviceId, ct).ConfigureAwait(false);
        await secrets.ForgetAsync(deviceId, ct).ConfigureAwait(false);
        lock (_gate) _agreedThisSession.Remove(deviceId);
        // Trust and presence are two different questions and this answers both: a device that is no longer
        // trusted has no business being listed as a member of the room either.
        Presence.Forget(deviceId);
        return forgotten;
    }

    /// <summary>
    /// Takes a device out of the room because it said it was leaving.
    /// </summary>
    /// <remarks>
    /// The credential is deliberately left alone. A phone that closes its tab has not revoked anything — it is
    /// simply not here — and forgetting it would make a person re-scan a code every time they put their phone
    /// down. Only <see cref="ForgetAsync"/> means "this device may not come back".
    /// </remarks>
    public bool LeaveRoom(LanContact contact) => Presence.Forget(contact.DeviceId);

    /// <summary>
    /// Lets a peer remove its own credential. It cannot remove anybody else's — the route carries no id to
    /// compare, which is what keeps this off the list of endpoints that need an authorization model.
    /// </summary>
    public Task<bool> ForgetOwnCredentialAsync(LanContact contact, CancellationToken ct = default) => ForgetAsync(contact.DeviceId, ct);

    /// <summary>Drops the armed code so a stopped service cannot be paired into.</summary>
    public void InvalidatePairingCode() => pairing.Invalidate();

    /// <summary>
    /// Records who was already trusted, before a single request is answered.
    /// </summary>
    /// <remarks>
    /// The listener calls this as it starts, and the timing is the whole rule: a device that pairs during this
    /// session has to be asked before its first write, and if the snapshot were taken on that first write
    /// instead, pairing would have written the row already and the stranger would be trusted by its own
    /// paperwork. Called once per window, it is idempotent.
    /// </remarks>
    public Task PrimeTrustSnapshotAsync(CancellationToken ct = default) => TrustedBeforeThisSessionAsync(ct);

    private async Task<HashSet<string>> TrustedBeforeThisSessionAsync(CancellationToken ct)
    {
        if (_trustedBeforeThisSession is { } snapshot) return snapshot;
        var records = await Peers.LoadAsync(ct).ConfigureAwait(false);
        snapshot = new HashSet<string>(records.Select(record => record.DeviceId), StringComparer.OrdinalIgnoreCase);
        // Two requests racing here compute the same set from the same file, so the loser is discarded and both
        // answer the question the user asked.
        return Interlocked.CompareExchange(ref _trustedBeforeThisSession, snapshot, null) ?? snapshot;
    }

    /// <summary>
    /// Reads the credential a request carries: <c>deviceId:token</c>, whether it arrived in the
    /// <c>Authorization</c> header or in the cookie a pairing reply left behind.
    /// </summary>
    /// <remarks>
    /// A token is hex and a device id is hex, so the first colon is the only separator either side could mean.
    /// The scheme prefix is dropped here rather than by the caller because the two transports differ only in it.
    /// </remarks>
    public static bool TrySplitCredential(string? credential, out string deviceId, out string token)
    {
        deviceId = "";
        token = "";
        if (string.IsNullOrWhiteSpace(credential)) return false;
        var value = credential.Trim();
        var separator = value.IndexOf(' ');
        if (separator > 0 && string.Equals(value[..separator], LanProtocol.AuthScheme, StringComparison.Ordinal))
            value = value[(separator + 1)..];
        var split = value.IndexOf(':');
        if (split <= 0 || split == value.Length - 1) return false;
        deviceId = value[..split];
        token = value[(split + 1)..];
        return LanProtocol.IsPeerDeviceId(deviceId) && token.Length >= LanPeerSecretStore.TokenBytes;
    }

    /// <summary>An unrecognised kind is kept as "unknown" rather than believed, so a row cannot describe itself as anything.</summary>
    private static string KindOf(string? claimed) => claimed switch
    {
        LanProtocol.KindWindows => LanProtocol.KindWindows,
        LanProtocol.KindPhone => LanProtocol.KindPhone,
        _ => LanProtocol.KindUnknown,
    };
}
