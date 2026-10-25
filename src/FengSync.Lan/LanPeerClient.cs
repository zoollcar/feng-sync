using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using FengSync.Core;

namespace FengSync.Lan;

/// <summary>
/// A peer to talk to: who it is, and where it answered from.
/// </summary>
/// <param name="DeviceId">
/// The other side's own id, read from <c>/hello</c> or from its discovery announcement. It is the key this
/// machine files that peer's issued token under — not our own id, which is the same value for every peer and
/// would collapse two credentials into one slot.
/// </param>
/// <param name="DeviceName">
/// What that machine said it was called, from its own <c>/hello</c> or its announcement. It travels here because
/// pairing is the moment this machine decides it will call that peer again, and the row that decision writes is
/// the one place a later member list reads a name from.
/// </param>
public sealed record LanPeerEndpoint(string DeviceId, string Address, int Port, string? DeviceName = null);

/// <summary>What handing one item over cost.</summary>
public sealed record LanSendOutcome(int Files, int Directories, long Bytes);

/// <summary>
/// The calling half of a computer-to-computer transfer: this machine as one peer among the devices another
/// machine's window has agreed to.
/// </summary>
/// <remarks>
/// <para>
/// A second computer needs nothing the phone page does not already do — <c>/hello</c>, <c>/pair</c>,
/// <c>/inbox</c>, <c>/mkdir</c>, and the room's two calls — so this class is those with typed bodies, and nothing
/// else. It deliberately grows no new protocol: a pull/ack handshake for computers would have made the receiving
/// side two code paths instead of one, and a folder is the same sequence of per-file writes either way.
/// </para>
/// <para>
/// Credentials it receives live in their own DPAPI file, keyed by the <b>peer's</b> id, because a token issued
/// by another machine is a different thing from one this machine issued: revoking the peer's row here cannot
/// unmake what that peer still holds, and forgetting it on this side is what stops a stale token being sent
/// again. It never enters <c>lan-peers.json</c>, which is a list a user can open in an editor.
/// </para>
/// </remarks>
public sealed class LanPeerClient : IDisposable
{
    /// <summary>
    /// How long a stranger is allowed to take answering 这里是 Feng Sync 吗 before the window says 无应答.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long this side waits for somebody on the far machine to answer an invitation.
    /// </summary>
    /// <remarks>
    /// Deliberately longer than the invitation's own lifetime, because the two numbers are about different things:
    /// the peer stops accepting an answer after <see cref="LanProtocol.InviteLifetimeSeconds"/>, and this side has to
    /// stay on the line long enough to be told so. Timing out first would turn every unanswered invitation into a
    /// network error the user cannot act on.
    /// </remarks>
    public static readonly TimeSpan InviteTimeout = TimeSpan.FromSeconds(LanProtocol.InviteLifetimeSeconds + 10);

    /// <summary>
    /// How long one of the small calls around a transfer may take. It is a timeout rather than an open end because
    /// a poll for the room's history runs on a timer: a peer that stops answering must make that poll give up and
    /// the next one happen, not leave a task waiting forever on a machine whose window has closed.
    /// </summary>
    public static readonly TimeSpan QuestionTimeout = TimeSpan.FromSeconds(10);

