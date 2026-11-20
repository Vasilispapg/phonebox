using System.Net;
using System.Net.NetworkInformation;
using PhoneBox.Agent;
using Xunit;

/// <summary>Wake-on-LAN: the packet, MAC parsing, adapter selection and the send loop (never a real network send).</summary>
public sealed class WakeOnLanTests
{
    private static readonly byte[] Mac = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x01];

    [Fact]
    public void Packet_is_six_ff_then_the_mac_sixteen_times()
    {
        var p = WakeOnLan.BuildPacket(Mac);
        Assert.Equal(102, p.Length);
        Assert.All(p.Take(6), b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++) Assert.Equal(Mac, p.Skip(6 + i * 6).Take(6).ToArray());
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:01")]
    [InlineData("aa-bb-cc-dd-ee-01")]
    [InlineData("AABBCCDDEE01")]
    [InlineData("  AA:BB:CC:DD:EE:01  ")]
    public void A_mac_is_accepted_in_every_common_format(string text)
    {
        Assert.True(WakeOnLan.TryParseMac(text, out var mac));
        Assert.Equal(Mac, mac);
        Assert.Equal("AA:BB:CC:DD:EE:01", WakeOnLan.FormatMac(mac));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("AA:BB:CC:DD:EE:01:02")]
    [InlineData("GG:BB:CC:DD:EE:01")]
    [InlineData("00:00:00:00:00:00")]
    [InlineData("FF:FF:FF:FF:FF:FF")]
    public void A_bad_mac_is_rejected(string? text) => Assert.False(WakeOnLan.TryParseMac(text, out _));

    [Fact]
    public void Args_are_a_mac_and_an_optional_broadcast_address()
    {
        Assert.Null(WakeOnLan.ParseArgs("AA:BB:CC:DD:EE:01", out var mac, out var none));
        Assert.Equal(Mac, mac);
        Assert.Null(none);
        Assert.Null(WakeOnLan.ParseArgs("AA:BB:CC:DD:EE:01 192.168.1.255", out _, out var bc));
        Assert.Equal(IPAddress.Parse("192.168.1.255"), bc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-mac")]
    public void Args_with_a_bad_mac_say_so(string args)
        => Assert.Contains("MAC", WakeOnLan.ParseArgs(args, out _, out _));

    [Fact]
    public void Args_with_a_bad_broadcast_address_say_so()
        => Assert.Contains("address", WakeOnLan.ParseArgs("AA:BB:CC:DD:EE:01 nope", out _, out _));

    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Fact]
    public void An_ethernet_adapter_with_a_private_address_is_eligible()
        => Assert.True(WakeOnLan.IsEligible(NetworkInterfaceType.Ethernet, "Intel I225-V", true, Ip("192.168.1.20"), Ip("255.255.255.0")));

    [Theory]
    [InlineData(NetworkInterfaceType.Loopback, "Loopback", true, "192.168.1.20", "255.255.255.0")]
    [InlineData(NetworkInterfaceType.Tunnel, "Teredo", true, "192.168.1.20", "255.255.255.0")]
    [InlineData(NetworkInterfaceType.Ppp, "VPN", true, "10.8.0.2", "255.255.255.0")]
    [InlineData(NetworkInterfaceType.Ethernet, "Tailscale", true, "100.101.1.2", "255.192.0.0")]
    [InlineData(NetworkInterfaceType.Ethernet, "Intel I225-V", true, "169.254.3.4", "255.255.0.0")]
    [InlineData(NetworkInterfaceType.Ethernet, "Intel I225-V", true, "10.0.0.5", "255.255.255.255")]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (WSL)", false, "172.20.0.1", "255.255.240.0")]
    public void Other_adapters_are_not_eligible(NetworkInterfaceType type, string desc, bool gateway, string address, string mask)
        => Assert.False(WakeOnLan.IsEligible(type, desc, gateway, Ip(address), Ip(mask)));

    [Fact]
    public void A_virtual_switch_that_owns_the_gateway_stays_eligible()
        => Assert.True(WakeOnLan.IsEligible(NetworkInterfaceType.Ethernet, "Hyper-V Virtual Ethernet Adapter", true, Ip("192.168.1.20"), Ip("255.255.255.0")));

    private static WakeOnLan.LanNic Nic(string addr, string bcast, bool wired = true)
        => new("eth", Ip(addr), Ip(bcast), Mac, wired);

    [Fact]
    public void Wake_with_no_eligible_adapter_is_an_error_not_a_silent_success()
    {
        var sent = 0;
        var err = WakeOnLan.Wake("AA:BB:CC:DD:EE:01", [], (_, _, _) => sent++);
        Assert.Contains("no network interface", err);
        Assert.Equal(0, sent);
    }

    [Fact]
    public void Wake_sends_three_packets_to_the_subnet_and_the_limited_broadcast_from_the_adapter()
    {
        var sends = new List<(IPAddress From, IPEndPoint To, byte[] Packet)>();
        var err = WakeOnLan.Wake("aa-bb-cc-dd-ee-01", [Nic("192.168.1.20", "192.168.1.255")], (f, t, p) => sends.Add((f, t, p)));
        Assert.Null(err);
        Assert.Equal(6, sends.Count);
        Assert.All(sends, s => Assert.Equal(Ip("192.168.1.20"), s.From));
        Assert.All(sends, s => Assert.Equal(9, s.To.Port));
        Assert.All(sends, s => Assert.Equal(WakeOnLan.BuildPacket(Mac), s.Packet));
        Assert.Equal(3, sends.Count(s => s.To.Address.Equals(Ip("192.168.1.255"))));
        Assert.Equal(3, sends.Count(s => s.To.Address.Equals(IPAddress.Broadcast)));
    }

    [Fact]
    public void A_broadcast_override_replaces_the_default_targets()
    {
        var sends = new List<IPEndPoint>();
        var err = WakeOnLan.Wake("AA:BB:CC:DD:EE:01 192.168.1.255", [Nic("192.168.1.20", "192.168.9.255")], (_, t, _) => sends.Add(t));
        Assert.Null(err);
        Assert.Equal(3, sends.Count);
        Assert.All(sends, t => Assert.Equal(Ip("192.168.1.255"), t.Address));
    }

    private static WakeOnLan.LanNic NicWithMac(byte last, bool wired)
        => new("n" + last, Ip("192.168.1.20"), Ip("192.168.1.255"), [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, last], wired);

    [Fact]
    public void Reported_macs_put_wired_adapters_first_without_duplicates_and_stop_at_four()
    {
        var nics = new[]
        {
            NicWithMac(1, wired: false), NicWithMac(2, wired: true), NicWithMac(2, wired: true), NicWithMac(3, wired: false),
            NicWithMac(4, wired: true), NicWithMac(5, wired: false), NicWithMac(6, wired: true),
        };
        Assert.Equal(["AA:BB:CC:DD:EE:02", "AA:BB:CC:DD:EE:04", "AA:BB:CC:DD:EE:06", "AA:BB:CC:DD:EE:01"], WakeOnLan.LocalMacs(nics));
    }

    [Fact]
    public void The_phone_may_ask_for_the_macs_over_wifi_but_not_for_anything_else_under_wake()
    {
        Assert.True(Lan.AllowedOverLan("/api/wake/macs"));
        Assert.False(Lan.AllowedOverLan("/api/wake"));
        Assert.False(Lan.AllowedOverLan("/api/wake/send"));
    }

    [Fact]
    public void A_failing_sender_is_reported()
    {
        var err = WakeOnLan.Wake("AA:BB:CC:DD:EE:01", [Nic("192.168.1.20", "192.168.1.255")], (_, _, _) => throw new System.Net.Sockets.SocketException());
        Assert.NotNull(err);
    }
}
