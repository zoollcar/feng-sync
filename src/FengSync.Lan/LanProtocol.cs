namespace FengSync.Lan;

/// <summary>
/// Every name the wire carries, in one place. A header spelled at a call site is a header nobody can find
/// again, and this protocol is read by two implementations (this process and the served phone page) that
/// cannot be compiled together.
/// </summary>
public static class LanProtocol
{
    public const int Version = 1;

    /// <summary>First token of a discovery datagram. A reply that does not start with this is not us.</summary>
    public const string DiscoveryMagic = "FENGSYNC-LAN-1";

    public const string AuthScheme = "FS-LAN";

    /// <summary>
    /// The cookie a pairing reply leaves, holding the same <c>deviceId:token</c> the header carries.
    /// </summary>
    /// <remarks>
    /// A browser cannot put a header on a download link, and the phone's one job with a link is to stream a file
    /// it may have to resume. The alternative — the token in a query string — would put a long-lived credential
    /// in an address a person can copy, share, or leave in a history, so this protocol does not do that.
    /// </remarks>
    public const string CookieName = "fs_lan";

    // Header values must be ASCII, so the paths that can hold a Chinese name travel percent-encoded.
    public const string HeaderRel = "X-FS-Rel";
    public const string HeaderSession = "X-FS-Session";
    public const string HeaderTotal = "X-FS-Total";
    public const string HeaderMtime = "X-FS-Mtime";
    public const string HeaderDeviceId = "X-FS-Device-Id";
    public const string HeaderDeviceName = "X-FS-Device-Name";
    public const string HeaderDeviceKind = "X-FS-Device-Kind";
    public const string HeaderPairCode = "X-FS-Pair-Code";

    /// <summary>
    /// The port a peer is itself listening on, so a computer can dial back. A phone reports nothing, and that
    /// absence is the answer rather than a made-up number.
    /// </summary>
    public const string HeaderPeerPort = "X-FS-Port";

    /// <summary>
    /// The room entry these bytes belong to. A write that carries it is a member delivering what it announced,
    /// so the arriving file becomes — or already is — something the other members can ask for.
    /// </summary>
    public const string HeaderMessage = "X-FS-Msg";

    /// <summary>
    /// Whether the bytes named by <see cref="HeaderMessage"/> are a folder rather than one file, which is the one
    /// thing a receiver cannot read off a single write of a tree that arrives file by file.
    /// </summary>
    public const string HeaderKind = "X-FS-Kind";

    /// <summary>The kind a peer declares for itself. Only these two are recognised; anything else is treated as unknown.</summary>
    public const string KindWindows = "windows";
    public const string KindPhone = "phone";

    /// <summary>How long one armed code stays answerable, and how often a fresh one replaces an expired one.</summary>
    public const int PairCodeLifetimeSeconds = 120;

    /// <summary>Wrong codes from one source that buy it a refusal, within <see cref="AttemptWindowSeconds"/>.</summary>
    public const int MaximumFailedAttempts = 5;
    public const int AttemptWindowSeconds = 60;

    /// <summary>
    /// Whether a peer's self-reported identifier is in a form this side will key trust on. Identifiers are
    /// generated hex — a GUID for a computer, 32 random bytes for a phone — so anything else is either a
    /// different application or somebody typing into a request body.
    /// </summary>
    public static bool IsPeerDeviceId(string? value) =>
        value is { Length: >= 8 and <= 64 }
        && value.All(character => Uri.IsHexDigit(character) || character == '-');

    /// <summary>
    /// Whether a header could be one of the room's message ids. The same shape as
    /// <see cref="IsPeerDeviceId"/> for the same reason: ids are minted here as <c>Guid.ToString("N")</c>, so a
    /// value of any other length or alphabet is a peer inventing a key rather than referring to a real entry.
    /// </summary>
    /// <remarks>
    /// This is a gate on <i>form</i> only, and it is the sink's tag and the read route's lookup that use it. What
    /// it cannot decide is whether such an id exists in this copy of the room, which is why an arrival with a
    /// well-formed but unknown id announces itself rather than being discarded, and a read for one this machine
    /// does not hold answers 没有 this side cannot hand these bytes over.
    /// </remarks>
    public static bool IsMessageId(string? value) =>
        value is { Length: >= 8 and <= 64 }
        && value.All(character => Uri.IsHexDigit(character) || character == '-');

    /// <summary>
    /// The limit a single name can carry. The composed path is bounded by the receive directory rather than
    /// by anything known here, which is why <see cref="LanReceiveSink"/> measures the real destination.
    /// </summary>
    public const int MaximumSegmentLength = 255;

    /// <summary>The longest peer name a list will show. Long enough for any real machine name, short enough to fit a row.</summary>
    public const int MaximumNameLength = 48;

