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
public sealed record LanPeerEndpoint(string DeviceId, string Address, int Port);

/// <summary>What one basket row cost to hand over.</summary>
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
    /// <summary>How long a stranger is allowed to take answering 这里是 Feng Sync 吗 before the window says 无应答.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long one of the small calls around a transfer may take. It is a timeout rather than an open end because
    /// a poll for the room's history runs on a timer: a peer that stops answering must make that poll give up and
    /// the next one happen, not leave a task waiting forever on a machine whose window has closed.
    /// </summary>
    public static readonly TimeSpan QuestionTimeout = TimeSpan.FromSeconds(10);

    private readonly LanDeviceIdentity _self;
    private readonly LanPeerSecretStore _received;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <param name="received">
    /// Where the tokens other machines issued to this one are kept. Defaults to its own file rather than
    /// <c>lan-peer-secrets.dat</c>, because that one holds what this machine handed out and a reader of either
    /// file has to be able to tell which direction the credential points.
    /// </param>
    public LanPeerClient(LanDeviceIdentity self, LanPeerSecretStore? received = null, HttpClient? http = null)
    {
        _self = self;
        _received = received ?? new LanPeerSecretStore(DefaultReceivedPath());
        _ownsHttpClient = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            // A whole file is one request, and a gigabyte on a slow Wi-Fi link has no reasonable number of
            // seconds to promise in advance. Liveness is the caller's CancellationToken's job; short calls
            // around it get their own linked timeout instead.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>The credential file for tokens this machine was handed. Kept apart from the one it hands out.</summary>
    public static string DefaultReceivedPath() => Path.Combine(AppDataPaths.LanTransfer, "lan-received-tokens.dat");

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
    public async Task<LanPairReply> PairAsync(LanPeerEndpoint peer, string rawCode, CancellationToken ct = default)
    {
        if (!LanPairCode.TryNormalise(rawCode, out var code)) throw LanTransferException.For(LanRefusal.BadCode);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        var request = new HttpRequestMessage(HttpMethod.Post, Build("/api/v1/pair", peer.Address, peer.Port))
        {
            Content = JsonContent(new LanPairRequest(code, _self.DeviceId, _self.DeviceName, _self.Kind)),
        };
        using var response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
        var reply = await ReadAsync<LanPairReply>(response, ct).ConfigureAwait(false);
        await _received.StoreAsync(peer.DeviceId, reply.Token, ct).ConfigureAwait(false);
        return reply;
    }

    /// <summary>Drops the credential this machine holds for a peer. The peer's own row is unaffected — that is what 取消信任 on the other side is for.</summary>
    public Task<bool> ForgetAsync(LanPeerEndpoint peer, CancellationToken ct = default) => _received.ForgetAsync(peer.DeviceId, ct);

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
    /// <exception cref="LanTransferException">
    /// <see cref="LanRefusal.MessageTooLong"/> when the sentence is longer than a peer will hold, and
    /// <see cref="LanRefusal.BadRequest"/> when it says nothing at all — both named by the peer, not guessed here.
    /// </exception>
    public async Task<LanMessageWire> SayAsync(LanPeerEndpoint peer, string text, CancellationToken ct = default)
    {
        var token = await TokenForAsync(peer, ct).ConfigureAwait(false) ?? throw LanTransferException.For(LanRefusal.Unpaired);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QuestionTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, Build("/api/v1/messages", peer.Address, peer.Port))
        {
            Content = JsonContent(new LanSayRequest(text)),
        };
        AddCredential(request, token);
        using var response = await SendAsync(request, timeout.Token).ConfigureAwait(false);
        return await ReadAsync<LanMessageWire>(response, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one basket row. A file is one <c>/inbox</c> write; a folder is a walk — one <c>/mkdir</c> per
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
    /// Asks for the bytes behind one room entry, from a member that says it holds them.
    /// </summary>
    /// <remarks>
    /// One id, one file, and no path anywhere in the call: a member that announced a folder is asked for the
    /// archive through <see cref="AttachmentArchiveAsync"/>, and a peer that does not hold the entry answers
    /// <see cref="LanRefusal.NotFound"/>, which is what lets a caller move on to the next holder rather than
    /// treating the refusal as damage.
    /// </remarks>
    public Task<byte[]> AttachmentAsync(LanPeerEndpoint peer, string messageId, CancellationToken ct = default) =>
        FetchAsync(peer, messageId, archive: false, ct);

    /// <summary>Asks for a held folder as one archive.</summary>
    public Task<byte[]> AttachmentArchiveAsync(LanPeerEndpoint peer, string messageId, CancellationToken ct = default) =>
        FetchAsync(peer, messageId, archive: true, ct);

    private async Task<byte[]> FetchAsync(LanPeerEndpoint peer, string messageId, bool archive, CancellationToken ct)
    {
        var token = await TokenForAsync(peer, ct).ConfigureAwait(false) ?? throw LanTransferException.For(LanRefusal.Unpaired);
        using var response = await _http.SendAsync(
            AttachmentRequest(peer, token, messageId, archive), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // ReadAsync would translate the peer's own body here, and for this call the body is the file.
        if (!response.IsSuccessStatusCode) throw LanTransferException.For(LanApi.RefusalFor(await ReadReasonAsync(response, ct).ConfigureAwait(false))
            ?? LanApi.FromStatusCode((int)response.StatusCode));

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
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
