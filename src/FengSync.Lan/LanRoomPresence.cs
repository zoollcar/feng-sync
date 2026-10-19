namespace FengSync.Lan;

/// <summary>
/// One device the room can see right now.
/// </summary>
/// <param name="DeviceId">The device's own stable identifier, which is what makes two sightings the same device.</param>
/// <param name="Kind">
/// <see cref="LanProtocol.KindWindows"/>, <see cref="LanProtocol.KindPhone"/>, or
/// <see cref="LanProtocol.KindUnknown"/> for a device heard on UDP and never spoken to.
/// </param>
/// <param name="Address">Where it was last seen, as the peer's own traffic or its announcement reported it.</param>
/// <param name="Port">
/// The port it listens on, or 0 when it has none. A phone browser has no listener, and 0 is the honest answer —
/// a made-up number would put an address in a list that nothing could be dialled at.
/// </param>
/// <param name="Discovered">
/// Whether this device is only known through its discovery announcement. A discovered computer is worth showing
/// and worth pairing with, but it has proved nothing and is therefore not a member in the sense the room's own
/// readers mean.
/// </param>
public sealed record LanRoomMember(
    string DeviceId,
    string DeviceName,
    string Kind,
    string Address,
    int Port,
    DateTimeOffset LastSeenUtc,
    bool Discovered)
{
    /// <summary>Whether this machine could dial it back, which is what separates a peer from a row.</summary>
    public bool IsDialable => Port is >= LanOptions.MinimumPort and <= LanOptions.MaximumPort;

    /// <summary>Where to write to it, or just the address when it has no port to name.</summary>
    public string Where => IsDialable ? $"{Address}:{Port}" : Address;
}

/// <summary>
/// Who is in the room <i>right now</i>, as opposed to who has ever been trusted.
/// </summary>
/// <remarks>
/// <para>
/// The two questions are genuinely different and this feature needs both answers kept apart.
/// <see cref="LanPeerStore"/> is a file that outlives every window: it says which devices may talk to this
/// listener again without a fresh code, and a device in it stays there until somebody forgets it. Presence is
/// memory, and it says who is actually here — a phone whose browser was closed, a computer whose window was
/// shut, and a machine that merely broadcast its name on the subnet are three different states, and only the
/// first two can be "in the room".
/// </para>
/// <para>
/// It is a lease rather than an event, because the interesting fact is an absence. A member that is in the
/// room keeps saying so — every authenticated request refreshes it, and so does every successful poll of it —
/// and a member that stops saying so ages out of <see cref="TimeToLive"/> and is announced as having left.
/// Nothing has to be sent for that to happen, which is the point: a browser that is closed cannot send a
/// goodbye, and a machine that was unplugged certainly cannot.
/// </para>
/// <para>
/// Every event is raised off the lock, so a handler that repaints a list can do it on whichever thread spoke
/// without this class holding a gate across somebody else's work.
/// </para>
/// </remarks>
public sealed class LanRoomPresence(TimeSpan? timeToLive = null)
{
    /// <summary>
    /// How long a member stays in the room without saying anything.
    /// </summary>
    /// <remarks>
    /// Comfortably more than the room's three-second poll beat, because a lease that expired between two beats
    /// would make a perfectly reachable member flicker in and out of the list — and a device list that blinks is
    /// worse than one that is ten seconds slow to notice a departure.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, LanRoomMember> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _timeToLive = timeToLive ?? DefaultTimeToLive;

    /// <summary>A device that was not in the room and now is.</summary>
    public event Action<LanRoomMember>? Joined;

    /// <summary>A device already in the room whose name, kind, or address changed.</summary>
    public event Action<LanRoomMember>? Updated;

    /// <summary>A device that stopped saying it was here, or said goodbye.</summary>
    public event Action<LanRoomMember>? Left;

    /// <summary>The lease a member is held under, for a window that has to explain why a row disappeared.</summary>
    public TimeSpan TimeToLive => _timeToLive;

