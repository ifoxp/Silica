using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Silica.Core.Sensors;

/// <summary>Where sensor values come from: this process, or the separate sensor host.</summary>
internal interface ISensorEngine : IDisposable
{
    SensorTree Tree { get; }

    /// <summary>Reads all sensors; <paramref name="tree"/> is non-null when the device list changed.</summary>
    float[] Poll(out SensorTree? tree);
}

internal sealed class LocalEngine(SensorGroups groups, bool includeVirtualNetwork) : ISensorEngine
{
    private readonly LhmSession _session = new(groups, includeVirtualNetwork);

    public SensorTree Tree => _session.Tree;

    public float[] Poll(out SensorTree? tree)
    {
        tree = _session.Poll(out var values) ? _session.Tree : null;
        return values;
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Talks to a "Silica --sensor-host" child process over a named pipe. When the hub releases the engine,
/// the child exits and every driver library and buffer it loaded goes with it.
/// </summary>
internal sealed class RemoteEngine : ISensorEngine
{
    private readonly NamedPipeServerStream _pipe;
    private readonly BinaryReader _reader = null!;
    private readonly BinaryWriter _writer = null!;
    private readonly Process _process;

    public RemoteEngine(Func<string, Process?> launch, SensorGroups groups, bool includeVirtualNetwork)
    {
        // The random name is passed only to our own child, so no other program can pose as the host.
        var name = "Silica.Sensors." + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _process = launch(name) ?? throw new InvalidOperationException("Sensor host did not start");
        try
        {
            var connect = _pipe.WaitForConnectionAsync();
            if (!connect.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Sensor host did not connect");
            _reader = new BinaryReader(_pipe, Encoding.UTF8, leaveOpen: true);
            _writer = new BinaryWriter(_pipe, Encoding.UTF8, leaveOpen: true);
            _writer.Write(SensorWire.Open);
            _writer.Write((int)groups);
            _writer.Write(includeVirtualNetwork);
            _writer.Flush();
            Tree = SensorWire.ReadTreeResponse(_reader);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public SensorTree Tree { get; private set; } = new([], []);

    public float[] Poll(out SensorTree? tree)
    {
        _writer.Write(SensorWire.Poll);
        _writer.Flush();
        tree = null;
        var kind = _reader.ReadByte();
        if (kind == SensorWire.TreeMessage)
        {
            tree = Tree = SensorWire.ReadTree(_reader);
            kind = _reader.ReadByte();
        }
        if (kind == SensorWire.ErrorMessage) throw new IOException(_reader.ReadString());
        if (kind != SensorWire.ValuesMessage) throw new InvalidDataException("Unexpected sensor host message");
        var count = _reader.ReadInt32();
        if (count != Tree.Sensors.Count) throw new InvalidDataException("Sensor count mismatch");
        var values = new float[count];
        for (var i = 0; i < count; i++) values[i] = _reader.ReadSingle();
        return values;
    }

    public void Dispose()
    {
        try
        {
            if (_pipe.IsConnected && _writer != null)
            {
                _writer.Write(SensorWire.Close);
                _writer.Flush();
            }
        }
        catch (IOException) { }
        _pipe.Dispose();
        try
        {
            if (!_process.WaitForExit(3000)) _process.Kill();
        }
        catch (InvalidOperationException) { }
        _process.Dispose();
    }
}

/// <summary>The few messages between Silica and its sensor host.</summary>
public static class SensorWire
{
    public const byte Open = 1, Poll = 2, Close = 3;
    public const byte TreeMessage = 10, ValuesMessage = 11, ErrorMessage = 12;

    public static void WriteTree(BinaryWriter w, SensorTree tree)
    {
        w.Write(TreeMessage);
        w.Write(tree.Devices.Count);
        foreach (var d in tree.Devices)
        {
            w.Write(d.Id);
            w.Write(d.Name);
            w.Write((byte)d.Kind);
            w.Write(d.Parent);
        }
        w.Write(tree.Sensors.Count);
        foreach (var s in tree.Sensors)
        {
            w.Write(s.Id);
            w.Write(s.Name);
            w.Write((byte)s.Kind);
            w.Write(s.Device);
        }
    }

    public static void WriteValues(BinaryWriter w, float[] values)
    {
        w.Write(ValuesMessage);
        w.Write(values.Length);
        foreach (var v in values) w.Write(v);
    }

    public static SensorTree ReadTreeResponse(BinaryReader r)
    {
        var kind = r.ReadByte();
        if (kind == ErrorMessage) throw new IOException(r.ReadString());
        if (kind != TreeMessage) throw new InvalidDataException("Unexpected sensor host message");
        return ReadTree(r);
    }

    public static SensorTree ReadTree(BinaryReader r)
    {
        var devices = new List<DeviceSpec>();
        var deviceCount = r.ReadInt32();
        for (var i = 0; i < deviceCount; i++)
        {
            var d = new DeviceSpec(r.ReadString(), r.ReadString(), (DeviceKind)r.ReadByte(), r.ReadInt32());
            if (d.Parent < -1 || d.Parent >= i) throw new InvalidDataException("Bad device parent");
            devices.Add(d);
        }
        var sensors = new List<SensorSpec>();
        var sensorCount = r.ReadInt32();
        for (var i = 0; i < sensorCount; i++)
        {
            var s = new SensorSpec(r.ReadString(), r.ReadString(), (SensorKind)r.ReadByte(), r.ReadInt32());
            if (s.Device < 0 || s.Device >= deviceCount) throw new InvalidDataException("Bad sensor device");
            sensors.Add(s);
        }
        return new SensorTree(devices, sensors);
    }
}

/// <summary>Entry point of "Silica --sensor-host &lt;pipe&gt; &lt;parent pid&gt;".</summary>
public static class SensorHost
{
    public static int Run(string pipeName, int parentId)
    {
        // Leave when Silica goes away, even if it could not say goodbye.
        try
        {
            var parent = Process.GetProcessById(parentId);
            new Thread(() => { parent.WaitForExit(); Environment.Exit(0); }) { IsBackground = true }.Start();
        }
        catch (ArgumentException)
        {
            return 1;
        }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(10000);
        using var reader = new BinaryReader(pipe, Encoding.UTF8);
        using var writer = new BinaryWriter(pipe, Encoding.UTF8);
        LhmSession? session = null;
        try
        {
            while (true)
            {
                var command = reader.ReadByte();
                if (command == SensorWire.Close) return 0;
                try
                {
                    if (command == SensorWire.Open)
                    {
                        var groups = (SensorGroups)reader.ReadInt32();
                        var includeVirtual = reader.ReadBoolean();
                        session?.Dispose();
                        session = new LhmSession(groups, includeVirtual);
                        SensorWire.WriteTree(writer, session.Tree);
                    }
                    else if (command == SensorWire.Poll && session != null)
                    {
                        if (session.Poll(out var values)) SensorWire.WriteTree(writer, session.Tree);
                        SensorWire.WriteValues(writer, values);
                    }
                    else
                    {
                        throw new InvalidOperationException("Unknown command");
                    }
                }
                catch (Exception ex) when (ex is not IOException)
                {
                    writer.Write(SensorWire.ErrorMessage);
                    writer.Write(ex.Message);
                }
                writer.Flush();
            }
        }
        catch (EndOfStreamException) { return 0; }
        catch (IOException) { return 0; }
        finally
        {
            session?.Dispose();
        }
    }
}
