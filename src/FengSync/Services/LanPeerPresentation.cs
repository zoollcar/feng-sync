using FengSync.Core.Configuration;
using FengSync.Lan;
using FengSync.Localization;

namespace FengSync.Services;

/// <summary>
/// Turns the service's named outcomes into the sentences a person reads.
/// </summary>
/// <remarks>
/// One table per outcome rather than a string at each call site, because the same refusal appears in the status
/// line, in a transfer row, and in a send failure — and a refusal that says one thing in one of those three is
/// the kind of drift nobody notices until somebody is advised to rename a file that was never refused for its
/// name. Every state in the enums has its own line and its own key, which is also what keeps a new state from
/// being folded silently into somebody else's advice.
/// </remarks>
internal static class LanPeerPresentation
{
    /// <summary>Why a request did not go through, in the form that tells the user what to do about it.</summary>
    public static string Refusal(LanRefusal refusal) => refusal switch
    {
        LanRefusal.None => Tr.F("Lan.Refuse.None"),
        LanRefusal.Unpaired => Tr.F("Lan.Refuse.Unpaired"),
        LanRefusal.BadCode => Tr.F("Lan.Refuse.BadCode"),
        LanRefusal.CodeExpired => Tr.F("Lan.Refuse.CodeExpired"),
        LanRefusal.RateLimited => Tr.F("Lan.Refuse.RateLimited"),
        LanRefusal.DeviceIdentityChanged => Tr.F("Lan.Refuse.IdentityChanged"),
        LanRefusal.PathUnsafe => Tr.F("Lan.Refuse.PathUnsafe"),
        LanRefusal.NameReserved => Tr.F("Lan.Refuse.NameReserved"),
        LanRefusal.PathTooLong => Tr.F("Lan.Refuse.PathTooLong"),
        LanRefusal.Conflict => Tr.F("Lan.Refuse.Conflict"),
        LanRefusal.NotFound => Tr.F("Lan.Refuse.NotFound"),
        LanRefusal.Declined => Tr.F("Lan.Refuse.Declined"),
        LanRefusal.IncompleteBody => Tr.F("Lan.Refuse.IncompleteBody"),
        LanRefusal.VerificationFailed => Tr.F("Lan.Refuse.VerificationFailed"),
        LanRefusal.PortUnavailable => Tr.F("Lan.Refuse.PortUnavailable"),
        LanRefusal.BadRequest => Tr.F("Lan.Refuse.BadRequest"),
        LanRefusal.MessageTooLong => Tr.F("Lan.Refuse.MessageTooLong"),
        _ => Tr.F("Lan.Refuse.BadRequest"),
    };

    /// <summary>What the firewall button did, including the answer where the user pressed 取消.</summary>
    public static string Firewall(LanFirewallOutcome outcome) => outcome switch
    {
        LanFirewallOutcome.AlreadyAllowed => Tr.F("Lan.Firewall.Allowed"),
        LanFirewallOutcome.Added => Tr.F("Lan.Firewall.Added"),
        LanFirewallOutcome.DeclinedByUser => Tr.F("Lan.Firewall.Declined"),
        LanFirewallOutcome.FailedElevated => Tr.F("Lan.Firewall.FailedElevated"),
        LanFirewallOutcome.FailedUnknown => Tr.F("Lan.Firewall.FailedUnknown"),
        _ => Tr.F("Lan.Firewall.FailedUnknown"),
    };

    /// <summary>What a row in the device list is, said in the way a person would say it.</summary>
    public static string Kind(string? kind) => kind switch
    {
        LanProtocol.KindWindows => Tr.F("Lan.KindComputer"),
        LanProtocol.KindPhone => Tr.F("Lan.KindPhone"),
        _ => Tr.F("Lan.KindUnknown"),
    };

    public static string State(LanTransferState state) => state switch
    {
        LanTransferState.Completed => Tr.F("Lan.State.Completed"),
        LanTransferState.Failed => Tr.F("Lan.State.Failed"),
        LanTransferState.Cancelled => Tr.F("Lan.State.Cancelled"),
        LanTransferState.Declined => Tr.F("Lan.State.Declined"),
        _ => Tr.F("Lan.State.Failed"),
    };

    /// <summary>Which way the bytes went, from this machine's point of view.</summary>
    public static string Direction(LanTransferDirection direction) => direction switch
    {
        LanTransferDirection.Received => Tr.F("Lan.Direction.Received"),
        LanTransferDirection.Sent => Tr.F("Lan.Direction.Sent"),
        _ => Tr.F("Lan.Direction.Received"),
    };

    /// <summary>
    /// A row's subject, which is the one part of a transfer line that must never read as a success: an outcome
    /// alone ("已完成") says nothing about what happened, and a diagnostic with no subject cannot be attached to
    /// anything.
    /// </summary>
    public static string Summary(LanTransferDirection direction, string deviceName, string path, long bytes, LanTransferState state, string? error)
    {
        var who = string.IsNullOrWhiteSpace(deviceName) ? Tr.F("Lan.KindUnknown") : deviceName;
        var line = Tr.Format("Lan.Row.Summary", Direction(direction), who, path, ByteSize.Format(bytes));
        return string.IsNullOrWhiteSpace(error) ? $"{line} · {State(state)}" : $"{line} · {State(state)} · {error}";
    }
}