    /// <summary>Everyone currently in the room, by name so the list a person reads does not reshuffle.</summary>
    public IReadOnlyList<LanRoomMember> Members
    {
        get
        {
            lock (_gate)
                return [.. _members.Values
                    .OrderBy(member => member.Discovered)
                    .ThenBy(member => member.DeviceName, StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    /// <summary>
    /// Records that a device is here now, and says whether that was news.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the room gained a device or learned something new about one, which is what a
    /// caller that narrates changes keys on. A sighting that says exactly what the last one did is a lease being
    /// renewed and raises nothing: an announcement repeats every two seconds, and a list that repainted on every
    /// repeat would move under the finger of whoever was about to click it.
    /// </returns>
    /// <remarks>
    /// The merge is deliberately one-directional in two places. A device that has ever spoken to this machine is
    /// not downgraded to "merely discovered" by a later broadcast, because a broadcast is the weaker evidence and
    /// the stronger one does not expire into it. And a kind that is known is not replaced by
    /// <see cref="LanProtocol.KindUnknown"/>, because discovery cannot carry a kind at all and would otherwise
    /// erase what a phone's own pairing said about itself.
    /// </remarks>
    public bool Seen(LanRoomMember sighting)
    {
        if (sighting.DeviceId.Length == 0) return false;
        LanRoomMember merged;
        var joined = false;
        var changed = false;
        lock (_gate)
        {
            if (_members.TryGetValue(sighting.DeviceId, out var known))
            {
                merged = sighting with
                {
                    Discovered = known.Discovered && sighting.Discovered,
                    Kind = string.Equals(sighting.Kind, LanProtocol.KindUnknown, StringComparison.Ordinal) ? known.Kind : sighting.Kind,
                    DeviceName = string.IsNullOrWhiteSpace(sighting.DeviceName) ? known.DeviceName : sighting.DeviceName,
                };
                changed = !Same(known, merged);
                _members[sighting.DeviceId] = merged;
            }
            else
            {
                merged = sighting;
                _members[sighting.DeviceId] = merged;
                joined = true;
            }
        }

        if (joined) Joined?.Invoke(merged);
        else if (changed) Updated?.Invoke(merged);
        return joined || changed;
    }

    /// <summary>
    /// Takes a device out of the room because it said it was leaving.
    /// </summary>
    /// <returns><see langword="false"/> for a device that was not in the room, which is not an error — a page
    /// that reloads says goodbye twice and the second one has nothing to remove.</returns>
    public bool Forget(string deviceId)
    {
        LanRoomMember gone;
        lock (_gate)
        {
            if (!_members.Remove(deviceId ?? "", out var found)) return false;
            gone = found;
        }

        Left?.Invoke(gone);
        return true;
    }

    /// <summary>
    /// Drops everyone whose lease has run out, and returns who is left.
    /// </summary>
    /// <remarks>
    /// Driven by a caller's clock rather than by a timer of its own, for the same reason
    /// <see cref="LanPairingState"/> is: a window already ticks once a second to redraw a countdown, and a
    /// second timer inside a library is one more thing to start, stop, and leak on close.
    /// </remarks>
    public IReadOnlyList<LanRoomMember> Sweep(DateTimeOffset now)
    {
        List<LanRoomMember> gone;
        lock (_gate)
        {
            gone = [.. _members.Values.Where(member => now - member.LastSeenUtc > _timeToLive)];
            foreach (var member in gone) _members.Remove(member.DeviceId);
        }

        foreach (var member in gone) Left?.Invoke(member);
        return Members;
    }

    /// <summary>
    /// Empties the room without saying anybody left.
    /// </summary>
    /// <remarks>
    /// For the one case where every member is about to be re-learned anyway: the listener is being rebuilt, so
    /// the addresses in here point at a port that no longer answers. Narrating that as a room full of
    /// departures would be a lie about what the user did, and the next sighting of each device is a second away.
    /// </remarks>
    public void Clear()
    {
        lock (_gate) _members.Clear();
    }

    private static bool Same(LanRoomMember left, LanRoomMember right) =>
        string.Equals(left.DeviceName, right.DeviceName, StringComparison.Ordinal)
        && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
        && string.Equals(left.Address, right.Address, StringComparison.Ordinal)
        && left.Port == right.Port
        && left.Discovered == right.Discovered;
}
