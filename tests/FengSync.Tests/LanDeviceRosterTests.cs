using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// The rules that decide which list a device belongs on. These are the rules a person reads off the screen —
/// 群里的设备 is a promise that the room can be talked to, 可邀请设备 is a promise that it cannot yet — so every
/// case here is about a row that would otherwise be filed under the wrong promise. The merge decides a third thing
/// as well: whether a row can be sent an invitation at all, which is the only answer a chip has about the button
/// it draws, and a row that could be asked through a member but is not marked as such is a capability with no way
/// in.
/// </summary>
public sealed class LanDeviceRosterTests
{
    private const string Self = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Computer = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Legacy = "cccccccccccccccccccccccccccccccc";
    private const string Phone = "dddddddddddddddddddddddddddddddd";
    private const string Stranger = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_computer_heard_on_the_subnet_is_invitable_and_never_a_member()
    {
        var rows = LanDeviceRoster.Merge(Self, [], [], [Discovered(Stranger, "客厅的台式机")], [], Credentials());

        // The whole point of the split: an announcement proves a machine exists and nothing else. It holds no
        // credential this listener would accept, so a list that called it a member would be promising a delivery
        // that cannot happen — which is exactly what the strip used to do.
        var row = Assert.Single(rows);
        Assert.Equal(LanDeviceState.Invitable, row.State);
        Assert.Equal("客厅的台式机", row.DeviceName);
        Assert.Equal(LanProtocol.KindUnknown, row.Kind);
        Assert.Equal("192.168.1.9:59871", row.Where);
    }

    [Fact]
    public void A_peer_this_machine_cannot_call_is_incomplete_rather_than_a_member()
    {
        var rows = LanDeviceRoster.Merge(Self, [Record(Legacy, "旧电脑", LanProtocol.KindWindows)], [], [],
            [], Credentials());

        // A pairing that only went one way: the row exists, the credential to spend on it does not. Calling it a
        // member would put it in the room with every poll of it answering 请先配对.
        Assert.Equal(LanDeviceState.Incomplete, Assert.Single(rows).State);
    }

    [Fact]
    public void A_peer_with_a_credential_is_a_member_and_a_phone_never_needs_one()
    {
        var rows = LanDeviceRoster.Merge(Self,
            [Record(Computer, "书房电脑", LanProtocol.KindWindows), Record(Phone, "小明的手机", LanProtocol.KindPhone)],
            [], [], [], Credentials(Computer));

        // A phone is never dialled — it answers nowhere — so demanding a credential of it would file every phone
        // the user ever paired under 可邀请.
        Assert.Equal(LanDeviceState.Member, rows.Single(row => row.DeviceId == Computer).State);
        Assert.Equal(LanDeviceState.Member, rows.Single(row => row.DeviceId == Phone).State);
    }

    [Fact]
    public void An_agreement_outranks_a_broadcast_and_a_broadcast_outranks_a_relay()
    {
        var rows = LanDeviceRoster.Merge(Self,
            [Record(Computer, "书房电脑", LanProtocol.KindWindows)],
            [],
            [Discovered(Computer, "别人的名字")],
            [Relayed(Computer, "第三个名字", null)],
            Credentials(Computer));

        // One device, three witnesses. The name this machine agreed with is the one it shows, and a weaker
        // witness never gets to rewrite it.
        var row = Assert.Single(rows);
        Assert.Equal("书房电脑", row.DeviceName);
        Assert.Equal(LanDeviceState.Member, row.State);
        Assert.Equal("192.168.1.20:59871", row.Where);
    }

    [Fact]
    public void A_relayed_phone_keeps_the_gateway_it_was_heard_through()
    {
        var rows = LanDeviceRoster.Merge(Self, [], [], [], [Relayed(Phone, "小明的手机", Computer)], Credentials());

        // A browser has no listener, so the only useful thing to say about it is which member it talks through —
        // and that is what makes it invitable at all.
        var row = Assert.Single(rows);
        Assert.Equal(LanDeviceState.Invitable, row.State);
        Assert.Equal(Computer, row.GatewayDeviceId);
        Assert.Equal(LanProtocol.KindPhone, row.Kind);
        Assert.False(row.IsDialable);
        Assert.Equal("", row.Where);
    }

