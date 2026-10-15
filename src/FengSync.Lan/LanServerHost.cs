using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FengSync.Lan;

/// <summary>What a listener needs to serve: the decisions, the identity, the code, and the one disk writer.</summary>
/// <param name="DefaultBodyLimitBytes">
/// The global cap, which exists for the JSON routes. <c>/inbox</c> lifts it per request rather than through
/// this number, because a cap that also applies to a file would silently refuse the one request the feature is
/// for, and a cap raised globally would leave every other route able to be flooded.
/// </param>
public sealed record LanServerOptions(
    LanSession Session,
    LanDeviceIdentity Self,
    LanPairingState Pairing,
    LanReceiveSink Sink,
    long DefaultBodyLimitBytes = 30L * 1024 * 1024)
{
    /// <summary>The reader this machine keeps its copy of the room with, when this listener has one.</summary>
    /// <remarks>
    /// A browser cannot be dialled, so a member that is one can only ask the computer it paired with to receive an
    /// entry and hand it over. Deciding whom to ask is the room's job and no route may grow a second copy of it, so
    /// the listener is handed the very <see cref="LanGroupRoom"/> the window polls with. Absent is a listener with
    /// no reader — a harness that mapped the routes and nothing else — and an entry then stays where it is.
    /// </remarks>
    public LanGroupRoom? Room { get; init; }

    public LanOptions Validate()
    {
        Session.Options.Validate();
        if (DefaultBodyLimitBytes <= 0) throw new ArgumentOutOfRangeException(nameof(DefaultBodyLimitBytes), "请求体上限必须是正数。");
        return Session.Options;
    }
}

/// <summary>
/// The Kestrel listener, the routes, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately thin: every judgement on a request lives in <see cref="LanSession"/> or
/// <see cref="LanReceiveSink"/>, both of which are testable without a socket. What is here is the part a fake
/// client cannot prove — that a header round-trips a Chinese name, that a range request answers 206, that a
/// refusal carries a reason body, and that the listener binds and stops when the window tells it to.
/// </para>
/// <para>
/// HTTP/1.1 only: a phone browser does not send h2c prior-knowledge, and agreeing to a protocol nobody can
/// speak buys nothing. Cleartext on purpose — see the note in <c>AGENTS.md</c> about what this listener is
/// exposed to, and why a per-request signature would not have changed it.
/// </para>
/// </remarks>
public sealed class LanServerHost : IAsyncDisposable
{
    private readonly LanServerOptions _options;
    private WebApplication? _app;
    private IReadOnlyList<string> _listenAddresses = [];

    private LanServerHost(LanServerOptions options) => _options = options;

    /// <summary>Raises the first decision a stranger's bytes face, before anybody else can react to it.</summary>
    public event Action<LanAdmissionEventArgs>? AdmissionDecided;

    /// <summary>A pairing request was judged, either way, and this is who made it.</summary>
    public event Action<LanPairAttemptEventArgs>? PairAttempted;

    /// <summary>A device already on the list corrected the name this machine calls it by.</summary>
    public event Action<LanPeerRenamedEventArgs>? PeerRenamed;

    /// <summary>Bytes landed in the receive directory.</summary>
    public event Action<LanInboxEventArgs>? InboxReceived;

    /// <summary>A directory a peer asked for was created.</summary>
    public event Action<LanDirectoryEventArgs>? DirectoryReceived;

    /// <summary>Bytes this machine handed to a peer.</summary>
    public event Action<LanOutboundEventArgs>? OutboundDelivered;

    /// <summary>How far a transfer has got. Throttled here so a gigabyte does not become twelve thousand events.</summary>
    public event Action<LanProgressEventArgs>? TransferProgress;

    public int Port => _options.Session.Options.Port;

    /// <summary>The addresses a peer can dial, as <c>ip:port</c>, LAN candidates first and loopback last.</summary>
    public IReadOnlyList<string> ListenAddresses => _listenAddresses;

    /// <summary>
    /// Binds the port and maps the routes. Throws <see cref="LanTransferException"/> with
    /// <see cref="LanRefusal.PortUnavailable"/> when something else already holds it, which is the one start-up
    /// failure the window can answer with an action rather than a stack trace.
    /// </summary>
    public static async Task<LanServerHost> StartAsync(LanServerOptions options, CancellationToken ct = default)
    {
        var host = new LanServerHost(options);
        await host.BindAsync(ct).ConfigureAwait(false);
        return host;
    }

