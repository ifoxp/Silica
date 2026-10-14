namespace Silica.Core.Sensors;

/// <summary>The handful of sensors the dashboard, tray and stability test care about, picked once per device tree.</summary>
public sealed record KeySensors
{
    public static KeySensors Empty { get; } = new();

    public DeviceNode? Cpu { get; init; }
    public SensorReading? CpuLoad { get; init; }
    public SensorReading? CpuTemp { get; init; }
    public SensorReading? CpuClock { get; init; }
    public SensorReading? CpuPower { get; init; }

    public DeviceNode? Gpu { get; init; }
    public SensorReading? GpuLoad { get; init; }
    public SensorReading? GpuTemp { get; init; }
    public SensorReading? GpuClock { get; init; }
    public SensorReading? GpuPower { get; init; }
    public SensorReading? GpuMemoryUsed { get; init; }
    public SensorReading? GpuMemoryTotal { get; init; }

    public SensorReading? RamLoad { get; init; }
    public SensorReading? RamUsed { get; init; }
    public SensorReading? RamAvailable { get; init; }

    public IReadOnlyList<DeviceNode> Networks { get; init; } = [];
    public IReadOnlyList<SensorReading> Fans { get; init; } = [];
    public IReadOnlyList<SensorReading> StorageTemps { get; init; } = [];

    public static KeySensors Resolve(IReadOnlyList<DeviceNode> roots)
    {
        var all = roots.SelectMany(Flatten).ToList();
        var cpu = all.FirstOrDefault(d => d.Kind == DeviceKind.Cpu);
        // Prefer a discrete GPU: the one with the most dedicated memory, else the first.
        var gpus = all.Where(d => d.Kind == DeviceKind.Gpu).ToList();
        var gpu = gpus.OrderByDescending(g => Pick(g, SensorKind.SmallData, "GPU Memory Total")?.Value ?? 0).FirstOrDefault();
        var ram = all.FirstOrDefault(d => d.Kind == DeviceKind.Memory && d.Id == "/ram")
                  ?? all.FirstOrDefault(d => d.Kind == DeviceKind.Memory);

        return new KeySensors
        {
            Cpu = cpu,
            CpuLoad = Pick(cpu, SensorKind.Load, "CPU Total"),
            CpuTemp = Pick(cpu, SensorKind.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core Max", "Core Average", "Tdie", "CPU Cores", "*"),
            CpuClock = Pick(cpu, SensorKind.Clock, "Cores (Average)", "Core #1", "Core #1 (Effective)", "*"),
            CpuPower = Pick(cpu, SensorKind.Power, "Package", "CPU Package", "CPU Cores", "*"),
            Gpu = gpu,
            GpuLoad = Pick(gpu, SensorKind.Load, "GPU Core", "D3D 3D", "*"),
            GpuTemp = Pick(gpu, SensorKind.Temperature, "GPU Core", "GPU Hot Spot", "*"),
            GpuClock = Pick(gpu, SensorKind.Clock, "GPU Core", "*"),
            GpuPower = Pick(gpu, SensorKind.Power, "GPU Package", "GPU Power", "GPU Core", "*"),
            GpuMemoryUsed = Pick(gpu, SensorKind.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
            GpuMemoryTotal = Pick(gpu, SensorKind.SmallData, "GPU Memory Total"),
            RamLoad = Pick(ram, SensorKind.Load, "Memory"),
            RamUsed = Pick(ram, SensorKind.Data, "Memory Used"),
            RamAvailable = Pick(ram, SensorKind.Data, "Memory Available"),
            Networks = all.Where(d => d.Kind == DeviceKind.Network).ToList(),
            Fans = all.Where(d => d.Kind is not DeviceKind.Gpu).SelectMany(d => d.Sensors).Where(s => s.Kind == SensorKind.Fan).ToList(),
            StorageTemps = all.Where(d => d.Kind == DeviceKind.Storage)
                .Select(d => d.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Temperature))
                .OfType<SensorReading>().ToList()
        };
    }

    /// <summary>First sensor of <paramref name="kind"/> whose name matches one of <paramref name="names"/> in order; "*" means any.</summary>
    public static SensorReading? Pick(DeviceNode? device, SensorKind kind, params string[] names)
    {
        if (device == null) return null;
        var candidates = device.AllSensors().Where(s => s.Kind == kind).ToList();
        foreach (var name in names)
        {
            var hit = name == "*"
                ? candidates.FirstOrDefault()
                : candidates.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    private static IEnumerable<DeviceNode> Flatten(DeviceNode node) => node.Children.SelectMany(Flatten).Prepend(node);
}
