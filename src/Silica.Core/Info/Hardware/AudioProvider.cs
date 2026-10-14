using System.Runtime.InteropServices;
using Microsoft.Win32;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed class AudioProvider : IInfoProvider
{
    public string Id => "audio";
    public string Title => "Audio";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 70;
    public string Icon => "";

    private const string MmDevices = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\";
    private const string NameKey = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    private const string DeviceKey = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
    private const string FormatKey = "{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0";

    internal sealed record Endpoint(bool Playback, string Id, string? Name, string? Device, int State, string? Format, bool IsDefault);

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);

        b.Try("Sound devices", () =>
        {
            var t = b.Table("Sound devices", "Name", "Manufacturer", "Status", "Device ID");
            foreach (var r in Wmi.Query("SELECT Name, Manufacturer, Status, PNPDeviceID FROM Win32_SoundDevice")
                         .OrderBy(r => r.Str("Name"), StringComparer.CurrentCultureIgnoreCase))
                t.Row(r.Str("Name"), r.Str("Manufacturer"), r.Str("Status"), r.Str("PNPDeviceID"));
        });

        b.Try("Audio endpoints", () =>
        {
            var endpoints = Endpoints();
            var active = endpoints.Where(e => e.State == 1).ToList();
            var g = b.Group("Default devices");
            g.Add("Playback", active.FirstOrDefault(e => e.Playback && e.IsDefault) is { } p ? Describe(p) : null);
            g.Add("Recording", active.FirstOrDefault(e => !e.Playback && e.IsDefault) is { } r ? Describe(r) : null);
            g.Add("Active playback devices", active.Count(e => e.Playback));
            g.Add("Active recording devices", active.Count(e => !e.Playback));

            // Endpoints of devices that are no longer present are leftovers; listing them only adds noise.
            var t = b.Table("Audio endpoints", "Type", "Name", "Device", "State", "Format", "Default");
            foreach (var e in endpoints.Where(e => e.State != 4).OrderBy(e => e.State != 1).ThenByDescending(e => e.Playback).ThenByDescending(e => e.IsDefault)
                         .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                t.Row(e.Playback ? "Playback" : "Recording", e.Name, e.Device, StateName(e.State), e.Format, e.IsDefault ? "Yes" : "");
        });

        return b.Build();
    }

    internal static string Describe(Endpoint e) => e.Device == null ? e.Name ?? e.Id : $"{e.Name} ({e.Device})";

    private static string StateName(int state) => state switch
    {
        1 => "Active",
        2 => "Disabled",
        4 => "Not present",
        8 => "Unplugged",
        _ => "Unknown"
    };

    internal static List<Endpoint> Endpoints()
    {
        var list = new List<Endpoint>();
        foreach (var playback in new[] { true, false })
        {
            var flow = playback ? "Render" : "Capture";
            var defaultId = DefaultEndpoint(playback ? 0 : 1);
            foreach (var id in Reg.SubKeys(Registry.LocalMachine, MmDevices + flow))
            {
                var path = MmDevices + flow + "\\" + id;
                // The upper bits of DeviceState hold flags; the low nibble is DEVICE_STATE_*.
                var state = (int)((Reg.Long(Registry.LocalMachine, path, "DeviceState") ?? 0) & 0xF);
                var props = path + @"\Properties";
                list.Add(new Endpoint(playback, id,
                    Reg.Str(Registry.LocalMachine, props, NameKey),
                    Reg.Str(Registry.LocalMachine, props, DeviceKey),
                    state,
                    Format(Reg.Get(Registry.LocalMachine, props, FormatKey) as byte[]),
                    defaultId != null && defaultId.EndsWith(id, StringComparison.OrdinalIgnoreCase)));
            }
        }
        return list;
    }

    // The value is a serialized PROPVARIANT blob: 8-byte header, then WAVEFORMATEX(TENSIBLE).
    private static string? Format(byte[]? blob)
    {
        if (blob is not { Length: >= 24 }) return null;
        var channels = BitConverter.ToUInt16(blob, 10);
        var rate = BitConverter.ToUInt32(blob, 12);
        var extensible = BitConverter.ToUInt16(blob, 8) == 0xFFFE && blob.Length >= 28;
        var bits = BitConverter.ToUInt16(blob, extensible ? 26 : 22);
        if (rate == 0 || channels == 0) return null;
        return $"{rate / 1000.0:0.#} kHz, {bits}-bit, {channels} ch";
    }

    private static string? DefaultEndpoint(int dataFlow)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(dataFlow, 0, out var device) != 0 || device == null) return null;
                try { return device.GetId(out var id) == 0 ? id : null; }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
        catch (COMException) { return null; }
        catch (InvalidCastException) { return null; }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
}
