using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Silica.Core.Sensors;
using Silica.Localization;

namespace Silica.Views;

public abstract class SensorListRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DeviceRow(DeviceNode device, int count, bool collapsed) : SensorListRow
{
    public DeviceNode Device { get; } = device;
    public string Name => Device.Name;
    public string Count => Loc.Instance.N(count, "{0} sensors");
    public bool IsChild => Device.Parent != null;
    public bool Collapsed { get; } = collapsed;
    public string Chevron => Collapsed ? "" : "";

    public string Icon => Device.Kind switch
    {
        DeviceKind.Cpu => "",
        DeviceKind.Gpu => "",
        DeviceKind.Memory => "",
        DeviceKind.Motherboard or DeviceKind.SuperIO or DeviceKind.EmbeddedController => "",
        DeviceKind.Storage => "",
        DeviceKind.Network => "",
        DeviceKind.Battery => "",
        DeviceKind.Cooler => "",
        DeviceKind.Psu => "",
        _ => ""
    };
}

public sealed class SensorRow(SensorReading reading) : SensorListRow
{
    private string _value = "—", _min = "—", _max = "—", _avg = "—";
    private bool _pinned;
    private Brush _dot = Brushes.Transparent;

    public SensorReading Reading { get; } = reading;
    public string Name => Loc.Instance.T(Reading.Name);
    public string KindName => Loc.Instance.T(KindTitle(Reading.Kind));

    public string Value { get => _value; private set { if (_value != value) { _value = value; Raise(); } } }
    public string Min { get => _min; private set { if (_min != value) { _min = value; Raise(); } } }
    public string Max { get => _max; private set { if (_max != value) { _max = value; Raise(); } } }
    public string Avg { get => _avg; private set { if (_avg != value) { _avg = value; Raise(); } } }

    public bool Pinned { get => _pinned; set { if (_pinned != value) { _pinned = value; Raise(); } } }
    public Brush Dot { get => _dot; set { _dot = value; Raise(); } }

    public int Version { get; private set; }

    public void Update()
    {
        Version++;
        Raise(nameof(Version));
        var k = Reading.Kind;
        Value = SensorFormat.Format(k, Reading.Value);
        Min = SensorFormat.Format(k, Reading.Min);
        Max = SensorFormat.Format(k, Reading.Max);
        Avg = SensorFormat.Format(k, Reading.Average is { } a ? (float)a : null);
    }

    public void Relocalize()
    {
        Raise(nameof(Name));
        Raise(nameof(KindName));
        Update();
    }

    public static string KindTitle(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "Temperature",
        SensorKind.Load => "Load",
        SensorKind.Clock => "Clock",
        SensorKind.Voltage => "Voltage",
        SensorKind.Current => "Electric current",
        SensorKind.Power => "Power draw",
        SensorKind.Fan => "Fan",
        SensorKind.Flow => "Flow",
        SensorKind.Control => "Fan control",
        SensorKind.Level => "Level",
        SensorKind.Data or SensorKind.SmallData => "Data",
        SensorKind.Throughput => "Throughput",
        SensorKind.Factor => "Factor",
        SensorKind.Energy => "Energy",
        SensorKind.Noise => "Noise",
        SensorKind.TimeSpan => "Time",
        SensorKind.Frequency => "Frequency",
        SensorKind.Humidity => "Humidity",
        _ => "Other"
    };
}
