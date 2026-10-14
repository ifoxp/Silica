using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

// Lookups and decoders shared by the hardware pages.
internal static partial class Hw
{
    public const string WmiNs = @"root\wmi";
    public const string StorageNs = @"root\Microsoft\Windows\Storage";
    public const string AdminHint = "Run as administrator for drive health data";

    public static string? OsName()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = Reg.Str(Registry.LocalMachine, key, "ProductName");
        if (product == null) return null;
        var build = Reg.Str(Registry.LocalMachine, key, "CurrentBuildNumber");
        if (int.TryParse(build, out var b) && b >= 22000)
            product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        var display = Reg.Str(Registry.LocalMachine, key, "DisplayVersion") ?? Reg.Str(Registry.LocalMachine, key, "ReleaseId");
        var ubr = Reg.Long(Registry.LocalMachine, key, "UBR");
        var sb = new StringBuilder(product);
        if (display != null) sb.Append(' ').Append(display);
        if (build != null) sb.Append(" (build ").Append(build).Append(ubr is { } u ? "." + u : "").Append(')');
        return sb.ToString();
    }

    public static string? MemoryType(int? smbios) => smbios switch
    {
        18 => "DDR",
        19 => "DDR2",
        20 => "DDR2 FB-DIMM",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        32 => "HBM",
        33 => "HBM2",
        34 => "DDR5",
        35 => "LPDDR5",
        36 => "HBM3",
        _ => null
    };

    public static string? MemoryFormFactor(int? f) => f switch
    {
        8 => "DIMM",
        12 => "SO-DIMM",
        13 => "RIMM",
        11 => "Row of chips",
        9 => "TSOP",
        _ => null
    };

    // JEDEC JEP106 codes as reported in Win32_PhysicalMemory.Manufacturer when the BIOS leaves them numeric.
    private static readonly Dictionary<string, string> Jedec = new(StringComparer.OrdinalIgnoreCase)
    {
        ["80CE"] = "Samsung", ["CE00"] = "Samsung", ["80AD"] = "SK hynix", ["AD00"] = "SK hynix",
        ["802C"] = "Micron", ["2C00"] = "Micron", ["859B"] = "Crucial", ["9B05"] = "Crucial",
        ["04CD"] = "G.Skill", ["CD04"] = "G.Skill", ["0198"] = "Kingston", ["9801"] = "Kingston",
        ["029E"] = "Corsair", ["9E02"] = "Corsair", ["04CB"] = "ADATA", ["CB04"] = "ADATA",
        ["04EF"] = "Team Group", ["EF04"] = "Team Group", ["8502"] = "Patriot", ["0285"] = "Patriot",
        ["8551"] = "Qimonda", ["80C2"] = "Kingmax", ["0B83"] = "Ramaxel", ["830B"] = "Nanya", ["0B03"] = "Nanya"
    };

    public static string? MemoryVendor(string? raw)
    {
        if (raw == null || raw.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return null;
        var hex = raw.Replace(" ", "");
        if (!HexOnly().IsMatch(hex)) return raw;
        return hex.Length >= 4 && Jedec.TryGetValue(hex[..4], out var name) ? name : raw;
    }

    public static string? ChassisType(int? t) => t switch
    {
        3 => "Desktop",
        4 => "Low profile desktop",
        5 => "Pizza box",
        6 => "Mini tower",
        7 => "Tower",
        8 => "Portable",
        9 => "Laptop",
        10 => "Notebook",
        11 => "Hand held",
        12 => "Docking station",
        13 => "All in one",
        14 => "Sub notebook",
        15 => "Space-saving",
        16 => "Lunch box",
        17 => "Main system chassis",
        23 => "Rack mount chassis",
        24 => "Sealed-case PC",
        30 => "Tablet",
        31 => "Convertible",
        32 => "Detachable",
        33 => "IoT gateway",
        34 => "Embedded PC",
        35 => "Mini PC",
        36 => "Stick PC",
        _ => null
    };

    // Decodes the zero-padded ushort arrays used by WmiMonitorID.
    public static string? WmiChars(object? value)
    {
        if (value is not Array arr) return null;
        var sb = new StringBuilder();
        foreach (var o in arr)
        {
            var c = Convert.ToInt32(o, CultureInfo.InvariantCulture);
            if (c == 0) break;
            sb.Append((char)c);
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }

    private static readonly Dictionary<string, string> PnpVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACI"] = "ASUS", ["AUS"] = "ASUS", ["ACR"] = "Acer", ["AOC"] = "AOC", ["APP"] = "Apple", ["AUO"] = "AU Optronics",
        ["BNQ"] = "BenQ", ["BOE"] = "BOE", ["CMN"] = "Innolux", ["DEL"] = "Dell", ["EIZ"] = "EIZO", ["ENC"] = "EIZO",
        ["GBT"] = "Gigabyte", ["GSM"] = "LG", ["HPN"] = "HP", ["HWP"] = "HP", ["IVM"] = "iiyama", ["LEN"] = "Lenovo",
        ["LGD"] = "LG Display", ["MSI"] = "MSI", ["MEI"] = "Panasonic", ["NEC"] = "NEC", ["PHL"] = "Philips",
        ["SAM"] = "Samsung", ["SDC"] = "Samsung Display", ["SEC"] = "Samsung", ["SHP"] = "Sharp", ["SNY"] = "Sony",
        ["VSC"] = "ViewSonic", ["XMI"] = "Xiaomi", ["HSD"] = "HannStar", ["CSO"] = "CSOT", ["IVO"] = "InfoVision",
        ["MED"] = "Medion", ["FUS"] = "Fujitsu", ["TSB"] = "Toshiba", ["HKC"] = "HKC", ["KTC"] = "KTC", ["TCL"] = "TCL"
    };

    public static string? PnpVendor(string? code) =>
        code == null ? null : PnpVendors.TryGetValue(code, out var name) ? $"{name} ({code})" : code;

    public static string? VideoOutput(long? tech) => tech switch
    {
        null or -2 => null,
        -1 => "Other",
        0 => "VGA",
        1 => "S-Video",
        2 => "Composite",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        8 => "D-Jpn",
        9 => "SDI",
        10 => "DisplayPort",
        11 => "Embedded DisplayPort",
        12 => "UDI",
        13 => "Embedded UDI",
        14 => "SDTV dongle",
        15 => "Miracast",
        16 => "Indirect wired",
        17 => "Indirect virtual",
        0x80000000L or int.MinValue => "Internal",
        _ => null
    };

    public static string? DiskMedia(int? m) => m switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => null
    };

    public static string? DiskBus(int? b) => b switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => null
    };

    public static (string? Vendor, string? Device, string? Subsys, string? Rev) PciIds(string? pnpId)
    {
        if (pnpId == null) return default;
        static string? G(Match m) => m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        return (G(VenRegex().Match(pnpId)), G(DevRegex().Match(pnpId)), G(SubsysRegex().Match(pnpId)), G(RevRegex().Match(pnpId)));
    }

    public static string? UsbVidPid(string? pnpId)
    {
        if (pnpId == null) return null;
        var m = VidPidRegex().Match(pnpId);
        return m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}" : null;
    }

    // Path of the driver key under HKLM\SYSTEM\CurrentControlSet\Control\Class for a PnP device.
    public static string? DriverKey(string? pnpId)
    {
        if (pnpId == null) return null;
        var driver = Reg.Str(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Enum\" + pnpId, "Driver");
        return driver == null ? null : @"SYSTEM\CurrentControlSet\Control\Class\" + driver;
    }

    public static string? EnumValue(string? pnpId, string name) =>
        pnpId == null ? null : Reg.Str(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Enum\" + pnpId, name);

    // Device location such as "PCI bus 1, device 0, function 0". The registry often holds an indirect string
    // ("@pci.sys,#65536;PCI bus %1, device %2, function %3;(1,0,0)") that is expanded here.
    public static string? DeviceLocation(string? pnpId)
    {
        var raw = EnumValue(pnpId, "LocationInformation");
        if (raw == null || !raw.StartsWith('@')) return raw;
        var parts = raw.Split(';');
        if (parts.Length < 3) return null;
        var text = parts[1];
        var args = parts[2].Trim('(', ')').Split(',');
        for (var i = args.Length; i >= 1; i--) text = text.Replace("%" + i, args[i - 1].Trim(), StringComparison.Ordinal);
        return text;
    }

    // Dedicated video memory from the display driver key (Win32_VideoController.AdapterRAM caps at 4 GB).
    // Drivers report usable VRAM, slightly below the nominal size (11.94 GB on a 12 GB card), so a gap of up to
    // 128 MB is rounded up to the next 512 MB step.
    public static ulong? GpuMemory(string? pnpId, long? adapterRam)
    {
        var bytes = RawGpuMemory(pnpId) ?? (adapterRam is > 0 ? (ulong)adapterRam.Value : 0);
        if (bytes == 0) return null;
        const ulong step = 512UL << 20;
        var up = (bytes + step - 1) / step * step;
        return up - bytes <= 128UL << 20 ? up : bytes;
    }

    private static ulong? RawGpuMemory(string? pnpId)
    {
        var key = DriverKey(pnpId);
        if (key == null) return null;
        return (Reg.Get(Registry.LocalMachine, key, "HardwareInformation.qwMemorySize")
                ?? Reg.Get(Registry.LocalMachine, key, "HardwareInformation.MemorySize")) switch
        {
            long l when l > 0 => (ulong)l,
            int i when i != 0 => (uint)i,
            byte[] { Length: >= 8 } b => BitConverter.ToUInt64(b),
            byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b),
            _ => null
        };
    }

    // Registry values that drivers store either as strings or as UTF-16 binary blobs.
    public static string? RegText(object? value) => value switch
    {
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        string[] a => string.Join(", ", a.Where(x => !string.IsNullOrWhiteSpace(x))),
        byte[] b => Encoding.Unicode.GetString(b).TrimEnd('\0', ' ') is { Length: > 0 } t ? t : null,
        _ => null
    };

    public static bool IsVirtualGpu(string? pnpId) =>
        pnpId == null || !pnpId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase);

    // Visible network adapters keyed by interface GUID (as in NetworkInterface.Id), with a flag for physical hardware.
    // Hidden WAN miniports and NDIS filter interfaces are not included. Null when the adapter list is unavailable.
    public static Dictionary<string, bool>? NetAdapters()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Wmi.Query("SELECT InterfaceGuid, HardwareInterface FROM MSFT_NetAdapter", @"root\StandardCimv2"))
            if (r.Str("InterfaceGuid") is { } g) map[g] = r.Bool("HardwareInterface") == true;
        if (map.Count > 0) return map;

        // Win32_NetworkAdapter.PhysicalAdapter is also true for many VPN drivers, so require a hardware bus too.
        foreach (var r in Wmi.Query("SELECT GUID, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
            if (r.Str("GUID") is { } g && r.Str("PNPDeviceID") is { } pnp)
                map[g] = pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
        return map.Count > 0 ? map : null;
    }

    public static string Mac(byte[] bytes) => bytes.Length == 0 ? "" : BitConverter.ToString(bytes);

    public static string Resolution(long w, long h) => $"{w} × {h}";

    public static string Join(IEnumerable<string?> values, string separator = ", ") =>
        string.Join(separator, values.Where(v => !string.IsNullOrWhiteSpace(v)));

    [GeneratedRegex("^[0-9A-Fa-f]+$")] private static partial Regex HexOnly();
    [GeneratedRegex(@"VEN_([0-9A-Fa-f]{4})")] private static partial Regex VenRegex();
    [GeneratedRegex(@"DEV_([0-9A-Fa-f]{4})")] private static partial Regex DevRegex();
    [GeneratedRegex(@"SUBSYS_([0-9A-Fa-f]{8})")] private static partial Regex SubsysRegex();
    [GeneratedRegex(@"REV_([0-9A-Fa-f]{2})")] private static partial Regex RevRegex();
    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})")] private static partial Regex VidPidRegex();
}
