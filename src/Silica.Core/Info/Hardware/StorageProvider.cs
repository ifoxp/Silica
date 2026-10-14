using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed class StorageProvider : IInfoProvider
{
    public string Id => "storage";
    public string Title => "Storage";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 50;
    public string Icon => "";

    internal sealed record Disk(string DeviceId, string Name, ulong Size, int? Media, int? Bus, WmiRow Row);

    internal static List<Disk> Disks() =>
        Wmi.Query("SELECT DeviceId, FriendlyName, Model, MediaType, BusType, Size, FirmwareVersion, SerialNumber, HealthStatus, " +
                  "OperationalStatus, SpindleSpeed, LogicalSectorSize, PhysicalSectorSize FROM MSFT_PhysicalDisk", Hw.StorageNs)
            .Select(r => new Disk(r.Str("DeviceId") ?? "", r.Str("FriendlyName") ?? r.Str("Model") ?? "Disk",
                r.ULong("Size") ?? 0, r.Int("MediaType"), r.Int("BusType"), r))
            .OrderBy(d => int.TryParse(d.DeviceId, out var n) ? n : int.MaxValue)
            .ToList();

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var disks = Disks();
        var reliability = new Dictionary<string, WmiRow>();
        foreach (var r in Wmi.Query("SELECT * FROM MSFT_StorageReliabilityCounter", Hw.StorageNs))
            if (r.Str("DeviceId") is { } id) reliability.TryAdd(id, r);
        var diskInfo = new Dictionary<string, WmiRow>();
        foreach (var r in Wmi.Query("SELECT Number, PartitionStyle, IsBoot, IsSystem, NumberOfPartitions FROM MSFT_Disk", Hw.StorageNs))
            if (r.Str("Number") is { } n) diskInfo.TryAdd(n, r);

        foreach (var d in disks)
        {
            ct.ThrowIfCancellationRequested();
            b.Try(d.Name, () =>
            {
                var r = d.Row;
                var g = b.Group(d.Name, localizeTitle: false);
                g.Add("Disk number", d.DeviceId);
                g.Add("Capacity", d.Size > 0 ? $"{Fmt.DecimalBytes(d.Size)} ({Fmt.Bytes((double)d.Size)})" : null);
                g.Add("Media type", Hw.DiskMedia(d.Media));
                g.Add("Bus type", Hw.DiskBus(d.Bus));
                g.Add("Firmware", r.Str("FirmwareVersion"));
                g.Add("Serial number", r.Str("SerialNumber")?.TrimEnd('.'));
                g.Word("Health", r.Int("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => null });
                g.Word("Status", Operational(r.Array<ushort>("OperationalStatus")));
                if (r.Long("SpindleSpeed") is > 0 and < uint.MaxValue and var rpm) g.Add("Spindle speed", $"{rpm} RPM");
                g.Add("Logical sector size", r.Long("LogicalSectorSize") is > 0 and var ls ? $"{ls} B" : null);
                g.Add("Physical sector size", r.Long("PhysicalSectorSize") is > 0 and var ps ? $"{ps} B" : null);
                if (diskInfo.TryGetValue(d.DeviceId, out var di))
                {
                    g.Word("Partition style", di.Int("PartitionStyle") switch { 1 => "MBR", 2 => "GPT", 3 => "RAW", _ => null });
                    g.Flag("Boot disk", di.Bool("IsBoot"));
                    g.Flag("System disk", di.Bool("IsSystem"));
                }
                if (reliability.TryGetValue(d.DeviceId, out var rc)) AddReliability(g, rc);
            });
        }
        if (disks.Count > 0 && reliability.Count == 0) b.Warn(Hw.AdminHint);

        b.Try("Partitions", () =>
        {
            var t = b.Table("Partitions", "Disk", "Partition", "Letter", "Size", "Type", "Offset");
            var parts = Wmi.Query("SELECT DiskNumber, PartitionNumber, DriveLetter, Size, Offset, GptType, MbrType, IsActive " +
                                  "FROM MSFT_Partition", Hw.StorageNs)
                .OrderBy(p => p.Long("DiskNumber")).ThenBy(p => p.Long("PartitionNumber"));
            foreach (var p in parts)
            {
                var letter = p["DriveLetter"] is char c && char.IsLetter(c) ? c + ":" : "";
                t.Row(p.Long("DiskNumber"), p.Long("PartitionNumber"), letter, Fmt.Bytes(p.ULong("Size")),
                    PartitionType(p.Str("GptType"), p.Int("MbrType")), Fmt.Bytes(p.ULong("Offset")));
            }
        });

        b.Try("Volumes", () =>
        {
            var t = b.Table("Volumes", "Drive", "Label", "File system", "Type", "Total", "Free", "Used");
            foreach (var v in DriveInfo.GetDrives())
            {
                var type = v.DriveType switch
                {
                    DriveType.Fixed => "Fixed",
                    DriveType.Removable => "Removable",
                    DriveType.Network => "Network",
                    DriveType.CDRom => "Optical",
                    DriveType.Ram => "RAM disk",
                    _ => "Unknown"
                };
                // Network drives can block for a long time when the server is unreachable.
                if (v.DriveType == DriveType.Network || !v.IsReady)
                {
                    t.Row(v.Name.TrimEnd('\\'), "", "", type, "", "", "");
                    continue;
                }
                var used = v.TotalSize > 0 ? 100.0 * (v.TotalSize - v.TotalFreeSpace) / v.TotalSize : 0;
                t.Row(v.Name.TrimEnd('\\'), v.VolumeLabel, v.DriveFormat, type, Fmt.Bytes(v.TotalSize),
                    Fmt.Bytes(v.TotalFreeSpace), Fmt.Percent(used));
            }
        });

        b.Try("Optical drives", () =>
        {
            foreach (var r in Wmi.Query("SELECT Name, Drive, Manufacturer, MediaType, MediaLoaded, Status FROM Win32_CDROMDrive"))
            {
                var g = b.Group(r.Str("Name") ?? "Optical drive", localizeTitle: false);
                g.Add("Drive", r.Str("Drive"));
                g.Add("Manufacturer", r.Str("Manufacturer"));
                g.Add("Media type", r.Str("MediaType"));
                g.Flag("Media loaded", r.Bool("MediaLoaded"));
                g.Word("Status", r.Str("Status"));
            }
        });

        return b.Build();
    }

    private static void AddReliability(PropertyBuilder g, WmiRow r)
    {
        if (r.Long("Temperature") is > 0 and var temp) g.Add("Temperature", $"{temp} °C");
        if (r.Long("TemperatureMax") is > 0 and var tmax) g.Add("Max temperature", $"{tmax} °C");
        // Wear is the percentage of rated endurance used; 100 - wear is what tools usually call "life remaining".
        if (r.Long("Wear") is { } wear) g.Add("Wear", $"{Fmt.Percent(wear)} (life remaining {Fmt.Percent(Math.Max(0, 100 - wear))})");
        if (r.Long("PowerOnHours") is > 0 and var hours) g.Add("Power-on hours", Fmt.Number(hours));
        if (r.Long("StartStopCycleCount") is > 0 and var ss) g.Add("Start-stop cycles", Fmt.Number(ss));
        if (r.Long("LoadUnloadCycleCount") is > 0 and var lu) g.Add("Load-unload cycles", Fmt.Number(lu));
        if (r.Long("ReadErrorsTotal") is { } re) g.Add("Read errors", Fmt.Number(re));
        if (r.Long("WriteErrorsTotal") is { } we) g.Add("Write errors", Fmt.Number(we));
    }

    private static string? Operational(ushort[]? status)
    {
        if (status is not { Length: > 0 }) return null;
        return status[0] switch
        {
            2 => "OK",
            3 => "Degraded",
            5 => "Predictive failure",
            6 => "Error",
            10 => "Stopped",
            13 => "Lost communication",
            0xD00D => "In maintenance",
            _ => null
        };
    }

    private static string? PartitionType(string? gpt, int? mbr)
    {
        if (gpt != null)
        {
            return gpt.Trim('{', '}').ToLowerInvariant() switch
            {
                "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" => "EFI system",
                "e3c9e316-0b5c-4db8-817d-f92df00215ae" => "Microsoft reserved",
                "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" => "Basic data",
                "de94bba4-06d1-4d40-a16a-bfd50179d6ac" => "Recovery",
                "5808c8aa-7e8f-42e0-85d2-e1e90434cfb3" => "LDM metadata",
                "af9b60a0-1431-4f62-bc68-3311714a69ad" => "LDM data",
                "e75caf8f-f680-4cee-afa3-b001e56efc2d" => "Storage Spaces",
                "0fc63daf-8483-4772-8e79-3d69d8477de4" => "Linux filesystem",
                "0657fd6d-a4ab-43c4-84e5-0933c84b4f4f" => "Linux swap",
                "e6d6d379-f507-44c2-a23c-238f2a3df928" => "Linux LVM",
                _ => gpt
            };
        }
        return mbr switch
        {
            null or 0 => null,
            1 or 4 or 6 or 0x0E => "FAT",
            5 or 0x0F => "Extended",
            7 => "NTFS / exFAT",
            0x0B or 0x0C => "FAT32",
            0x27 => "Recovery",
            0x42 => "Dynamic",
            0x82 => "Linux swap",
            0x83 => "Linux",
            0xEE => "GPT protective",
            0xEF => "EFI system",
            _ => Fmt.Hex((ulong)mbr.Value, 2)
        };
    }
}
