namespace FengSync.Lan;

/// <summary>
/// What a device this machine knows about is, from the room's point of view.
/// </summary>
/// <remarks>
/// The distinction this enum exists to make is the one a person could not previously see: a computer heard
/// broadcasting its name on the subnet has <b>not</b> joined anything. It has proved nothing, holds no credential
/// this listener would accept, and is never dialled — so it belongs in a list of invitations, not in a list of
/// members. <see cref="Member"/> is the only state that means "this device and this machine can talk".
/// </remarks>
public enum LanDeviceState
{
    /// <summary>In this machine's peer list with a credential behind it: reachable, and in the room.</summary>
    Member,

    /// <summary>Known, but nothing has been agreed yet — the row an invitation belongs on.</summary>
    Invitable,

    /// <summary>An invitation has been sent and nobody has answered it.</summary>
    Invited,

    /// <summary>
    /// In the peer list but with no credential this machine can call with: a pairing that only went one way,
    /// which is what every pairing did before the grant travelled in the request. It is not a member in any
    /// useful sense — every poll of it answers 请先配对 — so it is offered back as something to finish.
    /// </summary>
    Incomplete,
}

/// <summary>
/// One device, as the window should draw it.
/// </summary>
/// <param name="Address">Where it answered from, or empty for a device that answers nowhere of its own.</param>
/// <param name="Port">The port it listens on, or 0 when it has none — a phone browser has no listener.</param>
/// <param name="Online">
/// Whether it is in the room <i>right now</i>, which is a lease rather than a record: a member that stopped
/// saying it was here has aged out of <see cref="LanRoomPresence"/> and is still a member.
/// </param>
/// <param name="GatewayDeviceId">
/// For a device with no listener, the member it talks through. The name is the window's to resolve, because a
/// library that held a table of display strings would be a library holding half the interface.
/// </param>
/// <param name="CanInvite">
/// Whether an invitation can be sent for this row at all — which is a different question from
/// <see cref="IsDialable"/> and cannot be answered by the row alone. A device another member relays carries no
/// address and no port on purpose, because that is what "it answers nowhere of its own" means on the wire, so the
/// only way to know whether it can still be asked is to look at the roster row its gateway names and see whether
/// <i>that</i> one has a listener somebody can dial. This is therefore a judgement <see cref="LanDeviceRoster.Merge"/>
/// settles once the whole table is built, not a rule each call site re-derives: the chip that decides whether to
/// draw 邀请 and <c>InviteAsync</c>, which decides where to send it, must never disagree about one row, and a
/// button that leads to a refusal is the same defect as a missing button wearing a different coat. A row built
/// anywhere else says that nobody checked, which is why the default is the refusal.
/// </param>
public sealed record LanRosterEntry(
    string DeviceId,
    string DeviceName,
    string Kind,
    string Address,
    int Port,
    LanDeviceState State,
    bool Online,
    string? GatewayDeviceId,
    DateTimeOffset LastSeenUtc,
    bool CanInvite = false)
{
    /// <summary>
    /// Whether this machine could open a socket to it, which is what separates a peer from a row. Exactly that and
    /// nothing more: a device with no listener of its own is still askable through the member that relays it, and
    /// <see cref="CanInvite"/> is the answer to that question.
    /// </summary>
    public bool IsDialable => Port is >= LanOptions.MinimumPort and <= LanOptions.MaximumPort;

    /// <summary>Where to write to it, or just the address when it has no port to name.</summary>
    public string Where => IsDialable ? $"{Address}:{Port}" : Address;
}

