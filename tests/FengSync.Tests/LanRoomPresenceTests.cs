using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Who is in the room right now, and how that answer differs from the list of devices that have ever been
/// trusted.
/// </summary>
/// <remarks>
/// The interesting case in every one of these is an absence rather than an event. Nothing dials a phone whose
/// browser was closed, so a room that only recorded arrivals would keep showing it forever — which is exactly
/// the bug this type exists to fix — and the lease is therefore the property under test rather than a detail.
/// </remarks>
public sealed class LanRoomPresenceTests
{
    private const string Phone = "9e8d7c6b5a4938271605f4e3d2c1b0a9";
    private const string Desk = "1a2b3c4d5e6f708192a3b4c5d6e7f809";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_device_that_says_it_is_here_twice_joins_once_and_is_never_repainted()
    {
        var presence = new LanRoomPresence(TimeSpan.FromSeconds(10));
        var joined = 0;
        var updated = 0;
        presence.Joined += _ => joined++;
        presence.Updated += _ => updated++;

        presence.Seen(Sighting(Phone, "小明的手机", LanProtocol.KindPhone, "192.168.1.5", 0));
        // The same thing again, a second later: this is a lease being renewed, and a list that repainted on
        // every renewal would move under the finger of whoever was about to read it.
        presence.Seen(Sighting(Phone, "小明的手机", LanProtocol.KindPhone, "192.168.1.5", 0) with
        {
            LastSeenUtc = Now.AddSeconds(1),
        });

        Assert.Equal(1, joined);
        Assert.Equal(0, updated);
        Assert.Single(presence.Members);
    }

    [Fact]
    public void A_member_that_stops_saying_it_is_here_ages_out_once()
    {
        var presence = new LanRoomPresence(TimeSpan.FromSeconds(10));
        var left = new List<LanRoomMember>();
        presence.Left += member => left.Add(member);
        presence.Seen(Sighting(Phone, "小明的手机", LanProtocol.KindPhone, "192.168.1.5", 0));

        // Inside the lease it is still here, which is what keeps a poll that is one beat late from looking like
        // a departure.
        Assert.Single(presence.Sweep(Now.AddSeconds(9)));
        Assert.Empty(left);

        Assert.Empty(presence.Sweep(Now.AddSeconds(11)));
        Assert.Equal(Phone, Assert.Single(left).DeviceId);
        // A second sweep has nothing left to say, because the row is already gone.
        Assert.Empty(presence.Sweep(Now.AddSeconds(30)));
        Assert.Single(left);
    }

    [Fact]
    public void A_device_that_says_goodbye_is_out_before_its_lease_would_have_run_out()
    {
        var presence = new LanRoomPresence(TimeSpan.FromSeconds(10));
        var left = new List<LanRoomMember>();
        presence.Left += member => left.Add(member);
        presence.Seen(Sighting(Phone, "小明的手机", LanProtocol.KindPhone, "192.168.1.5", 0));

        Assert.True(presence.Forget(Phone));

        // A browser that closes its tab cannot be dialled, so saying goodbye is the only way this is instant —
        // the lease is the backstop for every other way a device disappears.
        Assert.Empty(presence.Members);
        Assert.Equal(Phone, Assert.Single(left).DeviceId);
        Assert.False(presence.Forget(Phone));
        Assert.Single(left);
    }

    [Fact]
    public void A_broadcast_never_downgrades_a_device_that_has_actually_spoken()
    {
        var presence = new LanRoomPresence();
        presence.Seen(Sighting(Phone, "小明的手机", LanProtocol.KindPhone, "192.168.1.5", 0));

        // Discovery carries no kind and is the weaker evidence: a device that paired and then kept announcing
        // itself must not fall back to being "merely discovered" by its own broadcast.
        presence.Seen(Sighting(Phone, "小明的手机", LanProtocol.KindUnknown, "192.168.1.5", 59871) with { Discovered = true });

        var member = Assert.Single(presence.Members);
        Assert.False(member.Discovered);
        Assert.Equal(LanProtocol.KindPhone, member.Kind);
    }

    [Fact]
    public void A_discovered_computer_that_pairs_stops_being_merely_discovered()
    {
        var presence = new LanRoomPresence();
        presence.Seen(Sighting(Desk, "客厅的台式机", LanProtocol.KindUnknown, "192.168.1.9", 59871) with { Discovered = true });
        Assert.True(Assert.Single(presence.Members).Discovered);

        presence.Seen(Sighting(Desk, "客厅的台式机", LanProtocol.KindWindows, "192.168.1.9", 59871));

        var member = Assert.Single(presence.Members);
        Assert.False(member.Discovered);
        Assert.Equal(LanProtocol.KindWindows, member.Kind);
    }

    [Fact]
    public void A_device_that_moved_is_the_same_device_at_a_new_address()
    {
        var presence = new LanRoomPresence();
        presence.Seen(Sighting(Phone, "小明的手机", LanProtocol.KindPhone, "192.168.1.5", 0));
        var renamed = 0;
        presence.Updated += _ => renamed++;

        presence.Seen(Sighting(Phone, "小明的手机", LanProtocol.KindPhone, "192.168.1.31", 0));

        // One chip, at the address it can actually be reached at: a laptop that walked between two access points
        // is not a second laptop, and the stale address would be a row that pairs and then transfers nothing.
        var member = Assert.Single(presence.Members);
        Assert.Equal("192.168.1.31", member.Address);
        Assert.Equal(1, renamed);
    }

    [Fact]
    public void Clearing_the_room_does_not_narrate_a_departure_nobody_made()
    {
        var presence = new LanRoomPresence();
        var left = 0;
        presence.Left += _ => left++;
        presence.Seen(Sighting(Phone, "小明的手机", LanProtocol.KindPhone, "192.168.1.5", 0));
        presence.Seen(Sighting(Desk, "客厅的台式机", LanProtocol.KindWindows, "192.168.1.9", 59871));

        presence.Clear();

        // The one case this is for is a listener being rebuilt, where every member is re-learned a moment later
        // from the address it is now at. A room full of departure lines would be a lie about what the user did.
        Assert.Empty(presence.Members);
        Assert.Equal(0, left);
    }

    [Fact]
    public void The_room_lists_devices_that_actually_spoke_before_the_ones_merely_heard()
    {
        var presence = new LanRoomPresence();
        presence.Seen(Sighting(Desk, "客厅的台式机", LanProtocol.KindUnknown, "192.168.1.9", 59871) with { Discovered = true });
        presence.Seen(Sighting(Phone, "小明的手机", LanProtocol.KindPhone, "192.168.1.5", 0));

        // A device this machine can talk to is worth more of the reader's attention than one that only
        // broadcast its name, so the order is part of the answer rather than an accident of arrival.
        Assert.Equal([Phone, Desk], presence.Members.Select(member => member.DeviceId));
    }

    private static LanRoomMember Sighting(string deviceId, string name, string kind, string address, int port) =>
        new(deviceId, name, kind, address, port, Now, Discovered: false);
}
