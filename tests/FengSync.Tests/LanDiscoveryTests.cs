using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers what a discovery announcement says, which of those sentences describe a computer, and the socket that
/// carries one — including the addresses a window may put in a QR code.
/// </summary>
/// <remarks>
/// <para>
/// The judgement half is tested as a function: a name with a space in it, a machine named in Chinese, a
/// neighbour's chat application broadcasting on the same port. The socket half is tested with real UDP on
/// loopback, because what a fake cannot prove is that the bytes this side writes are the bytes the other side
/// reads — the same reason <c>ResumableDownloadTests</c> drives a real rclone.
/// </para>
/// <para>
/// The address rules matter more than the transport. A QR printed from a link-local address, or a broadcast
/// thrown onto the wrong one of five adapters, is a phone that scans, opens, and fails.
/// </para>
/// </remarks>
public sealed class LanDiscoveryTests
{
    private const string Self = "0123456789abcdef0123456789abcdef";
    private const string Peer = "fedcba9876543210fedcba9876543210";
    private const int Port = 59871;

    [Theory]
    [InlineData("DESKTOP-AB1")]
    [InlineData("DESKTOP A B")]
    [InlineData("张三的电脑")]
    [InlineData("a+b")]
    [InlineData("  padded  ")]
    [InlineData("pc/with?slashes")]
    [InlineData("quote\"inside")]
    [InlineData("1234")]
    public void An_announcement_carries_its_name_whatever_the_name_looks_like(string name)
    {
        var payload = LanDiscovery.BuildAnnouncement(Self, name, Port);

        // One line of ASCII is the contract with every scanner on the subnet, so this is asserted before the
        // round trip: a raw CJK name or a raw space would break the fields, not merely mislabel the row.
        Assert.DoesNotContain('\n', payload);
        Assert.DoesNotContain('\r', payload);
        Assert.True(payload.All(character => character < 128), "载荷必须是纯 ASCII。");

        Assert.True(LanDiscovery.TryReadAnnouncement(payload, "192.168.1.20", out var peer));
        Assert.Equal(Self, peer.DeviceId);
        Assert.Equal(name.Trim(), peer.DeviceName);
        Assert.Equal(Port, peer.Port);
        Assert.Equal("192.168.1.20", peer.Address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_announces_a_placeholder_rather_than_an_empty_field(string? name)
    {
        var payload = LanDiscovery.BuildAnnouncement(Self, name, Port);

        // An empty field would shift the port into the name's position and every reader on the subnet would
        // throw the announcement away — a machine that announces itself into invisibility.
        Assert.Equal(LanDiscovery.FieldCount, payload.Split(' ').Length);
        Assert.True(LanDiscovery.TryReadAnnouncement(payload, "192.168.1.20", out var peer));
        Assert.Equal(LanDiscovery.UnnamedDevice, peer.DeviceName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void An_announcement_refuses_a_port_no_listener_could_hold(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LanDiscovery.BuildAnnouncement(Self, "pc", port));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("not hex at all")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000000000")]
    public void An_announcement_refuses_an_id_no_peer_of_ours_would_produce(string deviceId)
    {
        // "abc" is the tempting case: valid hex, and short. It is refused because an identifier is generated
        // rather than typed, and a three-character one is somebody else's sequence number.
        Assert.Throws<ArgumentException>(() => LanDiscovery.BuildAnnouncement(deviceId, "pc", Port));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("other-app-1 abcdef0123456789abcdef0123456789 pc 59871")]
    [InlineData("FENGSYNC-LAN-1 abcdef0123456789abcdef0123456789 pc")]
    [InlineData("FENGSYNC-LAN-1 abcdef0123456789abcdef0123456789 pc 59871 extra")]
    [InlineData("FENGSYNC-LAN-1 zz pc 59871")]
    [InlineData("FENGSYNC-LAN-1 abcdef0123456789abcdef0123456789 pc 0")]
    [InlineData("FENGSYNC-LAN-1 abcdef0123456789abcdef0123456789 pc 99999")]
    [InlineData("FENGSYNC-LAN-1 abcdef0123456789abcdef0123456789 pc port")]
    [InlineData("FENGSYNC-LAN-1 abcdef0123456789abcdef0123456789 %20 59871")]
    [InlineData("fengsync-lan-1 abcdef0123456789abcdef0123456789 pc 59871")]
    public void A_sentence_that_is_not_ours_is_not_a_peer(string? payload)
    {
        Assert.False(LanDiscovery.TryReadAnnouncement(payload, "192.168.1.20", out _));
    }

    [Fact]
    public void A_peer_is_read_only_from_where_the_packet_actually_came_from()
    {
        // The address is a parameter rather than a field, so a peer cannot announce that it lives somewhere
        // else. A record that could be steered would be an instruction to send files to a stranger.
        Assert.True(LanDiscovery.TryReadAnnouncement(
            LanDiscovery.BuildAnnouncement(Peer, "pc", Port), "10.1.2.3", out var peer));
        Assert.Equal("10.1.2.3", peer.Address);
    }

    [Fact]
    public void A_repeated_announcement_is_not_news_but_a_moved_one_is()
    {
        var first = new LanDiscoveredPeer(Peer, "office-pc", "192.168.1.20", Port);

        Assert.True(LanDiscovery.IsNewInformation(null, first));
        Assert.False(LanDiscovery.IsNewInformation(first, first));

        // Every one of these three is news, because each is a different machine or the same machine somewhere
        // else — and showing the stale address would be the row that pairs and then transfers nothing.
        Assert.True(LanDiscovery.IsNewInformation(first, first with { Address = "192.168.1.31" }));
        Assert.True(LanDiscovery.IsNewInformation(first, first with { Port = 59872 }));
        Assert.True(LanDiscovery.IsNewInformation(first, first with { DeviceName = "renamed" }));
    }

    [Theory]
    [InlineData("192.168.1.20", 24, "192.168.1.255")]
    [InlineData("10.0.0.5", 8, "10.255.255.255")]
    [InlineData("172.16.3.4", 12, "172.31.255.255")]
    [InlineData("192.168.1.20", 30, "192.168.1.23")]
    [InlineData("192.168.1.20", 32, "192.168.1.20")]
    [InlineData("192.168.1.20", 0, "255.255.255.255")]
    public void The_broadcast_address_is_the_complement_of_the_mask(string address, int prefix, string expected)
    {
        // The /32 row is the host route: its "broadcast" is itself, which is why the service skips a candidate
        // whose address equals its broadcast rather than sending a duplicate of the global one.
        Assert.Equal(expected, LanNetwork.BroadcastOf(IPAddress.Parse(address), prefix).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.5")]
    [InlineData("224.0.0.9")]
    [InlineData("239.255.255.250")]
    public void An_address_a_peer_cannot_reach_is_not_offered(string? address)
    {
        // 169.254 is the sharpest of these: an adapter that asked for a configuration and got none. A QR
        // carrying it looks paired, opens, and then never connects.
        Assert.False(LanNetwork.IsUsableIpv4(address is null ? null : IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.17.3.90")]
    [InlineData("172.20.4.11")]
    [InlineData("8.8.8.8")]
    public void An_address_a_peer_can_reach_is_offered(string address)
    {
        Assert.True(LanNetwork.IsUsableIpv4(IPAddress.Parse(address)));
    }

    [Fact]
    public void An_ipv6_address_is_not_offered_as_a_v4_one()
    {
        Assert.False(LanNetwork.IsUsableIpv4(IPAddress.IPv6Loopback));
        Assert.False(LanNetwork.IsUsableIpv4(IPAddress.IPv6Any));
    }

    [Fact]
    public void The_probe_names_loopback_last_so_a_real_network_is_offered_first()
    {
        var found = LanNetwork.Probe();

        // The order is the whole contract with the QR code: a machine with a VPN offers the address the phone
        // can reach, not the one the routing table invented.
        Assert.NotEmpty(found);
        Assert.Equal(IPAddress.Loopback.ToString(), found[^1].Address);
        foreach (var candidate in found.Take(found.Count - 1))
            Assert.True(LanNetwork.IsUsableIpv4(IPAddress.Parse(candidate.Address)), candidate.Address);
    }

    [Fact]
    public async Task A_peer_on_this_machine_is_found_over_a_real_socket()
    {
        var port = FreeUdpPort();
        await using var service = LanDiscoveryService.Start(Self, "listening-pc", port, [Loopback(port)]);
        var seen = new TaskCompletionSource<LanDiscoveredPeer>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PeerFound += peer => seen.TrySetResult(peer);

        using var sender = new UdpClient(AddressFamily.InterNetwork);
        await sender.SendAsync(
            Encoding.ASCII.GetBytes(LanDiscovery.BuildAnnouncement(Peer, "张 三's PC", port)),
            new IPEndPoint(IPAddress.Loopback, port));

        // Nothing is asserted about timing, but the wait is real: this proves the announcement this side would
        // send is readable by the code that receives it, on a socket, with a space and CJK in the name.
        var winner = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(seen.Task, winner);
        var peer = await seen.Task;
        Assert.Equal(Peer, peer.DeviceId);
        Assert.Equal("张 三's PC", peer.DeviceName);
        Assert.Equal(IPAddress.Loopback.ToString(), peer.Address);
        Assert.Equal(port, peer.Port);
        Assert.Contains(peer, service.Snapshot());
    }

    [Fact]
    public async Task A_peer_that_announces_itself_as_this_machine_is_not_found()
    {
        var port = FreeUdpPort();
        await using var service = LanDiscoveryService.Start(Self, "listening-pc", port, [Loopback(port)]);
        var found = new List<LanDiscoveredPeer>();
        service.PeerFound += peer => { lock (found) found.Add(peer); };

        using var sender = new UdpClient(AddressFamily.InterNetwork);
        // The same device id this service announces with, arriving from elsewhere: a machine with two adapters
        // does exactly this to itself, and a peer list containing the computer it was opened on is a trap.
        await sender.SendAsync(Encoding.ASCII.GetBytes(LanDiscovery.BuildAnnouncement(Self, "myself", port)),
            new IPEndPoint(IPAddress.Loopback, port));
        // …and a foreign id straight after, which is the proof the loop is alive rather than merely silent.
        await sender.SendAsync(Encoding.ASCII.GetBytes(LanDiscovery.BuildAnnouncement(Peer, "other", port)),
            new IPEndPoint(IPAddress.Loopback, port));

        await WaitFor(() => { lock (found) return found.Count; }, 1);
        lock (found)
        {
            Assert.Single(found);
            Assert.Equal(Peer, found[0].DeviceId);
        }
    }

    [Fact]
    public async Task Something_else_on_the_port_is_ignored_rather_than_belonging_to_the_list()
    {
        var port = FreeUdpPort();
        await using var service = LanDiscoveryService.Start(Self, "listening-pc", port, [Loopback(port)]);
        var found = 0;
        service.PeerFound += _ => Interlocked.Increment(ref found);

        using var sender = new UdpClient(AddressFamily.InterNetwork);
        foreach (var noise in new[]
        {
            "NETBIOS-ish garbage",
            "FENGSYNC-LAN-2 " + Peer + " pc " + port,
            "FENGSYNC-LAN-1 " + Peer + " pc",
        })
        {
            await sender.SendAsync(Encoding.ASCII.GetBytes(noise), new IPEndPoint(IPAddress.Loopback, port));
        }

        await sender.SendAsync(Encoding.ASCII.GetBytes(LanDiscovery.BuildAnnouncement(Peer, "pc", port)),
            new IPEndPoint(IPAddress.Loopback, port));
        await WaitFor(() => Volatile.Read(ref found), 1);

        // Exactly one row: the three noise packets never reached a list, and a listener that mis-parses a
        // neighbour's protocol would show the user a device that refuses every code.
        Assert.Equal(1, Volatile.Read(ref found));
    }

    [Fact]
    public async Task Stopping_the_service_frees_the_port_immediately()
    {
        var port = FreeUdpPort();
        var service = LanDiscoveryService.Start(Self, "listening-pc", port, [Loopback(port)]);
        await service.DisposeAsync();

        // The window can be closed and reopened, and a port still held by a socket nobody owns would answer the
        // second opening with "端口被占用" for the rest of the session.
        using var rebinding = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        rebinding.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    [Fact]
    public async Task A_loopback_only_setup_announces_nothing()
    {
        var port = FreeUdpPort();
        await using var service = LanDiscoveryService.Start(Self, "listening-pc", port, [Loopback(port)]);

        // The seam the tests above depend on, stated as a fact: with no subnet to aim at, this service sends no
        // packet at all — so nothing here can put a fake device onto the network the user is working on.
        Assert.Empty(service.BroadcastTargets);
    }

    private static LanNetworkAddress Loopback(int port) =>
        new("Loopback", IPAddress.Loopback.ToString(), IPAddress.Loopback.ToString(), 8);

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static async Task WaitFor(Func<int> count, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (count() < expected && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }
}