/// <summary>
/// The one place the three answers about a device are put together: who this machine has agreed with, who it can
/// hear, and who its members have told it about.
/// </summary>
/// <remarks>
/// <para>
/// A pure function over snapshots rather than a store of its own. The room already has three sources of truth —
/// <see cref="LanPeerStore"/> for agreements, <see cref="LanRoomPresence"/> for liveness, <see cref="LanDiscovery"/>
/// for announcements — and a fourth that remembered devices would be a fourth thing to keep in step. Everything
/// here is decided fresh from what the caller can see, so the window redraws the same list from the same facts
/// however often it asks.
/// </para>
/// <para>
/// Precedence is deliberate and one-directional. An agreement outranks a broadcast, because a device this machine
/// has paired with is the same device even when its announcement is the only thing arriving; a broadcast outranks
/// a relayed member list, because a machine that answers for itself is a better witness than a member reporting
/// it. Nothing is ever downgraded by weaker evidence.
/// </para>
/// <para>
/// What comes back also carries the whole answer about asking. Whether an invitation can reach a row is settled at
/// the end, once all three sources have been written, because the question is about a different row — the gateway a
/// relayed device names — and a caller cannot be left to look that up for itself.
/// </para>
/// </remarks>
public static class LanDeviceRoster
{
    /// <param name="credentialed">
    /// The devices this machine holds a credential <b>for</b> — the ones it can actually call. A peer row without
    /// one is <see cref="LanDeviceState.Incomplete"/>, and that is the whole reason this is an input rather than
    /// something the merge could infer: the peer list records who may talk to this machine, not who it can reach.
    /// </param>
    /// <param name="invited">Devices an invitation has already been sent to, so a row can say so.</param>
    public static IReadOnlyList<LanRosterEntry> Merge(
        string selfDeviceId,
        IReadOnlyList<LanPeerRecord> peers,
        IReadOnlyList<LanRoomMember> present,
        IReadOnlyList<LanDiscoveredPeer> discovered,
        IReadOnlyList<LanMemberWire> relayed,
        IReadOnlySet<string> credentialed,
        IReadOnlySet<string>? invited = null)
    {
        var entries = new Dictionary<string, LanRosterEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in peers)
        {
            if (Same(row.DeviceId, selfDeviceId)) continue;
            var online = present.FirstOrDefault(member => Same(member.DeviceId, row.DeviceId));
            var state = row.Kind == LanProtocol.KindWindows && !credentialed.Contains(row.DeviceId)
                ? LanDeviceState.Incomplete
                : LanDeviceState.Member;

            entries[row.DeviceId] = new LanRosterEntry(
                row.DeviceId,
                Name(row.DeviceName, online?.DeviceName),
                Kind(row.Kind, online?.Kind),
                row.SeenAddresses.Count > 0 ? row.SeenAddresses[^1] : online?.Address ?? "",
                row.Port,
                state,
                online is not null,
                row.GatewayDeviceId,
                online?.LastSeenUtc ?? row.LastSeenUtc);
        }

        foreach (var peer in discovered)
        {
            if (Same(peer.DeviceId, selfDeviceId) || entries.ContainsKey(peer.DeviceId)) continue;
            entries[peer.DeviceId] = new LanRosterEntry(peer.DeviceId, peer.DeviceName, LanProtocol.KindUnknown,
                peer.Address, peer.Port, LanDeviceState.Invitable, Online: false, GatewayDeviceId: null,
                DateTimeOffset.UtcNow);
        }

        foreach (var member in relayed)
        {
            if (Same(member.DeviceId, selfDeviceId) || entries.ContainsKey(member.DeviceId)) continue;
            entries[member.DeviceId] = new LanRosterEntry(member.DeviceId, LanProtocol.SanitizePeerName(member.DeviceName),
                Kind(member.Kind, null), "", 0, LanDeviceState.Invitable, Online: false, member.GatewayDeviceId,
                member.LastSeenUtc);
        }

        // Whether a row can be asked is settled here rather than inside the loops that wrote it, because none of
        // them can see the whole table yet. A peer row's gateway may name a device that only comes later in the
        // peer list, or one that arrives as a broadcast or as another member's report; and a relayed row has
        // nothing of its own to test, its address and port being empty by design. The rule is the window's own:
        // reachable directly, or reachable through a row this roster holds that is itself dialable.
        foreach (var entry in entries.Values.ToArray())
        {
            var canInvite = entry.IsDialable
                || entry.GatewayDeviceId is { Length: > 0 } gateway
                && entries.TryGetValue(gateway, out var host) && host.IsDialable;
            if (canInvite != entry.CanInvite) entries[entry.DeviceId] = entry with { CanInvite = canInvite };
        }

        if (invited is not null)
            foreach (var id in invited)
                if (entries.TryGetValue(id, out var pending) && pending.State == LanDeviceState.Invitable)
                    entries[id] = pending with { State = LanDeviceState.Invited };

        return [.. entries.Values
            .OrderBy(entry => entry.State == LanDeviceState.Member ? 0 : 1)
            .ThenBy(entry => entry.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.DeviceId, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>A name this machine has been told, in the order it trusts the telling.</summary>
    private static string Name(string? agreed, string? heard) =>
        !string.IsNullOrWhiteSpace(agreed) ? agreed! : LanProtocol.SanitizePeerName(heard);

    /// <summary>What kind of thing it is, with a broadcast's silence not overriding what a person was told.</summary>
    private static string Kind(string? first, string? second) =>
        !string.IsNullOrEmpty(first) && first != LanProtocol.KindUnknown ? first!
        : !string.IsNullOrEmpty(second) && second != LanProtocol.KindUnknown ? second!
        : LanProtocol.KindUnknown;
}
