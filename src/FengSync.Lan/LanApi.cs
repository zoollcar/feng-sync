using System.Text.Json;
using System.Text.Json.Serialization;

namespace FengSync.Lan;

/// <summary>
/// The shapes the LAN wire carries, and the one mapping from a refusal to an HTTP answer.
/// </summary>
/// <remarks>
/// Two implementations read this protocol — this process and the page served to a phone — and they cannot be
/// compiled together, so every field name in here is a compatibility promise. They are declared once, in
/// camel case, and the status table below is the only place a refusal becomes a number.
/// </remarks>
public static class LanApi
{
    /// <summary>The version a peer must see in a hello reply to accept this listener as one of ours.</summary>
    public const int WireVersion = LanProtocol.Version;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Turns a refusal into the status and reason body the peer gets.
    /// </summary>
    /// <remarks>
    /// The reason token is always sent: a phone page that reads only the number can still show "请先配对", and a
    /// body that says nothing turns a refusal into a mystery the user cannot act on.
    /// </remarks>
    public static (int StatusCode, string Error) AnswerFor(LanRefusal refusal) => refusal switch
    {
        // A stranger with no credential is asked to pair, not forbidden: there is nothing it is not allowed to
        // do yet, it simply has not introduced itself.
        LanRefusal.Unpaired => (401, LanReason.PairRequired),
        LanRefusal.BadCode => (401, LanReason.BadCode),
        LanRefusal.CodeExpired => (401, LanReason.CodeExpired),
        LanRefusal.DeviceIdentityChanged => (401, LanReason.IdentityChanged),

        // Its own code because the advice is the opposite of every other refusal here: wait, and do not retry
        // with the same guess.
        LanRefusal.RateLimited => (429, LanReason.RateLimited),

        LanRefusal.Declined => (403, LanReason.Declined),

        LanRefusal.PathUnsafe => (400, LanReason.PathUnsafe),
        LanRefusal.NameReserved => (400, LanReason.NameReserved),
        LanRefusal.IncompleteBody => (400, LanReason.IncompleteBody),

        // Its own code because a page that got this answer has something to do about it — shorten the sentence —
        // while every other 400 here means the request was built wrong. 413 is what the number actually means.
        LanRefusal.MessageTooLong => (413, LanReason.MessageTooLong),

        LanRefusal.PathTooLong => (414, LanReason.PathTooLong),
        LanRefusal.Conflict => (409, LanReason.Conflict),
        LanRefusal.NotFound => (404, LanReason.NotFound),
        LanRefusal.BadRequest => (400, LanReason.BadRequest),

        // The only one that is this machine's fault: the bytes were accepted, published, and then disagreed
        // with what the sender described, so a retry by the peer is not obviously the answer.
        LanRefusal.VerificationFailed => (500, LanReason.VerificationFailed),

        // A refusal nobody mapped would answer with somebody else's reason, which is how a protocol starts
        // lying. Every state above is named, so reaching this line means the enum grew and this table did not.
        LanRefusal.PortUnavailable => throw new InvalidOperationException("端口不可用不是线上拒绝，它是启动失败。"),
        _ => throw new InvalidOperationException($"未映射的拒绝状态：{refusal}"),
    };

    /// <summary>Refusals that mean "this peer has not introduced itself properly" and therefore belong to pairing.</summary>
    public static bool IsPairingFailure(LanRefusal refusal) => refusal
        is LanRefusal.Unpaired or LanRefusal.BadCode or LanRefusal.CodeExpired
        or LanRefusal.RateLimited or LanRefusal.DeviceIdentityChanged;

    /// <summary>
    /// Reads a refusal out of a reason token, for the calling half of a transfer. The token is the answer a
    /// peer gives; the status code alone cannot tell 配对码过期 from 配对码不对, and those are different things
    /// for a person to read on screen.
    /// </summary>
    /// <returns><see langword="null"/> for a token this protocol never wrote, which the caller then judges
    /// from the status code rather than inventing a named state for.</returns>
    public static LanRefusal? RefusalFor(string? reason) => reason switch
    {
        LanReason.PairRequired => LanRefusal.Unpaired,
        LanReason.BadCode => LanRefusal.BadCode,
        LanReason.CodeExpired => LanRefusal.CodeExpired,
        LanReason.RateLimited => LanRefusal.RateLimited,
        LanReason.IdentityChanged => LanRefusal.DeviceIdentityChanged,
        LanReason.PathUnsafe => LanRefusal.PathUnsafe,
        LanReason.NameReserved => LanRefusal.NameReserved,
        LanReason.PathTooLong => LanRefusal.PathTooLong,
        LanReason.Conflict => LanRefusal.Conflict,
        LanReason.NotFound => LanRefusal.NotFound,
        LanReason.Declined => LanRefusal.Declined,
        LanReason.IncompleteBody => LanRefusal.IncompleteBody,
        LanReason.VerificationFailed => LanRefusal.VerificationFailed,
        LanReason.BadRequest => LanRefusal.BadRequest,
        LanReason.MessageTooLong => LanRefusal.MessageTooLong,
        _ => null,
    };