    [Fact]
    public void A_relayed_device_is_askable_when_the_member_holding_it_can_be_dialled()
    {
        var rows = LanDeviceRoster.Merge(Self, [Record(Computer, "书房电脑", LanProtocol.KindWindows)], [], [],
            [Relayed(Phone, "小明的手机", Computer)], Credentials(Computer));

        // The whole point of the relay: this machine cannot dial the phone, but it can dial the computer that
        // serves its page, and that computer can carry the question. Without this the capability exists in the
        // service layer and no button on the screen reaches it.
        var row = Assert.Single(rows, entry => entry.DeviceId == Phone);
        Assert.True(row.CanInvite);

        // …and the two answers stay two answers. The phone is askable precisely because it is not dialable: a merge
        // that conflated them would try to open a socket to a device that listens on nothing, and would send the
        // question nowhere while looking as though it had sent it to somebody.
        Assert.False(row.IsDialable);
    }

    [Fact]
    public void A_relayed_device_whose_gateway_the_roster_does_not_know_is_not_askable()
    {
        var rows = LanDeviceRoster.Merge(Self, [], [], [Discovered(Stranger, "客厅的台式机")],
            [Relayed(Phone, "小明的手机", Computer)], Credentials());

        // Nobody here holds the phone, so there is nobody to ask, and the window would refuse the click on the
        // spot. A button that can only ever say "no" is a button that lies about what it can do. The row that is on
        // the strip with a real port is askable on its own account, which is the contrast this case pins.
        var relayed = Assert.Single(rows, entry => entry.DeviceId == Phone);
        Assert.False(relayed.CanInvite);
        Assert.False(relayed.IsDialable);
        Assert.True(Assert.Single(rows, entry => entry.DeviceId == Stranger).CanInvite);
    }

    [Fact]
    public void A_relayed_device_whose_gateway_answers_nowhere_is_not_askable()
    {
        var rows = LanDeviceRoster.Merge(Self,
            [Record(Computer, "书房电脑", LanProtocol.KindWindows), SilentPhone(Legacy, "旧手机", null)],
            [], [], [Relayed(Phone, "小明的手机", Legacy)], Credentials(Computer));

        // The gateway exists as a row and still cannot be dialled — a phone does not listen, whatever its row says.
        // A chain of two devices that both answer nowhere has nobody left to ask, so the flag stops at the first
        // link rather than searching on through it.
        var relayed = Assert.Single(rows, entry => entry.DeviceId == Phone);
        Assert.Equal(Legacy, relayed.GatewayDeviceId);
        Assert.False(relayed.CanInvite);
    }

    [Fact]
    public void A_computer_heard_on_the_subnet_is_askable_without_a_gateway()
    {
        var rows = LanDeviceRoster.Merge(Self, [], [], [Discovered(Stranger, "客厅的台式机")], [], Credentials());

        // Direct reachability is enough on its own: a broadcast carries an address and a port and needs no member
        // to carry anything for it.
        var row = Assert.Single(rows);
        Assert.True(row.IsDialable);
        Assert.True(row.CanInvite);
        Assert.Null(row.GatewayDeviceId);
    }

    [Fact]
    public void A_paired_phone_talking_through_a_dialable_computer_is_askable_however_the_rows_arrive()
    {
        // The phone's row comes first, so nothing could have settled it while the peer list was being walked: the
        // computer it leans on is only written two rows later.
        var rows = LanDeviceRoster.Merge(Self,
            [SilentPhone(Phone, "小明的手机", Computer), Record(Computer, "书房电脑", LanProtocol.KindWindows)],
            [], [], [], Credentials(Computer));

        // This is the row an accepted invitation leaves behind: a member, with no listener, naming the computer
        // that served its page. Asking it again means going through that computer, so the answer must not depend
        // on the order the peer list happened to be read in.
        var phone = Assert.Single(rows, entry => entry.DeviceId == Phone);
        Assert.Equal(LanDeviceState.Member, phone.State);
        Assert.True(phone.CanInvite);
        Assert.False(phone.IsDialable);
        Assert.True(Assert.Single(rows, entry => entry.DeviceId == Computer).CanInvite);
    }