    /// <summary>
    /// A name a peer chose for itself, made safe to render in a list.
    /// </summary>
    /// <remarks>
    /// It is not a filesystem name, so it is trimmed and shortened rather than refused: a phone that sends 300
    /// characters of name still has to be recognisable to the person deciding whether to accept its files. A
    /// blank claim answers blank rather than a sentence, because which sentence it becomes is the window's and
    /// this library holds no table of those strings. One owner for this rule, because a discovery row and a
    /// paired row describe the same machine and must not disagree about what it is called.
    /// </remarks>
    public static string SanitizePeerName(string? claimed)
    {
        if (string.IsNullOrWhiteSpace(claimed)) return "";
        var trimmed = claimed.Trim();
        var printable = new string([.. trimmed.Where(character => !char.IsControl(character))]);
        return printable.Length <= MaximumNameLength ? printable : printable[..MaximumNameLength];
    }
}

/// <summary>
/// Why a request was refused. Named states rather than one "no", because the window must be able to tell
/// the user to re-pair, to rename, or to check the firewall — and those are different sentences.
/// </summary>
public enum LanRefusal
{
    None = 0,

    /// <summary>No credential, or one this device has never issued. The caller's answer is to pair.</summary>
    Unpaired,

    /// <summary>The code is well formed but is not the armed one.</summary>
    BadCode,

    /// <summary>The armed code aged past <see cref="LanProtocol.PairCodeLifetimeSeconds"/>.</summary>
    CodeExpired,

    /// <summary>Too many wrong codes from this source inside the attempt window.</summary>
    RateLimited,

    /// <summary>A known device presented a token that is not the one issued to it.</summary>
    DeviceIdentityChanged,

    /// <summary>A path segment is traversal, absolute, empty, or holds a control character.</summary>
    PathUnsafe,

    /// <summary>A segment collides with a Windows reserved device name.</summary>
    NameReserved,

    /// <summary>The whole path is longer than the destination filesystem could publish.</summary>
    PathTooLong,

    /// <summary>The destination name exists and overwrite was refused.</summary>
    Conflict,

    /// <summary>No bytes this machine may hand over answer to the id the caller named.</summary>
    NotFound,

    /// <summary>
    /// A person looked at this transfer and said no. It is its own state rather than a flavour of
    /// <see cref="Conflict"/>, because "rename it and send again" and "the user does not want this device's
    /// files" are different advice, and 403 is the only code that means "understood and refused".
    /// </summary>
    Declined,

    /// <summary>The body was cut short, so nothing was published.</summary>
    IncompleteBody,

    /// <summary>
    /// The published file is not what the sender described. It is its own state rather than another name for
    /// a short body, because the honest advice differs: a short body means "send it again", a failed check
    /// after the move means something else in the directory is competing with this file.
    /// </summary>
    VerificationFailed,

    /// <summary>The listen port is taken by something else.</summary>
    PortUnavailable,

    /// <summary>
    /// A request body this side cannot use: no device id, no path, an id in a form no peer of ours would
    /// produce. Named rather than folded into <see cref="PathUnsafe"/> because the sender's fix is different —
    /// it sent a request, not a name.
    /// </summary>
    BadRequest,

    /// <summary>
    /// A sentence longer than <see cref="LanGroupLog.MaximumTextLength"/>. Its own state rather than another name
    /// for <see cref="BadRequest"/>, because the sender can act on this one — shorten it and say it again — while
    /// every other bad request means the message was built wrong.
    /// </summary>
    MessageTooLong,
}

/// <summary>The stable, machine-readable reason strings that go in a refusal body.</summary>
public static class LanReason
{
    public const string PairRequired = "pair_required";
    public const string BadCode = "bad_code";
    public const string CodeExpired = "code_expired";
    public const string RateLimited = "rate_limited";
    public const string IdentityChanged = "device_identity_changed";
    public const string PathUnsafe = "path_unsafe";
    public const string NameReserved = "name_reserved";
    public const string PathTooLong = "path_too_long";
    public const string Conflict = "name_conflict";
    public const string NotFound = "not_found";
    public const string Declined = "declined";
    public const string IncompleteBody = "incomplete_body";
    public const string VerificationFailed = "verification_failed";
    public const string BadRequest = "bad_request";
    public const string MessageTooLong = "message_too_long";

    /// <summary>The only mapping between a refusal and its wire token, so a body cannot drift from a code.</summary>
    public static string For(LanRefusal refusal) => refusal switch
    {
        LanRefusal.Unpaired => PairRequired,
        LanRefusal.BadCode => BadCode,
        LanRefusal.CodeExpired => CodeExpired,
        LanRefusal.RateLimited => RateLimited,
        LanRefusal.DeviceIdentityChanged => IdentityChanged,
        LanRefusal.PathUnsafe => PathUnsafe,
        LanRefusal.NameReserved => NameReserved,
        LanRefusal.PathTooLong => PathTooLong,
        LanRefusal.Conflict => Conflict,
        LanRefusal.NotFound => NotFound,
        LanRefusal.Declined => Declined,
        LanRefusal.IncompleteBody => IncompleteBody,
        LanRefusal.VerificationFailed => VerificationFailed,
        LanRefusal.BadRequest => BadRequest,
        LanRefusal.MessageTooLong => MessageTooLong,
        // A refusal with no token would answer an empty reason, which is the kind of silent default this
        // protocol keeps being bitten by; unpaired is the closest truthful one and it is never selected here.
        _ => PairRequired,
    };
}
