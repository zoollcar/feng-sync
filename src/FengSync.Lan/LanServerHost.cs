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
        endpoints.MapPost("/api/v1/inbox", (Func<HttpContext, Task<IResult>>)HandleInboxAsync);
        endpoints.MapPost("/api/v1/mkdir", (Func<HttpContext, Task<IResult>>)HandleMkdirAsync);

        // The room, in both directions: what this copy has heard past a number the caller named, and a sentence
        // somebody is adding to it.
        endpoints.MapGet("/api/v1/log", (Func<HttpContext, Task<IResult>>)HandleLogAsync);
        endpoints.MapPost("/api/v1/messages", (Func<HttpContext, Task<IResult>>)HandleSayAsync);
        endpoints.MapGet("/api/v1/basket", (Func<HttpContext, Task<IResult>>)HandleBasketAsync);
        endpoints.MapGet("/api/v1/basket/{id}", (Func<string, HttpContext, Task<IResult>>)HandleDownloadAsync);
        endpoints.MapGet("/api/v1/basket/{id}/zip", (Func<string, HttpContext, Task<IResult>>)HandleArchiveAsync);

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
        return Json(new LanMkdirReply(relative), StatusCodes.Status201Created);
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

        var refusal = _options.Session.Group.TryAppendText(auth.Contact!, request?.Text, DateTimeOffset.UtcNow, out var entry);
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

        var (item, error) = Find(id);
        if (error is not null) return error;
        if (item!.IsDirectory)
            // A folder has no single-length answer: it is served as an archive, and a client that asked for the
            // bare item would otherwise receive a file that is not the folder it named.
            return Answer(LanRefusal.BadRequest);
        if (!File.Exists(item.AbsolutePath)) return Answer(LanRefusal.NotFound);

        OutboundDelivered?.Invoke(new LanOutboundEventArgs(auth.Contact!, item, IsArchive: false));
        // Range support is the whole point of this route: a phone that drops a large download can come back for
        // the tail instead of starting over, which is the property a zip cannot offer.
        // The name is written here rather than handed to Results.File, because that call would answer a single
        // ASCII filename and a Chinese download name is exactly what the peer needs intact.
        http.Response.Headers.ContentDisposition = Attachment(item.Name);
        return Results.File(item.AbsolutePath, "application/octet-stream", fileDownloadName: null, enableRangeProcessing: true);
    }

    private async Task<IResult> HandleArchiveAsync(string id, HttpContext http)
    {
        var auth = await AuthorizeAsync(http).ConfigureAwait(false);
        if (auth.Refusal != LanRefusal.None) return Answer(auth.Refusal);

        var (item, error) = Find(id);
        if (error is not null) return error;
        if (!item!.IsDirectory) return Answer(LanRefusal.BadRequest);
        if (!Directory.Exists(item.AbsolutePath)) return Answer(LanRefusal.NotFound);

        OutboundDelivered?.Invoke(new LanOutboundEventArgs(auth.Contact!, item, IsArchive: true));
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

    private (LanSendItem? Item, IResult? Error) Find(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return (null, Answer(LanRefusal.NotFound));
        var item = _options.Session.Basket.Snapshot().FirstOrDefault(candidate => candidate.Id == guid);
        return item is null ? (null, Answer(LanRefusal.NotFound)) : (item, null);
    }

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