    private async Task BindAsync(CancellationToken ct)
    {
        var transfer = _options.Validate();
        // Captured before the port is bound, so no peer can pair into the window and then be judged as a
        // device it had always trusted.
        await _options.Session.PrimeTrustSnapshotAsync(ct).ConfigureAwait(false);
        // Armed as the service starts, because a listener nobody can pair into is not doing anything. Arm
        // replaces only a spent or aged code, so the window's 重新生成 button stays meaningful.
        _options.Pairing.Arm();
        _listenAddresses = [.. LanNetwork.Probe().Select(candidate => $"{candidate.Address}:{transfer.Port}")];

        try
        {
            // CreateEmptyBuilder rather than CreateBuilder, because this is a desktop process: the default
            // builder reads appsettings.json from a directory the user never chose, installs console logging
            // nobody reads, and brings a host lifetime built for a service. The empty one is the same Kestrel
            // with only what is named here — routing, and a log factory with no providers.
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Services.AddRouting();
            builder.Services.AddLogging(logging => logging.ClearProviders());
            builder.WebHost.UseKestrel(kestrel =>
            {
                kestrel.Limits.MaxRequestBodySize = _options.DefaultBodyLimitBytes;
                kestrel.AddServerHeader = false;
                // Every interface, not a chosen one: the phone may arrive on whichever network the user is
                // actually on, and the address list above is what makes that visible to them.
                kestrel.Listen(IPAddress.Any, transfer.Port, listen => listen.Protocols = HttpProtocols.Http1);
            });

            var app = builder.Build();
            // Mapped straight onto the application: a WebApplication is its own endpoint route builder, and the
            // UseRouting/UseEndpoints pair is the older shape that needed a service provider of its own.
            MapRoutes(app);
            _app = app;
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            throw new LanTransferException(LanRefusal.PortUnavailable, $"端口 {transfer.Port} 无法监听。", ex);
        }
        // A cancellation asked for by the window is not a port problem, and dressing it up as one would point
        // the user at a number that was never the issue.
    }

