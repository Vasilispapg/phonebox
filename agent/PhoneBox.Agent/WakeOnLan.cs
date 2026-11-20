using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PhoneBox.Agent;

/// <summary>
/// Wake-on-LAN. A magic packet is six 0xFF bytes followed by the target's MAC 16 times, sent as a UDP broadcast to port 9.
/// The agent sends it for the <c>builtin:wake</c> shortcut; <see cref="LocalMacs"/> tells the phone which MACs belong to this
/// computer, so the phone can wake it while the agent is not running. Only private LAN adapters are used: Tailscale, VPN,
/// tunnel and link-local addresses have no broadcast domain.
/// </summary>
public static class WakeOnLan
{
    public const int Port = 9;
    private const int Repeats = 3;
    private const int RepeatDelayMs = 100;
    private const int MaxReportedMacs = 4;

    /// <summary>An adapter a packet can leave from: its address, its subnet's broadcast address and its MAC.</summary>
    public sealed record LanNic(string Name, IPAddress Address, IPAddress Broadcast, byte[] Mac, bool Wired);

    /// <summary>Accepts AA:BB:CC:DD:EE:FF, AA-BB-CC-DD-EE-FF and AABBCCDDEEFF; rejects all-zero and broadcast.</summary>
    public static bool TryParseMac(string? text, out byte[] mac)
    {
        mac = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = text.Trim().Replace(":", "").Replace("-", "");
        if (hex.Length != 12) return false;
        try { mac = Convert.FromHexString(hex); }
        catch (FormatException) { mac = []; return false; }
        if (mac.All(b => b == 0x00) || mac.All(b => b == 0xFF)) { mac = []; return false; }
        return true;
    }

    public static string FormatMac(byte[] mac) => string.Join(':', mac.Select(b => b.ToString("X2")));

    public static byte[] BuildPacket(byte[] mac)
    {
        var packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++) Array.Copy(mac, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    /// <summary>The shortcut's arguments: "MAC" or "MAC broadcast-IPv4". Returns an error message, or null.</summary>
    public static string? ParseArgs(string? args, out byte[] mac, out IPAddress? broadcast)
    {
        broadcast = null;
        var parts = (args ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !TryParseMac(parts[0], out mac))
        {
            mac = [];
            return "not a valid MAC address (use AA:BB:CC:DD:EE:FF)";
        }
        if (parts.Length > 2) return "too many values: use the MAC address and, optionally, a broadcast address";
        if (parts.Length == 2)
        {
            if (!IPAddress.TryParse(parts[1], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                return "not a valid broadcast address (use an IPv4 address such as 192.168.1.255)";
            broadcast = ip;
        }
        return null;
    }

    /// <summary>True for a wired or Wi-Fi adapter with a private LAN address, a mask that leaves host bits and no skip rule against it.</summary>
    public static bool IsEligible(NetworkInterfaceType type, string description, bool hasGateway, IPAddress address, IPAddress mask)
    {
        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) return false;
        if (address.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork) return false;
        var a = address.GetAddressBytes();
        var privateLan = a[0] == 10 || (a[0] == 172 && a[1] is >= 16 and <= 31) || (a[0] == 192 && a[1] == 168);
        if (!privateLan) return false;
        if (mask.GetAddressBytes().All(b => b == 0xFF)) return false; // /32: no host bits, no broadcast address
        return !Lan.ShouldSkipAdapter(description, hasGateway);
    }

    /// <summary>The adapters packets can leave from, one entry per eligible address.</summary>
    public static IReadOnlyList<LanNic> EligibleNics()
    {
        var found = new List<LanNic>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var mac = ni.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length != 6 || mac.All(b => b == 0)) continue;
            var props = ni.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is not { } mask) continue;
                if (!IsEligible(ni.NetworkInterfaceType, ni.Description + " " + ni.Name, hasGateway, ua.Address, mask)) continue;
                found.Add(new LanNic(ni.Name, ua.Address, SubnetBroadcast(ua.Address, mask), mac, ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211));
            }
        }
        return found;
    }

    private static IPAddress SubnetBroadcast(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        for (var i = 0; i < 4; i++) a[i] |= (byte)~m[i];
        return new IPAddress(a);
    }

    /// <summary>Sends the packet for the shortcut's arguments. Returns an error message, or null once at least one packet left.</summary>
    public static string? Wake(string? args, Action<IPAddress, IPEndPoint, byte[]>? send = null) => Wake(args, EligibleNics(), send ?? SendUdp);

    internal static string? Wake(string? args, IReadOnlyList<LanNic> nics, Action<IPAddress, IPEndPoint, byte[]> send)
    {
        if (ParseArgs(args, out var mac, out var broadcast) is { } bad) return bad;
        if (nics.Count == 0) return "no network interface to send from (a private LAN address is needed; Tailscale and VPN adapters do not count)";
        var packet = BuildPacket(mac);
        var delivered = false;
        string? firstError = null;
        for (var round = 0; round < Repeats; round++)
        {
            if (round > 0) Thread.Sleep(RepeatDelayMs);
            foreach (var nic in nics)
            {
                var targets = broadcast is not null ? [broadcast] : new[] { nic.Broadcast, IPAddress.Broadcast };
                foreach (var target in targets)
                {
                    try { send(nic.Address, new IPEndPoint(target, Port), packet); delivered = true; }
                    catch (Exception ex) { firstError ??= ex.Message; }
                }
            }
        }
        return delivered ? null : firstError ?? "no packet could be sent";
    }

    private static void SendUdp(IPAddress from, IPEndPoint to, byte[] packet)
    {
        using var udp = new UdpClient(new IPEndPoint(from, 0)) { EnableBroadcast = true };
        udp.Send(packet, packet.Length, to);
    }

    /// <summary>This computer's own MACs, formatted, for the phone: wired adapters first, no duplicates, at most 4.</summary>
    public static IReadOnlyList<string> LocalMacs() => LocalMacs(EligibleNics());

    internal static IReadOnlyList<string> LocalMacs(IEnumerable<LanNic> nics) =>
        nics.OrderByDescending(n => n.Wired).Select(n => FormatMac(n.Mac)).Distinct().Take(MaxReportedMacs).ToList();
}