    private readonly LanDeviceIdentity _self;
    private readonly LanPeerStore _members;
    private readonly LanPeerSecretStore _received;
    private readonly LanPeerSecretStore? _issued;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <param name="members">
    /// The device list this machine keeps, which is also the list the room dials. Pairing writes into it: a
    /// machine this one holds a credential for is a member it can read, and a caller that never recorded the
    /// address it paired at would have a token with nothing to spend it on.
    /// </param>
    /// <param name="received">
    /// Where the tokens other machines issued to this one are kept. Defaults to its own file rather than
    /// <c>lan-peer-secrets.dat</c>, because that one holds what this machine handed out and a reader of either
    /// file has to be able to tell which direction the credential points.
    /// </param>
    /// <param name="issued">
    /// What this machine hands out, used for the one thing the calling half must mint: the grant that travels in a
    /// pairing or invite request so the peer can dial back. Optional, and shared with the listener when both live
    /// in one window — a second instance over the same file would load-modify-write over the first one's work.
    /// </param>
    /// <param name="listenPort">
    /// The port this machine is itself listening on, sent as <c>X-FS-Port</c> on every call.
    /// </param>
    /// <remarks>
    /// <paramref name="listenPort"/> is not a convenience. Without it the peer records the row for this machine
    /// with port 0 — the same number a phone browser honestly reports — and a member at port 0 cannot be dialled,
    /// so every sentence and file this machine sends reaches the other side while nothing comes back. That is the
    /// one-way room this whole invitation feature was built to end, and the header is what makes it two-way.
    /// </remarks>
    public LanPeerClient(LanDeviceIdentity self, LanPeerStore members, LanPeerSecretStore? received = null,
        HttpClient? http = null, LanPeerSecretStore? issued = null, int listenPort = 0)
    {
        _self = self;
        _members = members;
        _received = received ?? new LanPeerSecretStore(DefaultReceivedPath());
        _issued = issued;
        ListenPort = listenPort;
        _ownsHttpClient = http is null;
        _http = http ?? new HttpClient(CreateHandler())
        {
            // A whole file is one request, and a gigabyte on a slow Wi-Fi link has no reasonable number of
            // seconds to promise in advance. Liveness is the caller's CancellationToken's job; short calls
            // around it get their own linked timeout instead.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// The handler every peer call goes through: direct, never through a proxy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No proxy is not a preference. Every address this class dials is a machine on the same wire as this one — a
    /// peer found by UDP discovery, or this process's own loopback while a test drives it. A proxy has no route to
    /// a peer's private address, so at best it fails; at worst it accepts the call, answers for whatever it
    /// forwards to, and then <em>two</em> peers are served by one pooled connection — after which every call
    /// reaches the machine that answered last. That is a room where a sentence typed here lands on the wrong
    /// computer, and a file asked of one member arrives from another.
    /// </para>
    /// <para>
    /// The failure is invisible until it is not: a machine with no proxy configured behaves perfectly, and the
    /// only machines that break are the ones running a VPN or a corporate gateway — which is to say, most of them.
    /// Public because the rule is unobservable from behaviour in the ordinary case, so a test can only state it.
    /// </para>
    /// </remarks>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        // Connections are kept across calls because a room polls every member every three seconds; a pool that
        // outlived one transfer would still be ten minutes of handshakes saved on a link that is already slow.
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    };

    /// <summary>The credential file for tokens this machine was handed. Kept apart from the one it hands out.</summary>
    public static string DefaultReceivedPath() => Path.Combine(AppDataPaths.LanTransfer, "lan-received-tokens.dat");

    /// <summary>
    /// The port this machine is listening on, declared to every peer as <c>X-FS-Port</c>.
    /// </summary>
    /// <remarks>
    /// Settable rather than fixed at construction because the window rebuilds its listener when the user applies a
    /// new port, and this object outlives that: a client still announcing the old number would keep telling every
    /// peer to dial an address that stopped answering. 0 means "not listening", and nothing is declared.
    /// </remarks>
    public int ListenPort { get; set; }

    /// <summary>Whether this machine holds a credential for that peer, so a window can say 已配对 without a round trip.</summary>
    public Task<bool> IsPairedAsync(LanPeerEndpoint peer, CancellationToken ct = default) =>
        TokenForAsync(peer, ct).ContinueWith(read => read.Result is not null, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Asks whether a Feng Sync is answering, and returns null for anything that is not one.</summary>
    public async Task<LanHelloReply?> HelloAsync(string address, int port, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await _http.GetAsync(Build("/api/v1/hello", address, port), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<LanHelloReply>(body, LanApi.Json, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException)
        {
            // A host that refuses, a host that answers as something else, and a host that says nothing all
            // mean the same thing to the person at the window: that address is not a pairing partner.
            return null;
        }
    }

    /// <summary>
    /// Trades the peer's displayed code for a token and remembers it under that peer's id.
    /// </summary>
    /// <exception cref="LanTransferException">
    /// <see cref="LanRefusal.BadCode"/> for a string that is not a code at all, before anything is sent, and the
    /// peer's own refusal afterwards — expired, rate limited, or an identity that changed.
    /// </exception>
    /// <remarks>
    /// One code buys both directions. The request carries a token this machine minted for the peer, and the reply
    /// carries the one the peer minted for this machine; without the first half the peer would keep a row it
    /// cannot dial and every poll of this machine would answer 请先配对.
    /// </remarks>
    public async Task<LanPairReply> PairAsync(LanPeerEndpoint peer, string rawCode, CancellationToken ct = default)
    {
        if (!LanPairCode.TryNormalise(rawCode, out var code)) throw LanTransferException.For(LanRefusal.BadCode);
        var grant = await MintGrantAsync(peer.DeviceId, ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            var request = new HttpRequestMessage(HttpMethod.Post, Build("/api/v1/pair", peer.Address, peer.Port))
            {
                Content = JsonContent(new LanPairRequest(code, _self.DeviceId, _self.DeviceName, _self.Kind, grant.Token)),
            };
            using var response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
            var reply = await ReadAsync<LanPairReply>(response, ct).ConfigureAwait(false);
            // A machine that answers /pair is a computer — no phone listens — so the kind is settled by the call
            // succeeding rather than by anything it claimed. The name is its own claim, sanitized the way every
            // name this application stores is, and a later 改名 or /hello is what corrects it.
            await AdoptAsync(DirectRecord(peer, LanProtocol.KindWindows), reply.Token, ct).ConfigureAwait(false);
            return reply;
        }
        catch
        {
            await ReleaseGrantAsync(grant, ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Keeps the credential a peer issued and the row that lets this machine dial it.
    /// </summary>
    /// <remarks>
    /// One method for pairing and for an accepted invitation, because the two ends of both are the same two
    /// writes and a second copy would be the place they start disagreeing about which kind a peer is.
    /// </remarks>
    private async Task AdoptAsync(LanPeerRecord record, string token, CancellationToken ct)
    {
        await _received.StoreAsync(record.DeviceId, token, ct).ConfigureAwait(false);
        await _members.RememberAsync(record, ct).ConfigureAwait(false);
    }

    /// <summary>The row a peer that answers for itself gets: its own address, its own port, no gateway.</summary>
    private static LanPeerRecord DirectRecord(LanPeerEndpoint peer, string kind) =>
        new(peer.DeviceId, LanProtocol.SanitizePeerName(peer.DeviceName), kind,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, peer.Port, [peer.Address]);

    /// <summary>
    /// A token minted for a peer before it has agreed to anything, because it has to travel in the request.
    /// </summary>
    /// <param name="WasPresent">
    /// Whether this machine already held a credential for that device. A failed attempt drops only a token it
    /// minted for a device that had none: forgetting one that was already there would take away a working
    /// credential because a second attempt went wrong, which is worse than an unused string.
    /// </param>
    private sealed record LanGrant(LanPeerSecretStore? Store, string DeviceId, string? Token, bool WasPresent);

    private async Task<LanGrant> MintGrantAsync(string deviceId, CancellationToken ct)
    {
        if (_issued is null) return new LanGrant(null, deviceId, null, false);
        // An existing credential is handed over again rather than replaced. Minting a new one here would
        // invalidate the token the peer is already using before this attempt has proved it can read a code, so
        // a mistyped second pairing would break a working one.
        var existing = await _issued.TokenForAsync(deviceId, ct).ConfigureAwait(false);
        if (existing is not null) return new LanGrant(_issued, deviceId, existing, true);

        var token = await _issued.IssueAsync(deviceId, ct).ConfigureAwait(false);
        return new LanGrant(_issued, deviceId, token, false);
    }

    private static async Task ReleaseGrantAsync(LanGrant grant, CancellationToken ct)
    {
        if (grant.Store is null || grant.WasPresent) return;
        try { await grant.Store.ForgetAsync(grant.DeviceId, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Drops the credential this machine holds for a peer. The peer's own row is unaffected — that is what 取消信任 on the other side is for.
    /// </summary>
    public Task<bool> ForgetAsync(LanPeerEndpoint peer, CancellationToken ct = default) => _received.ForgetAsync(peer.DeviceId, ct);

    /// <summary>
    /// Asks a device to join the room, and waits while a person on that machine decides.
    /// </summary>
    /// <param name="targetDeviceId">
    /// The device the invitation is really for, when it is not the machine being called. A phone browser cannot be
    /// dialled, so its invitation goes to the computer serving its page and names the phone — which is the whole of
    /// the invitation network's trick. Null invites the machine being called.
    /// </param>
    /// <param name="targetName">
    /// What the relayed device is called, so the member row written for it is the name a person saw rather than a
    /// bare identifier. Ignored for a direct invitation, whose row takes the name the peer gave itself.
    /// </param>
    /// <remarks>
    /// <para>
    /// The one call in this protocol that waits on a human. Everything else is answered by a credential or by a
    /// rule; this is answered by somebody looking at a screen and clicking, which is why its timeout is measured in
    /// a minute and a half rather than in seconds.
    /// </para>
    /// <para>
    /// It is also the only call that mints a credential before it has any right to one. The grant travels in the
    /// request so the accepting machine can dial this one back, and it is released on every outcome but a yes —
    /// otherwise a declined invitation would leave a working token in this machine's file for a device that has
    /// just said no.
    /// </para>
    /// </remarks>
    /// <exception cref="LanTransferException">
    /// <see cref="LanRefusal.NotFound"/> from a build that predates this route, which the window reads as 对方版本太旧;
    /// <see cref="LanRefusal.RateLimited"/> or <see cref="LanRefusal.InviteConflict"/> when the far side is already
    /// holding as many questions as it will.
    /// </exception>
    public async Task<LanInviteReply> InviteAsync(LanPeerEndpoint peer, string? targetDeviceId = null,
        string? targetName = null, CancellationToken ct = default)
    {
        // The grant is minted for the machine that will hold the answer, which for a relayed invitation is the
        // gateway rather than the phone: the phone has no listener, so the credential is only ever spent on the
        // machine that can act on it.
        var grant = await MintGrantAsync(peer.DeviceId, ct).ConfigureAwait(false);
        var inviteId = Guid.NewGuid().ToString("N");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InviteTimeout);
            var request = new HttpRequestMessage(HttpMethod.Post, Build("/api/v1/invites", peer.Address, peer.Port))
            {
                Content = JsonContent(new LanInviteRequest(inviteId, _self.DeviceId, _self.DeviceName, _self.Kind,
                    ListenPort, grant.Token ?? "", targetDeviceId)),
            };
            // Only a relayed invitation carries a credential. A direct one is answered by a person, and sending a
            // token to a machine that is not going to check it is one more copy of a secret in flight for nothing.
            if (!string.IsNullOrEmpty(targetDeviceId)
                && await TokenForAsync(peer, ct).ConfigureAwait(false) is { } credential)
                AddCredential(request, credential);

            using var response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
            var reply = await ReadAsync<LanInviteReply>(response, ct).ConfigureAwait(false);
            if (!reply.Accepted)
            {
                // No is an outcome, not an exception — and it costs the inviter the token it pre-minted, because a
                // credential for a device that has just said no is exactly what 取消信任 would have removed.
                await ReleaseGrantAsync(grant, ct).ConfigureAwait(false);
                return reply;
            }

            // Same two writes a pairing makes, in the same order: the credential it minted for this machine, and the
            // row that lets this machine call it. A relayed invitation joins the device it named rather than the
            // machine that relayed it — the gateway is named on that row, because it is the only way the room can
            // reach a device that answers nowhere.
            var joined = string.IsNullOrEmpty(targetDeviceId)
                ? DirectRecord(peer, LanProtocol.KindWindows)
                : new LanPeerRecord(targetDeviceId!, LanProtocol.SanitizePeerName(targetName), LanProtocol.KindPhone,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null, peer.DeviceId);
            await AdoptAsync(joined, reply.Token!, ct).ConfigureAwait(false);
            return reply;
        }
        catch
        {
            await ReleaseGrantAsync(grant, ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reads a peer's copy of the room past the number this machine remembers for that peer.
    /// </summary>
    /// <remarks>
    /// The watermark is per peer on purpose: each machine numbers the arrival order of its own copy, so one number
    /// for the room would need a member that owns it, and this design has none. A caller that reads two peers with
    /// one shared watermark re-asks for what it already has from one and misses what the other said.
    /// </remarks>
    public async Task<LanLogReply> LogAsync(LanPeerEndpoint peer, long since, CancellationToken ct = default)
    {
        var token = await TokenForAsync(peer, ct).ConfigureAwait(false) ?? throw LanTransferException.For(LanRefusal.Unpaired);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QuestionTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            // The query goes on after the address rather than inside the path: UriBuilder writes its path with the
            // question mark escaped, and a route that arrives as <c>/api/v1/log%3Fsince=0</c> is not the route
            // this listener mapped.
            Build("/api/v1/log", peer.Address, peer.Port) + $"?since={since}");
        AddCredential(request, token);
        using var response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
        return await ReadAsync<LanLogReply>(response, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a sentence to the room through a peer that holds this machine's credential.
    /// </summary>
    /// <remarks>
    /// This is how a machine that nobody can dial still gets said into the history: the peer it paired with is the
    /// one member it can reach, and what lands there spreads the way everything else does — by being pulled.
    /// </remarks>
    /// <param name="messageId">
    /// The id this machine minted for the sentence, which every peer is asked to keep as its own copy's identity.
    /// A window fanning one sentence out to several members names it here, because otherwise each of them numbers
    /// a different arrival and the room the others converge on holds the sentence once per member it reached.
    /// </param>
    /// <exception cref="LanTransferException">
    /// <see cref="LanRefusal.MessageTooLong"/> when the sentence is longer than a peer will hold, and
    /// <see cref="LanRefusal.BadRequest"/> when it says nothing at all — both named by the peer, not guessed here.
    /// </exception>
    public async Task<LanMessageWire> SayAsync(LanPeerEndpoint peer, string text, string? messageId = null, CancellationToken ct = default)
    {
        var token = await TokenForAsync(peer, ct).ConfigureAwait(false) ?? throw LanTransferException.For(LanRefusal.Unpaired);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QuestionTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, Build("/api/v1/messages", peer.Address, peer.Port))
        {
            Content = JsonContent(new LanSayRequest(text, messageId)),
        };
        AddCredential(request, token);
        using var response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
        return await ReadAsync<LanMessageWire>(response, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells a peer the name this machine now wants to be listed under.
    /// </summary>
    /// <remarks>
    /// One call per peer, and the answer is the name that peer really stored, because a device that chose more
    /// characters than a row can hold is listed under the ones it got — and a window that showed a name nobody
    /// else in the room can read would be a lie with a tick next to it. Nobody on the far side is asked anything,
    /// since a name is a claim about the machine making it and pairing already settled that it owns that claim.
    /// </remarks>
    public async Task<string> TellNameAsync(LanPeerEndpoint peer, string name, CancellationToken ct = default)
    {
        var token = await TokenForAsync(peer, ct).ConfigureAwait(false) ?? throw LanTransferException.For(LanRefusal.Unpaired);
        using var request = new HttpRequestMessage(HttpMethod.Post, Build("/api/v1/name", peer.Address, peer.Port))
        {
            Content = JsonContent(new LanNameRequest(name)),
        };
        AddCredential(request, token);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        return (await ReadAsync<LanNameReply>(response, ct).ConfigureAwait(false)).DeviceName;
    }

    /// <summary>
    /// Sends one item. A file is one <c>/inbox</c> write; a folder is a walk — one <c>/mkdir</c> per
    /// directory it holds, including the empty ones, then one write per file — so the tree the peer receives
    /// has the shape it was given rather than a pile of names.
    /// </summary>
    /// <remarks>
    /// Nothing here is batched into one request. A folder that fails on its ninth file has already landed
    /// eight, and the peer's own per-file staging means the ninth is a half-written
    /// <c>.fengsync.partial</c> rather than a truncated archive — the same contract a phone's upload gets.
    /// </remarks>
    /// <param name="messageId">
    /// The id of this machine's own announcement of the row, so the peer files what lands under it and every other
    /// member can ask for it later. Absent means an ordinary send: the bytes still land, and nobody hears about
    /// them except through the receive event on that peer's window.
    /// </param>
    public async Task<LanSendOutcome> SendAsync(LanPeerEndpoint peer, LanSendItem item, string? session = null,
        string? messageId = null, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var token = await TokenForAsync(peer, ct).ConfigureAwait(false)
            ?? throw LanTransferException.For(LanRefusal.Unpaired);
        if (item.IsDirectory) return await SendDirectoryAsync(peer, item, token, session, messageId, progress, ct).ConfigureAwait(false);

        using var body = File.OpenRead(item.AbsolutePath);
        var reply = await PushFileAsync(peer, token, item.Name, body, item.Bytes, item.ModifiedUtc, session, messageId,
            announcedAsDirectory: false, progress, ct).ConfigureAwait(false);
        return new LanSendOutcome(1, 0, reply.Bytes);
    }

    /// <summary>One file's bytes, with the name it should land under and the timestamp the sink will keep.</summary>
    public async Task<LanInboxReply> PushFileAsync(LanPeerEndpoint peer, string relativePath, Stream body, long bytes,
        DateTimeOffset? sourceWriteUtc, string? session = null, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var token = await TokenForAsync(peer, ct).ConfigureAwait(false) ?? throw LanTransferException.For(LanRefusal.Unpaired);
        return await PushFileAsync(peer, token, relativePath, body, bytes, sourceWriteUtc, session,
            messageId: null, announcedAsDirectory: false, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Asks the peer to hold a directory open, which is the only way an empty one arrives at all.</summary>
    public async Task PushDirectoryAsync(LanPeerEndpoint peer, string relativePath, string? session = null, CancellationToken ct = default)
    {
        var token = await TokenForAsync(peer, ct).ConfigureAwait(false) ?? throw LanTransferException.For(LanRefusal.Unpaired);
        await PushDirectoryAsync(peer, token, relativePath, session, messageId: null, ct).ConfigureAwait(false);
    }

    private async Task<LanSendOutcome> SendDirectoryAsync(LanPeerEndpoint peer, LanSendItem folder, string token,
        string? session, string? messageId, IProgress<long>? progress, CancellationToken ct)
    {
        var files = 0;
        var directories = 0;
        long bytes = 0;
        // The folder's own name first, so an empty row still becomes an empty directory on the far side rather
        // than nothing at all. After that the walk's own order already puts every directory before its contents,
        // which is the only order a receiver can use: a file's parent has to exist before its write is admitted.
        await PushDirectoryAsync(peer, token, folder.Name, session, messageId, ct).ConfigureAwait(false);
        directories++;

        foreach (var entry in LanFolderTree.Entries(folder.AbsolutePath))
        {
            var relative = $"{folder.Name}/{entry.RelativePath}";
            if (entry.IsDirectory)
            {
                await PushDirectoryAsync(peer, token, relative, session, messageId, ct).ConfigureAwait(false);
                directories++;
                continue;
            }

            var info = new FileInfo(Path.Combine(folder.AbsolutePath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            await using var body = File.OpenRead(info.FullName);
            var reply = await PushFileAsync(peer, token, relative, body, info.Length, info.LastWriteTimeUtc, session,
                messageId, announcedAsDirectory: true, progress, ct).ConfigureAwait(false);
            files++;
            bytes += reply.Bytes;
        }

        return new LanSendOutcome(files, directories, bytes);
    }

    private async Task PushDirectoryAsync(LanPeerEndpoint peer, string token, string relativePath, string? session, string? messageId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Build("/api/v1/mkdir", peer.Address, peer.Port));
        AddCredential(request, token);
        AddWireHeaders(request, relativePath, session, declaredBytes: null, sourceWriteUtc: null);
        AddAnnouncement(request, messageId, isDirectory: true);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await ReadAsync<LanMkdirReply>(response, ct).ConfigureAwait(false);
    }

    private async Task<LanInboxReply> PushFileAsync(LanPeerEndpoint peer, string token, string relativePath, Stream body,
        long bytes, DateTimeOffset? sourceWriteUtc, string? session, string? messageId, bool announcedAsDirectory,
        IProgress<long>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Build("/api/v1/inbox", peer.Address, peer.Port));
        AddCredential(request, token);
        AddWireHeaders(request, relativePath, session, bytes, sourceWriteUtc);
        AddAnnouncement(request, messageId, announcedAsDirectory);
        var content = new StreamContent(progress is null ? body : new ProgressStream(body, progress));
        if (bytes > 0) content.Headers.ContentLength = bytes;
        request.Content = content;

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync<LanInboxReply>(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Names the room entry these bytes belong to, and says whether that entry is a folder.
    /// </summary>
    /// <remarks>
    /// The kind travels with every file of a tree rather than with the announcement alone, because a folder arrives
    /// as separate writes and whichever one lands first is the one that has to create the entry with the right kind
    /// — a peer that filed a tree's first file as a plain file would announce a whole folder as one text document,
    /// and a later 接收 would ask for one file and be handed a zip it was never promised.
    /// </remarks>
    private static void AddAnnouncement(HttpRequestMessage request, string? messageId, bool isDirectory)
    {
        if (string.IsNullOrEmpty(messageId)) return;
        request.Headers.TryAddWithoutValidation(LanProtocol.HeaderMessage, messageId);
        if (isDirectory) request.Headers.TryAddWithoutValidation(LanProtocol.HeaderKind, LanGroupKind.Directory);
    }

    /// <summary>
    /// Reads the bytes behind one room entry from a member that says it holds them, while the answer is still open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One id, one entry, and no path anywhere in the call: a member that announced a folder is asked for the
    /// archive with <paramref name="archive"/> set, and a peer that does not hold the entry answers
    /// <see cref="LanRefusal.NotFound"/>, which is what lets a caller move on to the next holder rather than
    /// treating the refusal as damage.
    /// </para>
    /// <para>
    /// The bytes are handed over as a stream the caller never has to close, rather than returned as one. A response
    /// a caller must remember to dispose leaks a connection on every refusal path a room poller catches — and it
    /// catches most of them — while nothing here buffers, which is the other half of the point: a pulled gigabyte
    /// lands in the receive directory through the same staging path a pushed one does, and must not have to fit in
    /// memory on the way.
    /// </para>
    /// </remarks>
    public async Task<T> ReadAttachmentAsync<T>(LanPeerEndpoint peer, string messageId, bool archive,
        Func<Stream, Task<T>> consume, CancellationToken ct = default)
    {
        var token = await TokenForAsync(peer, ct).ConfigureAwait(false) ?? throw LanTransferException.For(LanRefusal.Unpaired);
        var attachment = AttachmentRequest(peer, token, messageId, archive);
        DeclareSelf(attachment);
        using var response = await _http.SendAsync(attachment, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // ReadAsync would translate the peer's own body here, and for this call the body is the file.
        if (!response.IsSuccessStatusCode)
        {
            var reason = await ReadReasonAsync(response, ct).ConfigureAwait(false);
            throw LanTransferException.For(LanApi.RefusalFor(reason) ?? LanApi.FromStatusCode((int)response.StatusCode));
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // The callback's own failure is the caller's business, not a refusal to translate: a sink that ran out of
        // room to publish into has nothing to do with what the peer answered.
        return await consume(body).ConfigureAwait(false);
    }

    private HttpRequestMessage AttachmentRequest(LanPeerEndpoint peer, string token, string messageId, bool archive)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            Build($"/api/v1/attachments/{messageId}{(archive ? "/zip" : "")}", peer.Address, peer.Port));
        AddCredential(request, token);
        return request;
    }

    /// <summary>
    /// Sends with the peer's answer translated into the named refusal it carried, so a caller catches one
    /// exception type and gets advice it can print rather than a status code to interpret again.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        DeclareSelf(request);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new LanTransferException(LanRefusal.BadRequest, "对端没有应答，这台电脑上的监听可能已经关了。", ex);
        }
        catch (OperationCanceledException)
        {
            // The window's own cancel, or a probe that outlived its welcome. Neither is a refusal by the peer,
            // and calling it one would tell the user that somebody said no.
            throw;
        }

        if (response.IsSuccessStatusCode) return response;
        var reason = await ReadReasonAsync(response, ct).ConfigureAwait(false);
        response.Dispose();
        throw LanTransferException.For(LanApi.RefusalFor(reason) ?? LanApi.FromStatusCode((int)response.StatusCode));
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var value = await JsonSerializer.DeserializeAsync<T>(body, LanApi.Json, ct).ConfigureAwait(false);
            // A 2xx with no body to read is this protocol answering something it has no shape for. Reporting it
            // as "not found" would be a guess; the honest statement is that the peer did not reply in kind.
            return value ?? throw new LanTransferException(LanRefusal.BadRequest, "对端的应答无法读取。");
        }
        finally { response.Dispose(); }
    }

    private static async Task<string?> ReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return (await JsonSerializer.DeserializeAsync<LanErrorReply>(body, LanApi.Json, ct).ConfigureAwait(false))?.Reason;
        }
        catch (Exception ex) when (ex is JsonException or IOException or OperationCanceledException)
        {
            // Something that is not this protocol answered — a proxy, a captive portal. Its status code is
            // still the only thing worth reading, so the reason is simply absent.
            return null;
        }
    }

    private Task<string?> TokenForAsync(LanPeerEndpoint peer, CancellationToken ct) => _received.TokenForAsync(peer.DeviceId, ct);

    private void AddCredential(HttpRequestMessage request, string token) =>
        request.Headers.Authorization = new AuthenticationHeaderValue(LanProtocol.AuthScheme, $"{_self.DeviceId}:{token}");

    /// <summary>
    /// Says where this machine can be dialled back, on every request.
    /// </summary>
    /// <remarks>
    /// Every request rather than only the pairing one, because the peer's record of this machine is refreshed by
    /// whichever call arrives: a port sent once and then dropped would be overwritten with the 0 a peer that
    /// reports nothing gets, and the row would stop being dialable an hour later for no reason anybody could see.
    /// </remarks>
    private void DeclareSelf(HttpRequestMessage request)
    {
        if (ListenPort is < LanOptions.MinimumPort or > LanOptions.MaximumPort) return;
        request.Headers.TryAddWithoutValidation(LanProtocol.HeaderPeerPort,
            ListenPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AddWireHeaders(HttpRequestMessage request, string relativePath, string? session, long? declaredBytes, DateTimeOffset? sourceWriteUtc)
    {
        // The header is ASCII on the wire, so the path is escaped here and unescaped by the peer. Uri's own
        // form is the one the server reads back with Uri.UnescapeDataString; nothing else round-trips a name
        // containing a percent or a plus.
        request.Headers.TryAddWithoutValidation(LanProtocol.HeaderRel, Uri.EscapeDataString(relativePath));
        if (!string.IsNullOrEmpty(session)) request.Headers.TryAddWithoutValidation(LanProtocol.HeaderSession, session);
        if (declaredBytes is { } total) request.Headers.TryAddWithoutValidation(LanProtocol.HeaderTotal, total.ToString());
        if (sourceWriteUtc is { } write) request.Headers.TryAddWithoutValidation(LanProtocol.HeaderMtime, write.ToUnixTimeMilliseconds().ToString());
    }

    private static string Build(string path, string address, int port) => new UriBuilder(Uri.UriSchemeHttp, address, port, path).Uri.ToString();

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, LanApi.Json), System.Text.Encoding.UTF8, "application/json");

    /// <summary>
    /// Counts bytes as the request stream drains them. The peer only sees a normal body; the window gets a
    /// progress bar for an upload, which is the one half of the feedback a phone page has and a computer does
    /// not get for free.
    /// </summary>
    private sealed class ProgressStream(Stream inner, IProgress<long> progress) : Stream
    {
        private long _written;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override int Read(byte[] buffer, int offset, int count) => ReadAdvance(inner.Read(buffer, offset, count));
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAdvance(await inner.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ReadAdvance(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));

        private int ReadAdvance(int read)
        {
            if (read > 0) progress.Report(Interlocked.Add(ref _written, read));
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
