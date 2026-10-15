using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FengSync.Lan;

/// <summary>One usable IPv4 address on this machine, with the broadcast address that goes with it.</summary>
public sealed record LanNetworkAddress(string AdapterName, string Address, string Broadcast, int PrefixLength);

/// <summary>
/// Which of this machine's addresses a LAN peer could actually reach, and what to broadcast to.
/// </summary>
/// <remarks>
/// <para>
/// The judgement is split from the socket work because it is the part that decides what a user scans: a desk
/// with a VPN, a Hyper-V switch and a Wi-Fi card has five IPv4 addresses and exactly one of them is on the
/// network the phone is on. Listing them all is the safety net — the window shows every candidate and the
/// QR follows whichever the user picks.
/// </para>
/// <para>
/// Sorting by address rather than by adapter order is deliberate: the same machine on the same network must
/// offer the same first address every time the window opens, or the printed QR changes for no reason anybody
/// can explain.
/// </para>
/// </remarks>
public static class LanNetwork
{
    /// <summary>A prefix that is not the whole address is a network, and a /32 host route has nobody to talk to.</summary>
    public const int MinimumPrefixLength = 8;

    /// <summary>Every usable LAN IPv4 address, plus loopback last so a local test can name the same port.</summary>
    public static IReadOnlyList<LanNetworkAddress> Probe()
    {
        var found = new List<LanNetworkAddress>();
        foreach (var adapter in SafeAdapters())
        {
            // Loopback is excluded from the adapter check on purpose: it comes back as the local address below,
            // in last position, rather than as a peer's equal.
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) continue;

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (!IsUsableIpv4(unicast.Address)) continue;
                var prefix = PrefixOf(unicast);
                if (prefix is < MinimumPrefixLength or > 30) continue;
                found.Add(new LanNetworkAddress(adapter.Name, unicast.Address.ToString(),
                    BroadcastOf(unicast.Address, prefix).ToString(), prefix));
            }
        }

        found.Sort((left, right) => string.CompareOrdinal(left.Address, right.Address));
        // Loopback joins the list last and only after the real adapters, so a machine with no network card
        // still has one address to show and a machine with one never offers the local address first.
        found.Add(new LanNetworkAddress("Loopback", IPAddress.Loopback.ToString(), IPAddress.Loopback.ToString(), 8));
        return found;
    }

    /// <summary>
    /// Whether an address is one a peer could be on.
    /// </summary>
    /// <remarks>
    /// <c>169.254/16</c> is excluded because it means the adapter asked for a configuration and got none: a QR
    /// carrying one of those is the most confusing thing this feature can print, since it will look paired and
    /// then never connect.
    /// </remarks>
    public static bool IsUsableIpv4(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.None)) return false;
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 169 && bytes[1] == 254) return false;
        // 224/4 is a group address, not one a machine owns. Type check instead of a framework helper, because
        // the only IsMulticast in this BCL sits on an IPv4 packet-info struct no unicast list hands back.
        return bytes[0] is < 224 or > 239;
    }

    /// <summary>The subnet broadcast for an address, computed as <c>address &amp; ~mask</c>.</summary>
    /// <remarks>
    /// Computed rather than read from the adapter because <c>UnicastIPAddressInformation</c> answers a mask of
    /// <c>0.0.0.0</c> on some Windows adapters, and a broadcast address of <c>0.0.0.0</c> is silently dropped
    /// by the socket layer — a discovery packet that goes nowhere with no error to show for it.
    /// </remarks>
    public static IPAddress BroadcastOf(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("只有 IPv4 有子网广播地址。", nameof(address));
        if (prefixLength is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(prefixLength));

        // Set every host bit, one byte at a time. The bytes are read in network order, so a single integer
        // mask over them would be reversed by BitConverter and produce a broadcast address that looks like a
        // typo — 255.1.168.192 for a /24 that should have been 192.168.1.255.
        var bytes = address.GetAddressBytes();
        for (var index = 0; index < bytes.Length; index++)
        {
            var networkBits = Math.Clamp(prefixLength - index * 8, 0, 8);
            bytes[index] |= (byte)(0xFF >> networkBits);
        }

        return new IPAddress(bytes);
    }

    /// <summary>
    /// The prefix length to broadcast on. A missing or absurd mask falls back to <c>/24</c>, which is what
    /// almost every home and office network is and is still smaller than the whole space.
    /// </summary>
    private static int PrefixOf(UnicastIPAddressInformation unicast)
    {
        var mask = unicast.IPv4Mask;
        if (mask is not null && mask.AddressFamily == AddressFamily.InterNetwork)
        {
            var bits = 0;
            foreach (var value in mask.GetAddressBytes())
                for (var bit = 7; bit >= 0; bit--)
                    if ((value & (1 << bit)) != 0) bits++;
            if (bits is >= MinimumPrefixLength and <= 32) return bits;
        }

        return 24;
    }

    private static IEnumerable<NetworkInterface> SafeAdapters()
    {
        // An adapter enumeration can throw on a machine with a driver in a bad state, and that must not become
        // "the listener cannot start" — the manual address box still works without knowing which cards exist.
        try { return NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return []; }
    }
}
