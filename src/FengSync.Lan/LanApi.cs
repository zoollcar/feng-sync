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

/// <summary>One row of the send basket, as a phone lists it.</summary>
public sealed record LanBasketReply(string Id, string Name, long Bytes, bool IsDirectory, DateTimeOffset? ModifiedUtc);

/// <summary>The only body shape a refusal ever takes.</summary>
public sealed record LanErrorReply(string Error, string Reason);
