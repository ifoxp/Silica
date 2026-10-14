using System.Text.RegularExpressions;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed partial class InputProvider : IInfoProvider
{
    public string Id => "input";
    public string Title => "Input devices";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 80;
    public string Icon => "";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var hid = Wmi.Query("SELECT Name, Manufacturer, Status, PNPDeviceID, PNPClass FROM Win32_PnPEntity " +
                            "WHERE PNPClass = 'HIDClass' OR PNPClass = 'XnaComposite' OR PNPClass = 'XboxComposite' OR PNPClass = 'Bluetooth'");

        b.Try("Touch", () =>
        {
            // SM_DIGITIZER / SM_MAXIMUMTOUCHES
            var digitizer = Native.GetSystemMetrics(94);
            var g = b.Group("Touch and pen");
            g.Flag("Touch support", (digitizer & 0x03) != 0);
            g.Flag("Pen support", (digitizer & 0x0C) != 0);
            if (Native.GetSystemMetrics(95) is > 0 and var touches) g.Add("Touch points", touches);
        });

        b.Try("Keyboards", () =>
        {
            // Win32_Keyboard.Name is the keyboard type ("Enhanced (101- or 102-key)"); Description is the device name.
            var t = b.Table("Keyboards", "Name", "Type", "Function keys", "Status", "Device ID");
            foreach (var r in Wmi.Query("SELECT Name, Description, NumberOfFunctionKeys, Status, PNPDeviceID FROM Win32_Keyboard"))
                t.Row(r.Str("Description") ?? r.Str("Name"), r.Str("Name"),
                    r.Int("NumberOfFunctionKeys") is > 0 and var fk ? fk : null, r.Str("Status"), r.Str("PNPDeviceID"));
        });

        b.Try("Pointing devices", () =>
        {
            var t = b.Table("Pointing devices", "Name", "Manufacturer", "Type", "Interface", "Buttons", "Status", "Device ID");
            foreach (var r in Wmi.Query("SELECT Name, Manufacturer, PointingType, DeviceInterface, NumberOfButtons, HardwareType, " +
                                        "Status, PNPDeviceID FROM Win32_PointingDevice"))
                t.Row(r.Str("Name"), r.Str("Manufacturer"), PointingType(r.Int("PointingType")),
                    Interface(r.Int("DeviceInterface"), r.Str("PNPDeviceID")), r.Int("NumberOfButtons") is > 0 and var n ? n : null,
                    r.Str("Status"), r.Str("PNPDeviceID"));
        });

        b.Try("Game controllers", () =>
        {
            var t = b.Table("Game controllers", "Name", "Manufacturer", "Status", "Device ID");
            foreach (var r in hid)
            {
                var name = r.Str("Name") ?? "";
                var id = r.Str("PNPDeviceID") ?? "";
                // "IG_" marks the XInput interface of wired Xbox-compatible pads.
                if (GamepadRegex().IsMatch(name) || id.Contains("IG_", StringComparison.OrdinalIgnoreCase))
                    t.Row(name, r.Str("Manufacturer"), r.Str("Status"), id);
            }
        });

        b.Try("Touch devices", () =>
        {
            var t = b.Table("Touch and pen devices", "Name", "Manufacturer", "Status", "Device ID");
            foreach (var r in hid.Where(r => r.Str("PNPClass") == "HIDClass"))
            {
                var name = r.Str("Name") ?? "";
                if (TouchRegex().IsMatch(name))
                    t.Row(name, r.Str("Manufacturer"), r.Str("Status"), r.Str("PNPDeviceID"));
            }
        });

        return b.Build();
    }

    private static string? PointingType(int? t) => t switch
    {
        4 => "Mouse",
        5 => "Trackball",
        6 => "Track point",
        7 => "Glide point",
        8 => "Touchpad",
        9 => "Touch screen",
        10 => "Optical mouse",
        _ => null
    };

    private static string? Interface(int? i, string? pnpId) => i switch
    {
        3 => "Serial",
        4 => "PS/2",
        5 => "Infrared",
        7 => "Bus mouse",
        8 => "ADB",
        162 => "USB",
        _ => pnpId switch
        {
            null => null,
            _ when pnpId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) && pnpId.Contains("VID_", StringComparison.OrdinalIgnoreCase) => "USB HID",
            _ when pnpId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) => "HID",
            _ when pnpId.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) => "PS/2",
            _ => null
        }
    };

    [GeneratedRegex(@"touch ?screen|touch ?pad|precision touch|\bpen\b|digitizer", RegexOptions.IgnoreCase)]
    private static partial Regex TouchRegex();

    [GeneratedRegex(@"game ?controller|gamepad|joystick|xinput|xbox|wireless controller|dualsense|dualshock", RegexOptions.IgnoreCase)]
    private static partial Regex GamepadRegex();
}