    [Fact]
    public void This_machine_is_never_a_row_in_its_own_list()
    {
        var rows = LanDeviceRoster.Merge(Self,
            [Record(Self, "这台电脑", LanProtocol.KindWindows)],
            [Present(Self, "这台电脑")],
            [Discovered(Self, "这台电脑")],
            [Relayed(Self, "这台电脑", null)],
            Credentials(Self));

        Assert.Empty(rows);
    }

    [Fact]
    public void A_member_that_stopped_saying_it_was_here_is_still_a_member()
    {
        var offline = LanDeviceRoster.Merge(Self, [Record(Computer, "书房电脑", LanProtocol.KindWindows)], [], [], [],
            Credentials(Computer));
        var online = LanDeviceRoster.Merge(Self, [Record(Computer, "书房电脑", LanProtocol.KindWindows)],
            [Present(Computer, "书房电脑")], [], [], Credentials(Computer));

        // Presence is a lease, not a record: a machine that is switched off is still one this user agreed with,
        // and a list that dropped it would look like the pairing had been forgotten.
        Assert.False(Assert.Single(offline).Online);
        Assert.Equal(LanDeviceState.Member, Assert.Single(offline).State);
        Assert.True(Assert.Single(online).Online);
    }

    [Fact]
    public void Members_come_before_invitations_and_each_group_reads_in_name_order()
    {
        var rows = LanDeviceRoster.Merge(Self,
            [Record(Computer, "乙电脑", LanProtocol.KindWindows)],
            [],
            [Discovered(Stranger, "甲电脑"), Discovered(Legacy, "丙电脑")],
            [], Credentials(Computer));

        Assert.Equal([LanDeviceState.Member, LanDeviceState.Invitable, LanDeviceState.Invitable],
            rows.Select(row => row.State));
        Assert.Equal(["乙电脑", "丙电脑", "甲电脑"], rows.Select(row => row.DeviceName));
    }

    [Fact]
    public void A_device_an_invitation_has_gone_to_says_so_and_a_member_never_does()
    {
        var rows = LanDeviceRoster.Merge(Self,
            [Record(Computer, "书房电脑", LanProtocol.KindWindows)],
            [], [Discovered(Stranger, "客厅的台式机")], [], Credentials(Computer),
            new HashSet<string>([Stranger, Computer], StringComparer.OrdinalIgnoreCase));

        // The invitation is this machine's own intent and only ever rewrites a row that has not joined. A device
        // that has since become a member must not be sent back to the invitation list by a stale marker.
        Assert.Equal(LanDeviceState.Invited, rows.Single(row => row.DeviceId == Stranger).State);
        Assert.Equal(LanDeviceState.Member, rows.Single(row => row.DeviceId == Computer).State);
    }

    private static HashSet<string> Credentials(params string[] deviceIds) =>
        new(deviceIds, StringComparer.OrdinalIgnoreCase);

    private static LanPeerRecord Record(string deviceId, string name, string kind) =>
        new(deviceId, name, kind, Now, Now, LanOptions.DefaultPort, ["192.168.1.20"]);

    /// <summary>
    /// A phone this machine paired with: remembered at an address it answered from, and listening on nothing. What
    /// an accepted invitation writes, which is why a gateway can be part of the record itself.
    /// </summary>
    private static LanPeerRecord SilentPhone(string deviceId, string name, string? gateway) =>
        new(deviceId, name, LanProtocol.KindPhone, Now, Now, 0, ["192.168.1.5"], gateway);

    private static LanDiscoveredPeer Discovered(string deviceId, string name) =>
        new(deviceId, name, "192.168.1.9", LanOptions.DefaultPort);

    private static LanRoomMember Present(string deviceId, string name) =>
        new(deviceId, name, LanProtocol.KindWindows, "192.168.1.20", LanOptions.DefaultPort, Now, Discovered: false);

    private static LanMemberWire Relayed(string deviceId, string name, string? gateway) =>
        new(deviceId, name, LanProtocol.KindPhone, gateway, Now);
}
