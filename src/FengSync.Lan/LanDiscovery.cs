using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FengSync.Lan;

/// <summary>A computer this process heard on UDP, before anybody has paired with it.</summary>
public sealed record LanDiscoveredPeer(string DeviceId, string DeviceName, string Address, int Port);

/// <summary>
/// The discovery datagram: what it says, and which of those sentences describe a computer.
/// </summary>
/// <remarks>
/// <para>
/// One message does both jobs. A computer repeats its own announcement, and a computer that wants to know who
/// is here listens for those, so there is no request-and-response pair. That is deliberate: a request that only
/// produces an answer when somebody happens to be listening is the failure shape this protocol keeps meeting —
/// an access point with client isolation drops the broadcast silently, and the only honest evidence is that
/// nobody said anything.
/// </para>
/// <para>
/// The payload is one line of ASCII, and a name that is not ASCII or not space-free travels percent-encoded the
/// same way <c>X-FS-Rel</c> does. The encoding is not decoration: fields are separated by a space, and
/// <c>DESKTOP A</c> or a machine named in Chinese sent raw would shift the port field and make the whole
/// announcement unreadable — which is a stranger's machine invisible, not merely mislabelled.
/// </para>
/// </remarks>
public static class LanDiscovery
{
    /// <summary>How often an announcement repeats, and therefore how long a freshly opened window waits.</summary>
    public static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The largest payload this side will read. Our own announcement is well under a tenth of that, so a bigger
    /// datagram is not a peer of ours and is dropped without being parsed.
    /// </summary>
    public const int MaximumDatagramBytes = 1024;

    /// <summary>The field count of a well-formed announcement: magic, device id, name, port.</summary>
    public const int FieldCount = 4;

    /// <summary>What a blank machine name becomes on the wire, so an announcement never carries an empty field.</summary>
    public const string UnnamedDevice = "computer";

