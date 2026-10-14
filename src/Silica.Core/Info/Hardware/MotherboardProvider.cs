using System.Runtime.InteropServices;
using Microsoft.Win32;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed class MotherboardProvider : IInfoProvider
{
    public string Id => "motherboard";
    public string Title => "Motherboard";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 20;
    public string Icon => "";

    private static readonly string[] ChipsetKeywords =
    [
        "SMBus", "LPC", "eSPI", "Chipset", "PCI Express Root", "Host Bridge", "host CPU bridge", "ISA bridge",
        "Root Complex", "SPI (flash)", "Management Engine", "PSP"
    ];

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);

        b.Try("System", () =>
        {
            var cs = Wmi.First("SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber FROM Win32_ComputerSystem");
            var csp = Wmi.First("SELECT UUID, IdentifyingNumber, Version FROM Win32_ComputerSystemProduct");
            var enc = Wmi.First("SELECT ChassisTypes, SMBIOSAssetTag FROM Win32_SystemEnclosure");
            var g = b.Group("System");
            g.Add("Manufacturer", Clean(cs?.Str("Manufacturer")));
            g.Add("Model", Clean(cs?.Str("Model")));
            g.Add("Family", Clean(cs?.Str("SystemFamily")));
            g.Add("SKU", Clean(cs?.Str("SystemSKUNumber")));
            g.Add("Version", Clean(csp?.Str("Version")));
            g.Add("Serial number", Clean(csp?.Str("IdentifyingNumber")));
            g.Add("UUID", csp?.Str("UUID"));
            var chassis = enc?.Array<ushort>("ChassisTypes")?.Select(c => Hw.ChassisType(c)).FirstOrDefault(c => c != null);
            g.Word("Chassis type", chassis);
            g.Add("Asset tag", Clean(enc?.Str("SMBIOSAssetTag")));
        });

        b.Try("Motherboard", () =>
        {
            var bb = Wmi.First("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard");
            var g = b.Group("Motherboard");
            g.Add("Manufacturer", Clean(bb?.Str("Manufacturer")));
            g.Add("Product", Clean(bb?.Str("Product")));
            g.Add("Version", Clean(bb?.Str("Version")));
            g.Add("Serial number", Clean(bb?.Str("SerialNumber")));
        });

        b.Try("BIOS", () =>
        {
            var bios = Wmi.First("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion, " +
                                 "SystemBiosMajorVersion, SystemBiosMinorVersion, EmbeddedControllerMajorVersion, " +
                                 "EmbeddedControllerMinorVersion FROM Win32_BIOS");
            var g = b.Group("BIOS");
            g.Add("Vendor", bios?.Str("Manufacturer"));
            g.Add("Version", bios?.Str("SMBIOSBIOSVersion"));
            g.Add("Release date", Fmt.Date(bios?.Date("ReleaseDate")));
            if (bios?.Int("SystemBiosMajorVersion") is { } maj and not 255)
                g.Add("BIOS revision", $"{maj}.{bios.Int("SystemBiosMinorVersion")}");
            if (bios?.Int("EmbeddedControllerMajorVersion") is { } ec and not 255)
                g.Add("Embedded controller", $"{ec}.{bios.Int("EmbeddedControllerMinorVersion")}");
            if (bios?.Int("SMBIOSMajorVersion") is { } sm)
                g.Add("SMBIOS version", $"{sm}.{bios.Int("SMBIOSMinorVersion")}");
            g.Word("BIOS mode", FirmwareType());
            g.Word("Secure Boot", SecureBoot());
            g.Add("TPM", Tpm());
        });

        b.Try("Chipset devices", () =>
        {
            var t = b.Table("Chipset devices", "Name", "Manufacturer", "Vendor ID", "Device ID", "Count", "Status");
            var rows = Wmi.Query("SELECT Name, Manufacturer, PNPDeviceID, Status FROM Win32_PnPEntity WHERE PNPClass = 'System'")
                .Where(r => r.Str("Name") is { } n && ChipsetKeywords.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .Where(r => r.Str("PNPDeviceID")?.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) == true)
                .Select(r => (Name: r.Str("Name"), Maker: r.Str("Manufacturer"), Ids: Hw.PciIds(r.Str("PNPDeviceID")), Status: r.Str("Status")))
                .GroupBy(r => (r.Name, r.Maker, r.Ids.Vendor, r.Ids.Device, r.Status))
                .OrderBy(g => g.Key.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Key.Device, StringComparer.Ordinal);
            foreach (var g in rows)
                t.Row(g.Key.Name, g.Key.Maker, g.Key.Vendor, g.Key.Device, g.Count(), g.Key.Status);
        });

        return b.Build();
    }

    // OEM placeholders that carry no information.
    private static string? Clean(string? s) => s is null
        || s.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase)
        || s.Equals("Default string", StringComparison.OrdinalIgnoreCase)
        || s.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)
        || s.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase)
        || s.Equals("System Version", StringComparison.OrdinalIgnoreCase)
        || s.Equals("System Serial Number", StringComparison.OrdinalIgnoreCase)
        || s.Equals("Not Applicable", StringComparison.OrdinalIgnoreCase)
        || s.Equals("x.x", StringComparison.OrdinalIgnoreCase)
        || s.Trim('0', ' ', '-').Length == 0
        ? null : s;

    internal static string? FirmwareType()
    {
        try
        {
            if (!Native.GetFirmwareType(out var type)) return null;
            return type switch { 1 => "Legacy", 2 => "UEFI", _ => null };
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    internal static string? SecureBoot()
    {
        var v = Reg.Long(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        return v switch { 1 => "Enabled", 0 => "Disabled", _ => FirmwareType() == "Legacy" ? "Not supported" : null };
    }

    private static string? Tpm()
    {
        try
        {
            if (Native.Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<Native.TpmDeviceInfo>(), out var info) != 0) return null;
            return info.TpmVersion switch { 1 => "1.2", 2 => "2.0", _ => null };
        }
        catch (DllNotFoundException) { return null; }
    }
}
