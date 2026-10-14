using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed partial class NetworkProvider : IInfoProvider
{
    public string Id => "network";
    public string Title => "Network";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 60;
    public string Icon => "";

    private sealed record Wifi(string? Ssid, string? Bssid, string? Phy, uint Signal, int? Rssi, uint? RxMbps, uint? TxMbps,
        uint? Channel, string? Band, string? Auth, string? Cipher);

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var adapters = Hw.NetAdapters();
        var configs = new Dictionary<string, WmiRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Wmi.Query("SELECT SettingID, DHCPEnabled, DHCPServer, DHCPLeaseObtained, DHCPLeaseExpires " +
                                    "FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
            if (r.Str("SettingID") is { } id) configs.TryAdd(id, r);
        var wifi = new Dictionary<string, Wifi>(StringComparer.OrdinalIgnoreCase);
        b.Try("Wi-Fi", () => wifi = WifiConnections());

        var all = Interfaces(adapters)
            .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
            .ThenByDescending(n => adapters?.GetValueOrDefault(n.Id) == true)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var n in all.Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            ct.ThrowIfCancellationRequested();
            b.Try(n.Name, () => AddAdapter(b, n, adapters?.GetValueOrDefault(n.Id), configs.GetValueOrDefault(n.Id), wifi.GetValueOrDefault(n.Id)));
        }

        b.Try("Other adapters", () =>
        {
            var t = b.Table("Other adapters", "Name", "Description", "Type", "Status", "MAC address");
            foreach (var n in all.Where(n => n.OperationalStatus != OperationalStatus.Up))
                t.Row(n.Name, n.Description, TypeName(n.NetworkInterfaceType), StatusName(n.OperationalStatus),
                    Hw.Mac(n.GetPhysicalAddress().GetAddressBytes()));
        });

        return b.Build();
    }

    // Network interfaces worth showing. GetAllNetworkInterfaces also returns hidden WAN miniports and one
    // pseudo-interface per NDIS filter driver ("Ethernet-QoS Packet Scheduler-0000"), which are dropped.
    internal static IEnumerable<NetworkInterface> Interfaces(Dictionary<string, bool>? adapters) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(n => adapters?.ContainsKey(n.Id)
                        ?? !(n.Description.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) || FilterSuffix().IsMatch(n.Description)));

    private static void AddAdapter(PageBuilder b, NetworkInterface n, bool? physical, WmiRow? cfg, Wifi? wifi)
    {
        var g = b.Group(n.Name, localizeTitle: false);
        g.Add("Description", n.Description);
        g.Add("Type", TypeName(n.NetworkInterfaceType));
        g.Word("Status", StatusName(n.OperationalStatus));
        g.Flag("Physical adapter", physical);
        if (n.Speed > 0) g.Add("Link speed", Fmt.BitRate(n.Speed));
        g.Add("MAC address", Hw.Mac(n.GetPhysicalAddress().GetAddressBytes()));

        if (wifi != null)
        {
            g.Add("SSID", wifi.Ssid);
            g.Add("BSSID", wifi.Bssid);
            g.Add("Wi-Fi standard", wifi.Phy);
            g.Add("Band", wifi.Band);
            g.Add("Channel", wifi.Channel);
            g.Add("Signal", wifi.Rssi is { } rssi ? $"{wifi.Signal} % ({rssi} dBm)" : $"{wifi.Signal} %");
            if (wifi.RxMbps is > 0) g.Add("Receive rate", $"{wifi.RxMbps} {Fmt.Unit("Mbps")}");
            if (wifi.TxMbps is > 0) g.Add("Transmit rate", $"{wifi.TxMbps} {Fmt.Unit("Mbps")}");
            g.Add("Authentication", wifi.Auth);
            g.Add("Encryption", wifi.Cipher);
        }

        var ip = n.GetIPProperties();
        g.Add("IPv4 address", Hw.Join(ip.UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => $"{a.Address}/{a.PrefixLength}")));
        g.Add("IPv6 address", Hw.Join(ip.UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6)
            .Select(a => $"{a.Address}/{a.PrefixLength}")));
        g.Add("Gateway", Hw.Join(ip.GatewayAddresses.Select(a => a.Address.ToString()).Where(a => a != "0.0.0.0")));
        g.Add("DNS servers", Hw.Join(ip.DnsAddresses.Select(a => a.ToString())));
        g.Add("DNS suffix", ip.DnsSuffix);

        var v4 = n.Supports(NetworkInterfaceComponent.IPv4) ? ip.GetIPv4Properties() : null;
        g.Flag("DHCP enabled", v4?.IsDhcpEnabled ?? cfg?.Bool("DHCPEnabled"));
        if (cfg?.Bool("DHCPEnabled") == true)
        {
            g.Add("DHCP server", cfg.Str("DHCPServer"));
            var obtained = cfg.Date("DHCPLeaseObtained");
            var expires = cfg.Date("DHCPLeaseExpires");
            g.Add("Lease obtained", Fmt.DateTime(obtained));
            // Very long leases overflow in WMI and come back as dates in the past.
            if (expires > obtained) g.Add("Lease expires", Fmt.DateTime(expires));
        }
        if (v4 is { Mtu: > 0 }) g.Add("MTU", v4.Mtu);

        var stats = n.GetIPStatistics();
        g.Add("Bytes received", Fmt.Bytes((double)stats.BytesReceived));
        g.Add("Bytes sent", Fmt.Bytes((double)stats.BytesSent));
    }

    private static string TypeName(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ppp => "PPP",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile broadband",
        (NetworkInterfaceType)53 => "Virtual",
        _ when Enum.IsDefined(t) => t.ToString(),
        _ => "Other"
    };

    private static string StatusName(OperationalStatus s) => s switch
    {
        OperationalStatus.Up => "Connected",
        OperationalStatus.Down => "Disconnected",
        OperationalStatus.NotPresent => "Not present",
        OperationalStatus.Dormant => "Dormant",
        OperationalStatus.LowerLayerDown => "Lower layer down",
        OperationalStatus.Testing => "Testing",
        _ => "Unknown"
    };

    [GeneratedRegex(@"-\d{4}$")]
    private static partial Regex FilterSuffix();

    // WLAN_CONNECTION_ATTRIBUTES and WLAN_BSS_ENTRY are read by offset to avoid large nested marshalling structs.
    private static Dictionary<string, Wifi> WifiConnections()
    {
        var result = new Dictionary<string, Wifi>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Native.WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return result;
            try
            {
                if (Native.WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return result;
                try
                {
                    var count = Marshal.ReadInt32(list);
                    for (var i = 0; i < count; i++)
                    {
                        var item = list + 8 + i * 532; // WLAN_INTERFACE_INFO: GUID, WCHAR[256], state
                        var guid = Marshal.PtrToStructure<Guid>(item);
                        if (Marshal.ReadInt32(item + 528) != 1) continue; // wlan_interface_state_connected
                        if (Query(handle, guid) is { } w) result["{" + guid.ToString().ToUpperInvariant() + "}"] = w;
                    }
                }
                finally { Native.WlanFreeMemory(list); }
            }
            finally { Native.WlanCloseHandle(handle, IntPtr.Zero); }
        }
        catch (DllNotFoundException) { }
        return result;
    }

    private static Wifi? Query(IntPtr handle, Guid guid)
    {
        if (Native.WlanQueryInterface(handle, ref guid, 7, IntPtr.Zero, out _, out var p, IntPtr.Zero) != 0) return null;
        try
        {
            const int assoc = 520;
            var ssidLen = Math.Min(Marshal.ReadInt32(p + assoc), 32);
            var ssidBytes = new byte[ssidLen];
            Marshal.Copy(p + assoc + 4, ssidBytes, 0, ssidLen);
            var bssidBytes = new byte[6];
            Marshal.Copy(p + assoc + 40, bssidBytes, 0, 6);
            var phy = Marshal.ReadInt32(p + assoc + 48);
            var signal = (uint)Marshal.ReadInt32(p + assoc + 56);
            var rx = (uint)Marshal.ReadInt32(p + assoc + 60) / 1000;
            var tx = (uint)Marshal.ReadInt32(p + assoc + 64) / 1000;
            var auth = Marshal.ReadInt32(p + 596);
            var cipher = Marshal.ReadInt32(p + 600);

            uint? channel = null;
            if (Native.WlanQueryInterface(handle, ref guid, 8, IntPtr.Zero, out _, out var ch, IntPtr.Zero) == 0)
            {
                channel = (uint)Marshal.ReadInt32(ch);
                Native.WlanFreeMemory(ch);
            }

            var (freqKhz, rssi) = BssInfo(handle, guid, bssidBytes);
            var band = freqKhz switch
            {
                >= 5925000 => "6 GHz",
                >= 4900000 => "5 GHz",
                >= 2400000 and < 2500000 => "2.4 GHz",
                _ => channel is >= 1 and <= 14 ? "2.4 GHz" : channel is >= 32 and <= 177 ? "5 GHz" : null
            };

            return new Wifi(ssidLen > 0 ? Encoding.UTF8.GetString(ssidBytes) : null,
                bssidBytes.Any(x => x != 0) ? BitConverter.ToString(bssidBytes).Replace('-', ':') : null,
                PhyName(phy), signal, rssi, rx, tx, channel, band, AuthName(auth), CipherName(cipher));
        }
        finally { Native.WlanFreeMemory(p); }
    }

    private static (uint FreqKhz, int? Rssi) BssInfo(IntPtr handle, Guid guid, byte[] bssid)
    {
        if (Native.WlanGetNetworkBssList(handle, ref guid, IntPtr.Zero, 1, false, IntPtr.Zero, out var list) != 0) return (0, null);
        try
        {
            var count = Marshal.ReadInt32(list + 4);
            var mac = new byte[6];
            for (var i = 0; i < count; i++)
            {
                var e = list + 8 + i * 360;
                Marshal.Copy(e + 40, mac, 0, 6);
                if (!mac.AsSpan().SequenceEqual(bssid)) continue;
                return ((uint)Marshal.ReadInt32(e + 92), Marshal.ReadInt32(e + 56));
            }
        }
        finally { Native.WlanFreeMemory(list); }
        return (0, null);
    }

    private static string? PhyName(int phy) => phy switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "802.11n (Wi-Fi 4)",
        8 => "802.11ac (Wi-Fi 5)",
        9 => "802.11ad (WiGig)",
        10 => "802.11ax (Wi-Fi 6)",
        11 => "802.11be (Wi-Fi 7)",
        _ => null
    };

    private static string? AuthName(int a) => a switch
    {
        1 => "Open",
        2 => "Shared key",
        3 => "WPA-Enterprise",
        4 => "WPA-Personal",
        6 => "WPA2-Enterprise",
        7 => "WPA2-Personal",
        8 => "WPA3-Enterprise 192-bit",
        9 => "WPA3-Personal",
        10 => "OWE",
        11 => "WPA3-Enterprise",
        _ => null
    };

    private static string? CipherName(int c) => c switch
    {
        0 => "None",
        1 => "WEP-40",
        2 => "TKIP",
        4 => "CCMP (AES)",
        5 => "WEP-104",
        8 => "GCMP",
        9 => "GCMP-256",
        10 => "CCMP-256",
        0x101 => "WEP",
        _ => null
    };
}