    /// <summary>
    /// The fallback when a refusal carries no token this side recognises — a proxy answering instead of a Feng
    /// Sync, or a build that renamed a reason. It keys on the number rather than guessing a reason a peer
    /// never sent, and a server-side failure with no token is filed under 对端没有接受这次请求 rather than under
    /// a named cause we did not observe: "not accepted" is true of every one of those answers, while inventing
    /// a specific one would let the window give advice nobody has evidence for.
    /// </summary>
    public static LanRefusal FromStatusCode(int statusCode) => statusCode switch
    {
        401 => LanRefusal.Unpaired,
        403 => LanRefusal.Declined,
        404 => LanRefusal.NotFound,
        409 => LanRefusal.Conflict,
        413 => LanRefusal.MessageTooLong,
        414 => LanRefusal.PathTooLong,
        429 => LanRefusal.RateLimited,
        _ => LanRefusal.BadRequest,
    };
}

/// <summary>The answer to "is there a Feng Sync listening here?" — the only body a stranger can read.</summary>
public sealed record LanHelloReply(
    int V,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    string DeviceName,
    string Kind,
    bool PairingArmed);

/// <summary>What a peer sends to trade a one-time code for a long-lived token.</summary>
public sealed record LanPairRequest(
    string Code,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    string DeviceName,
    string Kind);

/// <summary>
/// The credential, and the row this machine just wrote for the device that asked. <c>Peer</c> is the caller's
/// own record seen from here, which is the only one it is allowed to read.
/// </summary>
public sealed record LanPairReply(string Token, int V, LanPeerReply Peer);

/// <summary>A device row, in the form the wire shows. It has no field that could carry a token.</summary>
public sealed record LanPeerReply(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    string DeviceName,
    string Kind,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);

/// <summary>What the receive sink decided, narrowed to what the sender can act on.</summary>
public sealed record LanInboxReply(string SavedAs, long Bytes, bool Renamed, string RelativePath);

/// <summary>A directory the peer asked this machine to create.</summary>
public sealed record LanMkdirReply(string RelativePath);

/// <summary>
/// One thing said in the room. <see cref="LocalSeq"/> is the answering machine's own arrival number and
/// <see cref="HeldHere"/> is a statement about that machine, so a reader keys its watermark on the pair
/// <c>(peer, LocalSeq)</c> and never on this field alone — there is no member that numbers the room.
/// </summary>
public sealed record LanMessageWire(
    string MessageId,
    string AuthorDeviceId,
    string AuthorName,
    long LocalSeq,
    string Kind,
    string? Text,
    string? FileName,
    string? RelativePath,
    long Bytes,
    bool IsDirectory,
    DateTimeOffset CreatedUtc,
    bool HeldHere);

/// <summary>
/// What one machine's copy of the room holds past a number its caller named. <see cref="Head"/> is where this
/// copy is now, which is what lets a poller record its watermark even when nothing new came across.
/// </summary>
public sealed record LanLogReply(int V, long Head, IReadOnlyList<LanMessageWire> Entries);

/// <summary>
/// A sentence a peer is asking this machine to remember and repeat.
/// </summary>
/// <param name="MessageId">
/// The id the speaker minted, so one sentence carries one identity into every copy of the room. Absent means the
/// caller has none, and this machine mints one for its own copy — which is why a fan-out is asked to name the id
/// rather than let each peer invent its own: two members told the same sentence with two different ids would
/// converge as two rows, and a room that shows a person their own sentence twice reads as damage.
/// </param>
public sealed record LanSayRequest(string? Text, string? MessageId = null);

/// <summary>
/// The name a peer now wants to be listed under. It carries no device id, because the id it renames is the one
/// its credential already proves and nothing a body claims may point at another member's row.
/// </summary>
public sealed record LanNameRequest(string? DeviceName);

/// <summary>The name this machine kept, which is the peer's answer when the claimed one was too long to store.</summary>
public sealed record LanNameReply(string DeviceName);

/// <summary>The only body shape a refusal ever takes.</summary>
public sealed record LanErrorReply(string Error, string Reason);