    /// <summary>
    /// The one line a computer sends about itself.
    /// </summary>
    /// <remarks>
    /// A device id in an unreadable form is refused here rather than on the wire, because this is the only place
    /// that knows the parts are meant to be generated hex: after the join, a stray space is unrecoverable.
    /// </remarks>
    public static string BuildAnnouncement(string deviceId, string? deviceName, int port)
    {
        if (!LanProtocol.IsPeerDeviceId(deviceId))
            throw new ArgumentException("设备标识必须是十六进制串。", nameof(deviceId));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        var name = LanProtocol.SanitizePeerName(deviceName);
        if (name.Length == 0) name = UnnamedDevice;
        return string.Join(' ', LanProtocol.DiscoveryMagic, deviceId, Uri.EscapeDataString(name),
            port.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Reads an announcement into a peer, or says it is not one.
    /// </summary>
    /// <remarks>
    /// Every rejection is silent and total, which is the correct shape: this socket is open to the whole subnet,
    /// so anything else that broadcasts on this port will be answered with a shrug rather than a half-believed
    /// row. The address comes from the packet, never from the payload — a peer cannot be told to be anywhere it
    /// is not, and a discovery record that could would be an invitation to send files to a stranger's host.
    /// </remarks>
    public static bool TryReadAnnouncement(string? payload, string remoteAddress, out LanDiscoveredPeer peer)
    {
        peer = null!;
        if (string.IsNullOrWhiteSpace(payload)) return false;

        var fields = payload.Trim().Split(' ');
        if (fields.Length != FieldCount) return false;
        if (!string.Equals(fields[0], LanProtocol.DiscoveryMagic, StringComparison.Ordinal)) return false;
        if (!LanProtocol.IsPeerDeviceId(fields[1])) return false;
        if (!int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535) return false;

        var name = LanProtocol.SanitizePeerName(Uri.UnescapeDataString(fields[2]));
        if (name.Length == 0 || string.IsNullOrWhiteSpace(remoteAddress)) return false;
        peer = new(fields[1], name, remoteAddress.Trim(), port);
        return true;
    }

    /// <summary>
    /// Whether what just arrived says something the window does not already show.
    /// </summary>
    /// <remarks>
    /// An announcement repeats forever, so without this gate a peer list would repaint twice a second and the
    /// row the user is pointing at would move. A changed address counts as news: a laptop that walked from one
    /// network to another is reachable at a different number, and the old one is a dead end the user would
    /// otherwise be asked to pair with.
    /// </remarks>
    public static bool IsNewInformation(LanDiscoveredPeer? known, LanDiscoveredPeer incoming) =>
        known is null
        || !string.Equals(known.DeviceName, incoming.DeviceName, StringComparison.Ordinal)
        || !string.Equals(known.Address, incoming.Address, StringComparison.Ordinal)
        || known.Port != incoming.Port;
}

/// <summary>
/// The socket that carries discovery for as long as the window is open.
/// </summary>
/// <remarks>
/// <para>
/// One socket, bound to every interface, both announces and listens. Sending a subnet's own broadcast address is
/// what reaches a multi-homed desk correctly: routing follows the destination, so a packet aimed at
/// <c>192.168.1.255</c> leaves through the adapter that owns <c>192.168.1.0/24</c> even when the machine also
/// runs a VPN and two virtual switches. The global broadcast goes alongside it because an adapter that answers a
/// mask of <c>0.0.0.0</c> yields no usable subnet address, and <see cref="LanNetwork"/> falls back to <c>/24</c>
/// rather than giving up.
/// </para>
/// <para>
/// Nothing here is a judgement about who to trust: a discovered peer is a row in a list until somebody types
/// its code, and the bytes that pair are checked by <see cref="LanSession"/> like any other request. Discovery
/// only ever shortens the distance to a pairing question.
/// </para>
/// </remarks>
public sealed class LanDiscoveryService : IAsyncDisposable
{
    private readonly string _deviceId;
    private string _payload;
    private readonly int _port;
    private readonly Socket _socket;
    private readonly IReadOnlyList<string> _broadcasts;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, LanDiscoveredPeer> _known = [];
    private readonly List<Task> _loops = [];
    private readonly Lock _gate = new();

    /// <summary>A computer that said something this window had not heard before.</summary>
    public event Action<LanDiscoveredPeer>? PeerFound;

    private LanDiscoveryService(string deviceId, string payload, int port, Socket socket, IReadOnlyList<string> broadcasts)
    {
        _deviceId = deviceId;
        _payload = payload;
        _port = port;
        _socket = socket;
        _broadcasts = broadcasts;
    }

    /// <summary>
    /// Binds the discovery port and starts announcing and listening.
    /// </summary>
    /// <param name="addresses">
    /// The networks to announce on. A parameter rather than a call to <see cref="LanNetwork.Probe"/> inside, so a
    /// test can put this on loopback — where there is nothing to announce to — and still prove the whole path of
    /// socket, payload, parse, and event without putting a packet on the network the user is working on.
    /// </param>
    public static LanDiscoveryService Start(string deviceId, string? deviceName, int port,
        IReadOnlyList<LanNetworkAddress>? addresses = null)
    {
        var payload = LanDiscovery.BuildAnnouncement(deviceId, deviceName, port);

        var broadcasts = new List<string>();
        foreach (var candidate in addresses ?? LanNetwork.Probe())
        {
            // An address that is its own broadcast has nobody to talk to, which is what loopback is.
            if (string.Equals(candidate.Address, candidate.Broadcast, StringComparison.Ordinal)) continue;
            if (!broadcasts.Contains(candidate.Broadcast, StringComparer.Ordinal)) broadcasts.Add(candidate.Broadcast);
        }

        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Broadcasting is not a socket default, and a discovery loop that silently fails to broadcast is the
            // kind of bug that shows up as "the other computer never appears" months later.
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            var service = new LanDiscoveryService(deviceId, payload, port, socket, broadcasts);
            service.Begin();
            return service;
        }
        catch (SocketException ex)
        {
            throw new LanTransferException(LanRefusal.PortUnavailable, $"发现端口 {port} 无法监听。", ex);
        }
    }

    /// <summary>The discovery port, which is the transfer port: one number, two protocols, one firewall rule each.</summary>
    public int Port => _port;

    /// <summary>The networks this socket will announce on. Empty means this machine has no LAN to describe.</summary>
    public IReadOnlyList<string> BroadcastTargets => _broadcasts;

    /// <summary>
    /// Changes what this socket says about itself, on the same port and the same socket.
    /// </summary>
    /// <remarks>
    /// A separate verb from <see cref="Start"/> because rebinding is the expensive, user-visible part: a window
    /// that restarted discovery to rename itself would drop every computer it had already heard from and make the
    /// other side's list blink empty for the length of a beat. The payload travels whole, so a peer reads either
    /// the old name or the new one and never a mixture of the two.
    /// </remarks>
    public void AnnounceAs(string? deviceName) => _payload = LanDiscovery.BuildAnnouncement(_deviceId, deviceName, _port);

    private void Begin()
    {
        _loops.Add(Task.Run(() => ReceiveAsync(_stop.Token)));
        _loops.Add(Task.Run(() => AnnounceAsync(_stop.Token)));
    }

    /// <summary>The peers heard so far, in a stable order, for a window that opened after they did.</summary>
    public IReadOnlyList<LanDiscoveredPeer> Snapshot()
    {
        lock (_gate)
        {
            return [.. _known.Values.OrderBy(peer => peer.Address, StringComparer.Ordinal)];
        }
    }

    private async Task AnnounceAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Read per beat rather than once for the loop, so a rename that lands mid-session reaches the next
            // announcement instead of waiting for the window to be closed and opened again.
            await AnnounceOnceAsync(Encoding.ASCII.GetBytes(_payload), ct).ConfigureAwait(false);
            try { await Task.Delay(LanDiscovery.AnnounceInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task AnnounceOnceAsync(byte[] bytes, CancellationToken ct)
    {
        // With no subnet to aim at there is nothing this side could reach, and a packet thrown at
        // 255.255.255.255 would leave through whichever interface the routing table prefers — onto a network
        // chosen by accident, from a machine that has not been shown to have a peer on it.
        if (_broadcasts.Count == 0) return;

        foreach (var target in _broadcasts.Append(IPAddress.Broadcast.ToString()))
        {
            if (!IPAddress.TryParse(target, out var address)) continue;
            await SendAsync(new IPEndPoint(address, _port), bytes, ct).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(IPEndPoint target, byte[] bytes, CancellationToken ct)
    {
        try
        {
            await _socket.SendToAsync(new ArraySegment<byte>(bytes), target, ct).ConfigureAwait(false);
        }
        // A network that refuses the broadcast is the common case, not an error: the address box is still there,
        // and a discovery loop that reported this every two seconds would fill a log with the sound of a
        // coffee shop.
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[LanDiscovery.MaximumDatagramBytes];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult arrival;
            try
            {
                arrival = await _socket.ReceiveMessageFromAsync(
                    buffer.AsMemory(), new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
            }
            // Stopping is the normal end of this loop, and a socket closed under it is the same news twice.
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (arrival.ReceivedBytes <= 0) continue;
            // ASCII on purpose: an announcement is ASCII by construction, and bytes that are not arrive here
            // as '?' and fail the magic check rather than becoming a row built from somebody else's protocol.
            var said = Encoding.ASCII.GetString(buffer, 0, arrival.ReceivedBytes);
            if (arrival.RemoteEndPoint is not IPEndPoint from) continue;
            if (!LanDiscovery.TryReadAnnouncement(said, from.Address.ToString(), out var peer)) continue;
            // Our own broadcast comes back on a multi-homed machine, and a list that contained this computer as
            // a device to pair with is a trap: the code it would type is one nobody armed.
            if (peer.DeviceId == _deviceId) continue;
            Record(peer);
        }
    }

    private void Record(LanDiscoveredPeer peer)
    {
        lock (_gate)
        {
            if (_known.TryGetValue(peer.DeviceId, out var known) && !LanDiscovery.IsNewInformation(known, peer)) return;
            _known[peer.DeviceId] = peer;
        }

        PeerFound?.Invoke(peer);
    }

    /// <summary>Stops announcing, stops listening, and frees the port.</summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _socket.Dispose();

        try { await Task.WhenAll([.. _loops]).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        _stop.Dispose();
    }
}
