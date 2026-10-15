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

    // Header values must be ASCII, so the paths that can hold a Chinese name travel percent-encoded.
    public const string HeaderRel = "X-FS-Rel";
    public const string HeaderSession = "X-FS-Session";
    public const string HeaderTotal = "X-FS-Total";
    public const string HeaderMtime = "X-FS-Mtime";
    public const string HeaderDeviceId = "X-FS-Device-Id";
    public const string HeaderDeviceName = "X-FS-Device-Name";
    public const string HeaderDeviceKind = "X-FS-Device-Kind";
    public const string HeaderPairCode = "X-FS-Pair-Code";

    /// <summary>The kind a peer declares for itself. Only these two are recognised; anything else is treated as unknown.</summary>
    public const string KindWindows = "windows";
    public const string KindPhone = "phone";

    /// <summary>How long one armed code stays answerable, and how often a fresh one replaces an expired one.</summary>
    public const int PairCodeLifetimeSeconds = 120;

    /// <summary>Wrong codes from one source that buy it a refusal, within <see cref="AttemptWindowSeconds"/>.</summary>
    public const int MaximumFailedAttempts = 5;
    public const int AttemptWindowSeconds = 60;

    /// <summary>
    /// The limit a single name can carry. The composed path is bounded by the receive directory rather than
    /// by anything known here, which is why <see cref="LanReceiveSink"/> measures the real destination.
    /// </summary>
    public const int MaximumSegmentLength = 255;
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

    /// <summary>The named basket item is not in this session's basket.</summary>
    NotFound,

    /// <summary>The body was cut short, so nothing was published.</summary>
    IncompleteBody,

    /// <summary>The listen port is taken by something else.</summary>
    PortUnavailable,
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
    public const string IncompleteBody = "incomplete_body";

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
        LanRefusal.IncompleteBody => IncompleteBody,
        _ => "unpaired",
    };
}