    /// <summary>Stops serving and releases the port, so the next window opening can bind the same number.</summary>
    public async ValueTask DisposeAsync()
    {
        _options.Pairing.Invalidate();
        await DisposeCoreAsync().ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await app.StopAsync(shutdown.Token).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // A handler still writing a gigabyte cannot be made to stop, and the window closing is not the
            // user's mistake. The port is released by the process either way.
        }
    }

    /// <summary>
    /// The routes, each registered as a typed handler rather than as a bare method group.
    /// </summary>
    /// <remarks>
    /// The overload a method group binds to here is <c>RequestDelegate</c>, whose return value the pipeline
    /// throws away — an endpoint mapped that way answers an empty 200 instead of the JSON the handler built.
    /// Naming the delegate type makes the routing layer build the handler and write its <see cref="IResult"/>,
    /// which is the difference between a working protocol and one that silently returns nothing.
    /// </remarks>
    private void MapRoutes(IEndpointRouteBuilder endpoints)
    {
        // The page a scanned code opens, and the one route a stranger reaches with no credential at all:
        // pairing is something a person does, so it has to have somewhere to be done from.
        endpoints.MapGet("/p", (Func<HttpContext, Task<IResult>>)HandlePageAsync);

        endpoints.MapGet("/api/v1/hello", (Func<HttpContext, Task<IResult>>)HandleHelloAsync);
        endpoints.MapPost("/api/v1/pair", (Func<HttpContext, Task<IResult>>)HandlePairAsync);
        endpoints.MapGet("/api/v1/peers", (Func<HttpContext, Task<IResult>>)HandlePeersAsync);
        endpoints.MapDelete("/api/v1/peers/mine", (Func<HttpContext, Task<IResult>>)HandleForgetSelfAsync);
        // Two verbs about the same row seen from the other side: a device correcting what it is called, and a
        // device taking itself off the list.
        endpoints.MapPost("/api/v1/name", (Func<HttpContext, Task<IResult>>)HandleNameAsync);
        endpoints.MapPost("/api/v1/inbox", (Func<HttpContext, Task<IResult>>)HandleInboxAsync);
        endpoints.MapPost("/api/v1/mkdir", (Func<HttpContext, Task<IResult>>)HandleMkdirAsync);

        // The room, in both directions: what this copy has heard past a number the caller named, and a sentence
        // somebody is adding to it.
        endpoints.MapGet("/api/v1/log", (Func<HttpContext, Task<IResult>>)HandleLogAsync);
        endpoints.MapPost("/api/v1/messages", (Func<HttpContext, Task<IResult>>)HandleSayAsync);
        endpoints.MapGet("/api/v1/basket", (Func<HttpContext, Task<IResult>>)HandleBasketAsync);
        endpoints.MapGet("/api/v1/basket/{id}", (Func<string, HttpContext, Task<IResult>>)HandleDownloadAsync);
        endpoints.MapGet("/api/v1/basket/{id}/zip", (Func<string, HttpContext, Task<IResult>>)HandleArchiveAsync);

        // The same two answers for the room's own entries: a peer that was told about a file asks whoever holds it,
        // and holds it by id rather than by a row of somebody's send basket.
        endpoints.MapGet("/api/v1/attachments/{id}", (Func<string, HttpContext, Task<IResult>>)HandleAttachmentAsync);
        endpoints.MapGet("/api/v1/attachments/{id}/zip", (Func<string, HttpContext, Task<IResult>>)HandleAttachmentArchiveAsync);
        // A member that cannot be dialed cannot dial out either, so a browser asks this machine to become the holder
        // first. The asking, the choosing of whom to ask, and the landing are all the room's; this route only answers.
        endpoints.MapPost("/api/v1/attachments/{id}/receive", (Func<string, HttpContext, Task<IResult>>)HandleAttachmentReceiveAsync);

        // Anything else answers in this protocol's shape. A bare 404 HTML page would leave a peer guessing
        // whether it had found a Feng Sync at all, which is the question /hello exists to answer.
        endpoints.MapFallback((Func<HttpContext, Task<IResult>>)HandleNotFoundAsync);
    }

    private Task<IResult> HandleNotFoundAsync(HttpContext http) => Task.FromResult(Answer(LanRefusal.NotFound));

    /// <summary>
    /// The page a phone opens, sent so that a reload always asks this process again.
    /// </summary>
    /// <remarks>
    /// No caching, because a page a phone keeps would show a pairing code that has already been spent — the code
    /// is good for exactly one try, and the only honest answer to the second is a refusal. No sniffing, because
    /// the bytes are this process's own and there is nothing for a browser to second-guess about them.
    /// </remarks>
    private Task<IResult> HandlePageAsync(HttpContext http)
    {
        http.Response.Headers["Cache-Control"] = "no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Task.FromResult<IResult>(Results.Text(LanPhonePage.Html, LanPhonePage.ContentType, LanPhonePage.Body));
    }

    private Task<IResult> HandleHelloAsync(HttpContext http) =>
        Task.FromResult(Json(_options.Session.Hello(_options.Self)));

    private async Task<IResult> HandlePairAsync(HttpContext http)
    {
        var address = RemoteAddress(http);
        var port = PeerPort(http);
        LanPairRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<LanPairRequest>(http.Request.Body, LanApi.Json, http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            RaisePairAttempted(new LanContact("", "", "unknown", address, port), LanRefusal.BadRequest);
            return Answer(LanRefusal.BadRequest);
        }

        var result = await _options.Session.PairAsync(request, address, port, http.RequestAborted).ConfigureAwait(false);
        RaisePairAttempted(result.Contact, result.Refusal);
        if (!result.Accepted) return Answer(result.Refusal);

        // The device id comes back in the reply's own row, so the phone does not have to remember what it made up.
        OfferCredential(http, result.Peer!.DeviceId, result.Token!);
        return Json(new LanPairReply(result.Token!, LanApi.WireVersion, PeerOf(result.Peer)), StatusCodes.Status201Created);
    }

    /// <summary>
    /// The cookie that carries a fresh credential to a browser that cannot write a header.
    /// </summary>
    /// <remarks>
    /// A phone takes a file off this machine through an ordinary link, and a link can carry nothing but what the
    /// browser stores for this origin. So the credential leaves here as an HttpOnly cookie and never as part of
    /// an address: a URL is something a person copies, shares, and leaves in a history, and a token outlives
    /// this window. Deleting a device's own row deletes this cookie too, in
    /// <see cref="HandleForgetSelfAsync"/>, so revoking means the phone stops working and not merely that its
    /// row is gone.
    /// </remarks>
    private static void OfferCredential(HttpContext http, string deviceId, string token) =>
        http.Response.Cookies.Append(LanProtocol.CookieName, $"{deviceId}:{token}", new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
        });

    private async Task<IResult> HandlePeersAsync(HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        // A peer is told about itself and nobody else. There is no endpoint that hands a device the list of
        // every other device this machine trusts, because a phone has no business learning what else is paired.
        var records = await _options.Session.Peers.LoadAsync(http.RequestAborted).ConfigureAwait(false);
        var own = records.FirstOrDefault(record =>
            string.Equals(record.DeviceId, auth.Contact!.DeviceId, StringComparison.OrdinalIgnoreCase));
        return own is null ? Answer(LanRefusal.Unpaired) : Json(PeerOf(own));
    }

    private async Task<IResult> HandleForgetSelfAsync(HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        await _options.Session.ForgetOwnCredentialAsync(auth.Contact!, http.RequestAborted).ConfigureAwait(false);
        // The row and the secret are gone from this machine, and the cookie would be the one thing left that
        // still opens a door — so the answer that removes the device removes the browser's copy as well.
        http.Response.Cookies.Delete(LanProtocol.CookieName, new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Lax });
        return Results.NoContent();
    }

    /// <summary>
    /// Lets a peer correct the name this machine lists it under, which is the only way a 改名 reaches here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The device id is the one the credential proved, so a caller relabels its own row and cannot point at
    /// another member's — which is why the body carries no id, and why the write takes
    /// <see cref="LanAuthResult.Contact"/> rather than anything read off the request. A name is the one claim about
    /// itself this library has always accepted, because it can only ever be a claim about the machine making it.
    /// </para>
    /// <para>
    /// What gets stored is <see cref="LanProtocol.SanitizePeerName"/>'s answer, and that answer is sent back for
    /// the same reason a received file's stored name is: a device that asked for sixty characters is listed under
    /// the characters it actually got, and must not keep showing a name nobody on the room can read. The room's
    /// own rows are not rewritten — what was said under an old name stays as it was said — and the event is what
    /// lets the window narrate the change in its own words, since this library holds no table of those.
    /// </para>
    /// </remarks>
    private async Task<IResult> HandleNameAsync(HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        LanNameRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<LanNameRequest>(http.Request.Body, LanApi.Json, http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return Answer(LanRefusal.BadRequest);
        }

        var name = LanProtocol.SanitizePeerName(request?.DeviceName);
        // A blank is not a rename in either direction: an empty row is the failure the fallback exists to avoid,
        // and which name to use instead is the person's decision rather than this route's.
        if (name.Length == 0) return Answer(LanRefusal.BadRequest);

        var contact = auth.Contact!;
        await _options.Session.Peers.RenameAsync(contact.DeviceId, name, http.RequestAborted).ConfigureAwait(false);
        PeerRenamed?.Invoke(new LanPeerRenamedEventArgs(contact with { DeviceName = name }, contact.DeviceName));
        return Json(new LanNameReply(name));
    }

    private async Task<IResult> HandleInboxAsync(HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);
        var contact = auth.Contact!;

        var wire = WirePath(http);
        var offer = new LanInboundOffer(contact, wire, DeclaredBytes(http), false, SessionOf(http));
        var admission = await AdmitAsync(offer, http.RequestAborted).ConfigureAwait(false);
        if (admission != LanRefusal.None) return Answer(admission);

        // Files are not bounded by a limit invented for JSON bodies, so this route lifts it for itself and
        // leaves every other route covered.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = null;

        LanReceived received;
        try
        {
            received = await _options.Sink
                .ReceiveAsync(http.Request.Body, wire, offer.DeclaredBytes, ReportedWriteUtc(http),
                    ProgressFor(contact, wire, offer.DeclaredBytes, inbound: true), http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (LanTransferException ex)
        {
            return Answer(ex.Refusal);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The peer went away mid-body. The sink deleted its staging file, there is no response to write,
            // and the transfer list shows the row as cancelled rather than as this exception.
            return Results.Empty;
        }

        InboxReceived?.Invoke(new LanInboxEventArgs(contact, received, offer.Session));
        TagArrival(http, contact, received.RelativePath, received.Bytes, storedIsDirectory: false);
        return Json(new LanInboxReply(received.Name, received.Bytes, received.Renamed, received.RelativePath),
            StatusCodes.Status201Created);
    }

    private async Task<IResult> HandleMkdirAsync(HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        var wire = WirePath(http);
        var offer = new LanInboundOffer(auth.Contact!, wire, 0, true, SessionOf(http));
        var admission = await AdmitAsync(offer, http.RequestAborted).ConfigureAwait(false);
        if (admission != LanRefusal.None) return Answer(admission);

        string relative;
        try
        {
            relative = await _options.Sink.EnsureDirectoryAsync(wire, http.RequestAborted).ConfigureAwait(false);
        }
        catch (LanTransferException ex) { return Answer(ex.Refusal); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException) { return Results.Empty; }

        DirectoryReceived?.Invoke(new LanDirectoryEventArgs(auth.Contact!, relative, offer.Session));
        TagArrival(http, auth.Contact!, relative, 0, storedIsDirectory: true);
        return Json(new LanMkdirReply(relative), StatusCodes.Status201Created);
    }

    /// <summary>
    /// Gives a push its entry in the room, when the push named one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A write with no <c>X-FS-Msg</c> stays what it always was — a file in the receive directory — which is what
    /// a peer that knows nothing of the room gets. Nothing about the write changes on the strength of the tag, so a
    /// peer that names an id gains the right to be asked for those bytes later, and nothing else.
    /// </para>
    /// <para>
    /// The folder question comes from the header rather than from the arrival, because a tree lands as many file
    /// writes and the one entry that announces it has to be a folder whichever file happened to arrive first. That
    /// entry names the tree's own root (<see cref="LanRelativePath.RootOf"/>) and reports no size, the same two
    /// choices <see cref="LanSendItem.Of"/> makes for a folder row: totalling a subtree is a walk nobody asked for.
    /// </para>
    /// <para>
    /// Both paths handed to the log are this class's own answers, not the peer's: the stored relative path is what
    /// the sink settled on after its collision rules, and the absolute path is that same name composed by the sink.
    /// A peer that sent <c>x.txt</c> and got stored as <c>x (1).txt</c> is announced under the name that is really
    /// there, which is the only name a later 接收 can open.
    /// </para>
    /// </remarks>
    private void TagArrival(HttpContext http, LanContact contact, string storedRelative, long storedBytes, bool storedIsDirectory)
    {
        var messageId = Header(http, LanProtocol.HeaderMessage);
        if (messageId.Length == 0) return;

        var isFolder = storedIsDirectory
            || string.Equals(Header(http, LanProtocol.HeaderKind), LanGroupKind.Directory, StringComparison.OrdinalIgnoreCase);
        var relative = isFolder ? LanRelativePath.RootOf(storedRelative) : storedRelative;
        _options.Session.Group.Hold(contact, messageId, relative, isFolder ? 0 : storedBytes, isFolder,
            _options.Sink.AbsolutePathOf(relative), DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// This machine's copy of what the room said, past the number the caller named.
    /// </summary>
    /// <remarks>
    /// The number is the caller's watermark on <i>this</i> copy: every host numbers its own arrival order, so a
    /// poller keeps one per peer and never one for the room — there is no member that could hand out a shared
    /// number. A <c>since</c> that is not a number is refused rather than read as zero, because answering a
    /// malformed watermark with the whole history would turn a caller's bug into a large response, and a
    /// poller that did that every few seconds would look only a little different from one copying the room out.
    /// </remarks>
    private async Task<IResult> HandleLogAsync(HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        var raw = http.Request.Query["since"].ToString();
        long since = 0;
        if (raw.Length > 0 && (!long.TryParse(raw, out since) || since < 0)) return Answer(LanRefusal.BadRequest);

        var group = _options.Session.Group;
        // A line about this window — a firewall question answered, a code regenerated — is narration, not
        // something a peer said, so it stays here. Every host can see its own room change; none of them needs to
        // hear about another's.
        var entries = group.Since(since)
            .Where(entry => entry.AuthorDeviceId.Length > 0)
            .Select(entry => entry.ToWire())
            .ToList();
        return Json(new LanLogReply(LanApi.WireVersion, group.Head, entries));
    }

    /// <summary>
    /// Adds one sentence to the room, from a peer that can only ever reach this machine.
    /// </summary>
    /// <remarks>
    /// A browser cannot be dialed, so a phone's text has exactly one way into the history and this is it; the
    /// entry then travels to everybody else the way every other entry does — by being pulled. A sentence is not
    /// gated by <see cref="LanSession.AdmitWriteAsync"/>, because that gate exists for bytes landing on the
    /// user's disk, and nothing here touches disk. What it does cost is a slot in a capped in-memory copy, which
    /// pairing alone already buys.
    /// </remarks>
    private async Task<IResult> HandleSayAsync(HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        LanSayRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<LanSayRequest>(http.Request.Body, LanApi.Json, http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return Answer(LanRefusal.BadRequest);
        }

        var refusal = _options.Session.Group.TryAppendText(auth.Contact!, request?.Text, DateTimeOffset.UtcNow, out var entry,
            request?.MessageId);
        if (refusal != LanRefusal.None || entry is null) return Answer(refusal);
        return Json(entry.ToWire(), StatusCodes.Status201Created);
    }

    private async Task<IResult> HandleBasketAsync(HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        // What a peer may read is exactly this list. There is no route that takes a path, which is why a token
        // cannot be used to walk the machine's disk.
        return Json(_options.Session.Basket.Snapshot().Select(item => item.ToWire()).ToList());
    }

    private async Task<IResult> HandleDownloadAsync(string id, HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        var (item, error) = Find(id, wantsArchive: false);
        if (error is not null) return error;
        return ServeFile(http, auth.Contact!, item!);
    }

    private async Task<IResult> HandleArchiveAsync(string id, HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        var (item, error) = Find(id, wantsArchive: true);
        if (error is not null) return error;
        return await ServeArchiveAsync(http, auth.Contact!, item!).ConfigureAwait(false);
    }

    /// <summary>
    /// The bytes behind one room entry, addressed by the id its author minted.
    /// </summary>
    /// <remarks>
    /// A peer asks whoever holds an entry rather than the machine that announced it, because the room has no
    /// hierarchy: every member that holds the file may serve it, and one that does not answers 这里没有 for that
    /// id. Nothing on this path is a caller-built read — the id selects an entry, and only an entry whose path
    /// <i>this</i> machine wrote (from a push it published, or from its own composer) carries one at all.
    /// </remarks>
    private async Task<IResult> HandleAttachmentAsync(string id, HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        var (item, error) = Held(id, wantsArchive: false);
        if (error is not null) return error;
        return ServeFile(http, auth.Contact!, item!);
    }

    /// <summary>A held folder, as one archive, for the member that wants the whole tree rather than file by file.</summary>
    private async Task<IResult> HandleAttachmentArchiveAsync(string id, HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        var (item, error) = Held(id, wantsArchive: true);
        if (error is not null) return error;
        return await ServeArchiveAsync(http, auth.Contact!, item!).ConfigureAwait(false);
    }

    /// <summary>
    /// Receives one room entry into this machine's receive directory, so that the one member of the room which
    /// cannot dial anybody can take it from here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A browser has no listener, so a phone can only ever reach the computer it paired with. When that computer
    /// does not hold the bytes an entry names, the phone's one honest move is to ask it to become a holder — which
    /// is exactly what the room's own receive does for a member that can be dialed. This route therefore adds no
    /// rule of its own: whom to ask, in what order, and where the bytes land all live in
    /// <see cref="LanGroupRoom.ReceiveAsync"/>, and a second answer to those questions written here is how a phone
    /// and a computer end up disagreeing about the same 接收.
    /// </para>
    /// <para>
    /// The answer is the entry as this machine now holds it, not a path. A phone has no use for a Windows directory,
    /// and <c>LocalPath</c> has never crossed the wire — it is a statement about this machine, which is why the row
    /// the page gets back says 本机有了 and nothing about where.
    /// </para>
    /// </remarks>
    private async Task<IResult> HandleAttachmentReceiveAsync(string id, HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        // A listener with no reader has nothing to ask with, so the entry stays where it is. This is a state a
        // harness reaches and a user does not: the window builds the reader beside the listener and takes it down
        // before the port closes.
        if (_options.Room is not { } room) return Answer(LanRefusal.NotFound);

        // There is no form gate here, and that is the read route's own stance rather than an oversight: an id that
        // is not an id, one this copy has never heard of, and one it holds no path for all answer 这里没有, because a
        // member keeping its own copy of the room cannot tell a caller which of the three it met. The id is only
        // ever a key in this machine's own log — never a path, and nothing on this route composes one from it.
        var result = await room.ReceiveAsync(id, http.RequestAborted).ConfigureAwait(false);
        if (result.Refusal != LanRefusal.None) return Answer(result.Refusal);
        return result.Entry is null ? Answer(LanRefusal.NotFound) : Json(result.Entry.ToWire());
    }

    /// <summary>
    /// Puts one local file on the response, with range support and the name the peer needs back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both read routes come through here, because a basket row and a room entry differ only in where their path
    /// came from — and two downloads that behave differently is exactly the drift nobody notices until a phone can
    /// resume one of them and not the other.
    /// </para>
    /// <para>
    /// Range support is the whole point of this shape: a phone that drops a large download can come back for the
    /// tail instead of starting over, which is the property an archive cannot offer. The name is written here rather
    /// than handed to <c>Results.File</c>, because that call would answer a single ASCII filename and a Chinese
    /// download name is precisely what the peer needs intact.
    /// </para>
    /// </remarks>
    private IResult ServeFile(HttpContext http, LanContact peer, LanSendItem item)
    {
        OutboundDelivered?.Invoke(new LanOutboundEventArgs(peer, item, IsArchive: false));
        http.Response.Headers.ContentDisposition = Attachment(item.Name);
        return Results.File(item.AbsolutePath, "application/octet-stream", fileDownloadName: null, enableRangeProcessing: true);
    }

    private async Task<IResult> ServeArchiveAsync(HttpContext http, LanContact peer, LanSendItem item)
    {
        OutboundDelivered?.Invoke(new LanOutboundEventArgs(peer, item, IsArchive: true));
        // The archive is written onto the response, so its length is unknown and the transfer is chunked; a
        // peer cannot ask for a range of something still being compressed.
        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = Attachment($"{item.Name}.zip");
        // Kestrel refuses a synchronous write on a response body, and <c>ZipArchive</c> cannot be disposed
        // otherwise — it has no asynchronous dispose, measured here by trying one and being told the archive
        // never closed. So this one response lifts that gate, which is what lets the central directory land;
        // every other route keeps Kestrel's protection untouched.
        if (http.Features.Get<IHttpBodyControlFeature>() is { } body) body.AllowSynchronousIO = true;
        try
        {
            await LanZipStream.WriteAsync(http.Response.Body, item.AbsolutePath, http.RequestAborted).ConfigureAwait(false);
        }
        // A peer that dropped mid-archive leaves a truncated file on the phone and nothing here. There is no
        // response left to write: the status and the headers went out with the first flush.
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }

        return Written.Instance;
    }

    private (LanSendItem? Item, IResult? Error) Find(string id, bool wantsArchive)
    {
        if (!Guid.TryParse(id, out var guid)) return (null, Answer(LanRefusal.NotFound));
        var item = _options.Session.Basket.Snapshot().FirstOrDefault(candidate => candidate.Id == guid);
        if (item is null) return (null, Answer(LanRefusal.NotFound));
        if (item.IsDirectory != wantsArchive)
            // A folder has no single-length answer: it is served as an archive, and a client that asked for the
            // bare item would otherwise receive a file that is not the folder it named.
            return (null, Answer(LanRefusal.BadRequest));
        return Exists(wantsArchive, item.AbsolutePath) ? (item, null) : (null, Answer(LanRefusal.NotFound));
    }

    /// <summary>
    /// The entry a caller named, when this machine can actually hand its bytes over.
    /// </summary>
    /// <remarks>
    /// Three refusals and one side effect. An id that is not a message id, one this copy has never heard of, and
    /// one it holds no path for all read the same way to a peer — 这里没有 — which is the honest form of a room where
    /// every member keeps its own copy: a caller cannot be told which of the three it hit, and an answer naming
    /// "the announcement exists somewhere" would offer bytes nobody promised. The kind question is the one answer
    /// the caller can act on, so a file asked for as an archive is a bad request rather than a not found.
    /// <para>
    /// The side effect is the vanished file: a held entry whose path stopped existing is un-marked here, so this
    /// copy stops claiming it can serve what it no longer has, and the next 接收 goes to whoever does. The entry
    /// itself stays — what the room said is not this machine's to erase.
    /// </para>
    /// </remarks>
    private (LanSendItem? Item, IResult? Error) Held(string id, bool wantsArchive)
    {
        if (!LanProtocol.IsMessageId(id) || !_options.Session.Group.TryFind(id, out var entry) || !entry.HeldHere)
            return (null, Answer(LanRefusal.NotFound));
        if (entry.IsDirectory != wantsArchive) return (null, Answer(LanRefusal.BadRequest));

        var path = entry.LocalPath;
        if (string.IsNullOrEmpty(path) || !Exists(entry.IsDirectory, path))
        {
            // The claim was made when the bytes landed, and this is the moment the room learns they are gone: the
            // entry keeps its sentence and loses 接收, so a poller is not invited to ask here again every round.
            _options.Session.Group.DropHeld(id);
            return (null, Answer(LanRefusal.NotFound));
        }

        LanSendItem item;
        try
        {
            // The row the window records for a delivery is the same description it would show for a basket row, so
            // the rule that reads a path is reused rather than re-derived from the entry's own claimed size.
            item = LanSendItem.Of(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The check above and this read are not one atomic act, and a member that deleted the received file in
            // between is the same vanished case a moment late — so it earns the same answer, and the same un-mark.
            _options.Session.Group.DropHeld(id);
            return (null, Answer(LanRefusal.NotFound));
        }

        return (item, null);
    }

    private static bool Exists(bool directory, string path) =>
        directory ? Directory.Exists(path) : File.Exists(path);

    private async Task<LanAuthResult> AuthorizeAsync(HttpContext http) =>
        await _options.Session.AuthenticateAsync(CredentialOf(http.Request),
            Header(http, LanProtocol.HeaderDeviceName), Header(http, LanProtocol.HeaderDeviceKind),
            RemoteAddress(http), PeerPort(http), http.RequestAborted).ConfigureAwait(false);

    /// <summary>
    /// The credential a request carries, in the bare <c>deviceId:token</c> form the session checks, from either
    /// transport a peer has.
    /// </summary>
    /// <remarks>
    /// A peer that authors its own requests — a computer, or a page talking to itself — sends the header. A phone
    /// browser following a plain download link can send nothing but what it already stores for this origin, and
    /// that is what the cookie is for. The header wins when both are present, because a peer that went to the
    /// trouble of naming its own credential means that one.
    /// </remarks>
    private static string CredentialOf(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.Length > 0) return header;
        return request.Cookies.TryGetValue(LanProtocol.CookieName, out var cookie) ? cookie : "";
    }

    private async Task<LanRefusal> AdmitAsync(LanInboundOffer offer, CancellationToken ct)
    {
        var decision = await _options.Session.AdmitWriteAsync(offer, ct).ConfigureAwait(false);
        AdmissionDecided?.Invoke(new LanAdmissionEventArgs(offer, decision));
        return decision;
    }

    private void RaisePairAttempted(LanContact contact, LanRefusal refusal) =>
        PairAttempted?.Invoke(new LanPairAttemptEventArgs(contact, refusal));

    /// <summary>
    /// A progress reporter that raises at most one event per megabyte or per quarter second.
    /// </summary>
    /// <remarks>
    /// A tick per read would put twelve thousand dispatcher posts on the UI thread for one large file, and the
    /// bar would look smoother while the window got slower. The first and last values always pass, because a
    /// transfer that reported nothing until it finished is not progress.
    /// </remarks>
    private IProgress<long> ProgressFor(LanContact contact, string path, long totalBytes, bool inbound)
    {
        var started = Stopwatch.GetTimestamp();
        var lastBytes = -1L;
        var lastRaised = started;
        return new LambdaProgress(value =>
        {
            var now = Stopwatch.GetTimestamp();
            var moved = lastBytes < 0 || value - lastBytes >= 1024 * 1024;
            var aged = (now - lastRaised) >= Stopwatch.Frequency / 4;
            if (!(moved || aged) && value != totalBytes) return;
            lastBytes = value;
            lastRaised = now;
            TransferProgress?.Invoke(new LanProgressEventArgs(contact, path, value, totalBytes, inbound));
        });
    }

    private IResult Json(object? body, int statusCode = StatusCodes.Status200OK) =>
        Results.Text(JsonSerializer.Serialize(body, LanApi.Json), "application/json; charset=utf-8", System.Text.Encoding.UTF8, statusCode);

    private IResult Answer(LanRefusal refusal)
    {
        var (status, reason) = LanApi.AnswerFor(refusal);
        return Json(new LanErrorReply("fs_lan", reason), status);
    }

    private static string Header(HttpContext http, string name) => http.Request.Headers[name].ToString();

    /// <summary>
    /// The path a peer named, percent-decoded with <see cref="Uri.UnescapeDataString"/> and never with a form
    /// decoder: <c>+</c> is legal in a filename and a form decoder would turn it into a space, renaming the
    /// user's file on the way in.
    /// </summary>
    private static string WirePath(HttpContext http)
    {
        var raw = http.Request.Headers[LanProtocol.HeaderRel].ToString();
        return raw.Length == 0 ? "" : Uri.UnescapeDataString(raw);
    }

    private static long DeclaredBytes(HttpContext http) =>
        long.TryParse(http.Request.Headers[LanProtocol.HeaderTotal].ToString(), out var bytes) && bytes >= 0 ? bytes : 0;

    private static string? SessionOf(HttpContext http)
    {
        var session = http.Request.Headers[LanProtocol.HeaderSession].ToString();
        // A peer-supplied grouping id is a display hint and nothing else, so it is bounded rather than trusted.
        return session.Length == 0 ? null : session.Length <= 64 ? session : session[..64];
    }

    /// <summary>The modification time a peer reported, in unix milliseconds — the form a browser has on hand.</summary>
    private static DateTimeOffset? ReportedWriteUtc(HttpContext http)
    {
        var raw = http.Request.Headers[LanProtocol.HeaderMtime].ToString();
        if (raw.Length == 0) return null;
        if (long.TryParse(raw, out var milliseconds) && milliseconds is >= 0 and <= 4102444800000)
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;
        return null;
    }

    private static string RemoteAddress(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } address ? address.ToString() : "unknown";

    /// <summary>
    /// The port a peer says it is itself listening on, so a computer that paired can be dialled back. A phone
    /// reports nothing and 0 is the honest answer: inventing this machine's port would put a number in the
    /// trusted list that never belonged to the device it names.
    /// </summary>
    private static int PeerPort(HttpContext http) =>
        int.TryParse(http.Request.Headers[LanProtocol.HeaderPeerPort].ToString(), out var port)
        && port is >= LanOptions.MinimumPort and <= LanOptions.MaximumPort ? port : 0;

    /// <summary>
    /// An attachment name in both forms a client might read. The plain <c>filename</c> has to survive as ASCII
    /// because Kestrel writes headers in ISO-8859-1, so a raw Chinese name would land as mojibake; the
    /// <c>filename*</c> carries the real one for anything modern enough to ask for it.
    /// </summary>
    private static string Attachment(string name)
    {
        var fallback = new string([.. name.Select(character => character is > (char)31 and < (char)127 and not '"' and not '\\' ? character : '_')]);
        return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    /// <summary>The shape of a trusted peer row as the wire shows it, which has no field for a token.</summary>
    private static LanPeerReply PeerOf(LanPeerRecord record) =>
        new(record.DeviceId, record.DeviceName, record.Kind, record.FirstSeenUtc, record.LastSeenUtc);

    /// <summary>A callback wearing the <see cref="IProgress{T}"/> shape the sink's signature asks for.</summary>
    private sealed class LambdaProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    /// <summary>
    /// The answer once a handler has put the bytes on the response itself. Any real result would describe a
    /// body this code has already written — <c>Results.Empty</c> declares a zero length, which Kestrel then
    /// finds contradicted by the archive in its buffer and punishes with a 500.
    /// </summary>
    private sealed class Written : IResult
    {
        public static readonly Written Instance = new();

        public Task ExecuteAsync(HttpContext http) => Task.CompletedTask;
    }
}
