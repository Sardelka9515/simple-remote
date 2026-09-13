using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SimpleRemote.Net;

/// <summary>A candidate address the phone could reach the host on.</summary>
/// <param name="Address">IPv4 literal.</param>
/// <param name="InterfaceName">Friendly adapter name, shown in the picker.</param>
/// <param name="Type">Adapter media type.</param>
/// <param name="Score">Higher is more likely to be the real LAN. See <see cref="LocalAddresses.Rank"/>.</param>
public sealed record AddressCandidate(string Address, string InterfaceName, NetworkInterfaceType Type, int Score);

/// <summary>
/// Picks the address to put in the QR code.
///
/// This matters more than it looks: a machine with Hyper-V, WSL, Docker or a VPN installed has
/// several "up" adapters with routable-looking IPv4 addresses, and choosing the wrong one produces
/// a QR code that scans perfectly and then times out - the single most confusing possible failure.
/// </summary>
public static class LocalAddresses
{
    /// <summary>Adapter-name fragments that indicate a virtual/host-only network the phone cannot reach.</summary>
    private static readonly string[] VirtualMarkers =
    [
        "vethernet", "hyper-v", "virtualbox", "vmware", "vmnet", "wsl", "docker",
        "loopback", "tailscale", "zerotier", "hamachi", "tap-", "tun", "openvpn",
        "wireguard", "bluetooth", "pseudo-interface", "npcap", "wan miniport",
    ];

    /// <summary>Enumerates reachable IPv4 addresses, best candidate first.</summary>
    public static IReadOnlyList<AddressCandidate> Enumerate()
    {
        var results = new List<AddressCandidate>();

        foreach (var nic in SafeGetInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            // No gateway usually means a host-only or link-local segment. We keep such adapters
            // but score them down rather than dropping them, since some LANs are gateway-less.
            var hasGateway = props.GatewayAddresses.Any(g =>
                g.Address is { AddressFamily: AddressFamily.InterNetwork } a && !a.Equals(IPAddress.Any));

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;

                var text = ua.Address.ToString();
                if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue; // APIPA: no DHCP

                results.Add(new AddressCandidate(
                    text, nic.Name, nic.NetworkInterfaceType,
                    Rank(nic.Name, nic.Description, nic.NetworkInterfaceType, hasGateway, text)));
            }
        }

        return results.OrderByDescending(r => r.Score).ThenBy(r => r.Address, StringComparer.Ordinal).ToList();
    }

    private static NetworkInterface[] SafeGetInterfaces()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return []; }
    }

    /// <summary>
    /// Scores an adapter. Pure so it can be unit tested against synthetic adapters rather than
    /// whatever hardware the test machine happens to have.
    /// </summary>
    public static int Rank(string name, string description, NetworkInterfaceType type, bool hasGateway, string address)
    {
        var score = 100;

        var haystack = $"{name} {description}".ToLowerInvariant();
        if (VirtualMarkers.Any(m => haystack.Contains(m, StringComparison.Ordinal)))
            score -= 80;

        score += type switch
        {
            NetworkInterfaceType.Wireless80211 => 30, // phone is on Wi-Fi, so this subnet is likeliest
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 25,
            NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp => -40,
            _ => 0,
        };

        if (hasGateway) score += 20;

        // Private ranges in rough order of "looks like a home LAN".
        if (address.StartsWith("192.168.", StringComparison.Ordinal)) score += 15;
        else if (address.StartsWith("10.", StringComparison.Ordinal)) score += 10;
        else if (IsCarrierGrade172(address)) score += 10;
        else score -= 10; // public or unusual address: probably not the LAN the phone is on

        return score;
    }

    private static bool IsCarrierGrade172(string address)
    {
        if (!address.StartsWith("172.", StringComparison.Ordinal)) return false;
        var second = address.AsSpan(4);
        var dot = second.IndexOf('.');
        return dot > 0 && int.TryParse(second[..dot], out var octet) && octet is >= 16 and <= 31;
    }

    /// <summary>Best guess, honouring an explicit user pin when it is still present on the machine.</summary>
    public static string Best(string? preferred = null)
    {
        var all = Enumerate();
        if (preferred is not null && all.Any(a => a.Address == preferred)) return preferred;
        return all.Count > 0 ? all[0].Address : "127.0.0.1";
    }
}
