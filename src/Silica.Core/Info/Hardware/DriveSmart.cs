using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Silica.Core.Info.Hardware;

public sealed record SmartAttribute(int Id, string Name, int Value, int Worst, long Raw);

/// <summary>What a drive reports about its own wear, read straight from the drive.</summary>
public sealed record DriveHealth(
    string Source,
    int? LifeRemaining,
    long? PowerOnHours,
    long? PowerCycles,
    long? UnsafeShutdowns,
    double? BytesRead,
    double? BytesWritten,
    int? Temperature,
    int? AvailableSpare,
    int? SpareThreshold,
    long? MediaErrors,
    int CriticalWarning,
    IReadOnlyList<SmartAttribute> Attributes);

/// <summary>
/// SMART data straight from the drive. Windows' own reliability counters leave wear and power-on hours empty for
/// many NVMe drives, which made every drive look new; the drive's health log has the real numbers.
/// </summary>
public static class DriveSmart
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint SmartRcvDriveData = 0x0007C088;

    public static DriveHealth? Read(int diskNumber)
    {
        using var handle = Open(diskNumber);
        if (handle == null) return null;
        return ReadNvme(handle) ?? ReadAta(handle, diskNumber);
    }

    private static SafeFileHandle? Open(int diskNumber)
    {
        // Read/write access is what the protocol commands need; it is available when running as administrator.
        var h = CreateFile($@"\\.\PhysicalDrive{diskNumber}", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        h = CreateFile($@"\\.\PhysicalDrive{diskNumber}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    // NVMe health information log (log page 02h), 512 bytes; layout from the NVMe base specification.
    private static DriveHealth? ReadNvme(SafeFileHandle handle)
    {
        const int header = 8, protocolData = 40, logSize = 512;
        var buffer = new byte[header + protocolData + logSize];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), 50);            // StorageDeviceProtocolSpecificProperty
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 0);             // PropertyStandardQuery
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), 3);             // ProtocolTypeNvme
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), 2);            // NVMeDataTypeLogPage
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), 2);            // health information log
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), protocolData); // data follows the descriptor
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), logSize);
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buffer, buffer.Length, buffer, buffer.Length, out var returned, IntPtr.Zero)
            || returned < header + protocolData + 192)
            return null;

        var log = buffer.AsSpan(header + protocolData, logSize);
        var used = log[5];
        var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        return new DriveHealth(
            "NVMe",
            Math.Clamp(100 - used, 0, 100),
            (long)Lo(log, 128),
            (long)Lo(log, 112),
            (long)Lo(log, 144),
            Lo(log, 32) * 512_000.0, // data units are thousands of 512-byte blocks
            Lo(log, 48) * 512_000.0,
            kelvin > 200 ? kelvin - 273 : null,
            log[3],
            log[4],
            (long)Lo(log, 160),
            log[0],
            []);
    }

    // The 128-bit counters never exceed 64 bits in practice.
    private static ulong Lo(ReadOnlySpan<byte> log, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(log[offset..]);

    // ATA SMART READ DATA through the classic SMART ioctl: 30 attribute slots of 12 bytes after a 2-byte revision.
    private static DriveHealth? ReadAta(SafeFileHandle handle, int diskNumber)
    {
        var input = new byte[33];
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(0), 512);
        input[4] = 0xD0;                     // SMART READ DATA
        input[5] = 1;
        input[6] = 1;
        input[7] = 0x4F;
        input[8] = 0xC2;
        input[9] = (byte)(0xA0 | ((diskNumber & 1) << 4));
        input[10] = 0xB0;                    // SMART command
        input[12] = (byte)diskNumber;
        var output = new byte[16 + 512];
        if (!DeviceIoControl(handle, SmartRcvDriveData, input, input.Length, output, output.Length, out var returned, IntPtr.Zero) || returned < 16 + 362)
            return null;

        var data = output.AsSpan(16);
        var attributes = new List<SmartAttribute>();
        for (var i = 0; i < 30; i++)
        {
            var a = data.Slice(2 + i * 12, 12);
            if (a[0] == 0) continue;
            long raw = 0;
            for (var b = 5; b >= 0; b--) raw = (raw << 8) | a[5 + b];
            attributes.Add(new SmartAttribute(a[0], AttributeName(a[0]), a[3], a[4], raw));
        }
        if (attributes.Count == 0) return null;

        SmartAttribute? Get(int id) => attributes.FirstOrDefault(x => x.Id == id);
        // Vendors report remaining life in different attributes; the normalized value is the percentage left.
        int? life = new[] { 231, 169, 233, 177, 202 }.Select(Get).FirstOrDefault(x => x is { Value: > 0 and <= 100 })?.Value;
        return new DriveHealth(
            "SMART",
            life,
            Get(9)?.Raw & 0xFFFFFFFF,
            Get(12)?.Raw,
            Get(174)?.Raw ?? Get(192)?.Raw,
            Get(242)?.Raw * 512.0,
            Get(241)?.Raw * 512.0,
            Get(194)?.Raw is { } t ? (int)(t & 0xFF) : Get(190)?.Raw is { } t2 ? (int)(t2 & 0xFF) : null,
            null,
            null,
            (Get(5)?.Raw ?? 0) + (Get(197)?.Raw ?? 0) + (Get(198)?.Raw ?? 0),
            0,
            attributes);
    }

    private static string AttributeName(int id) => id switch
    {
        1 => "Read error rate",
        3 => "Spin-up time",
        4 => "Start/stop count",
        5 => "Reallocated sectors",
        7 => "Seek error rate",
        9 => "Power-on hours",
        10 => "Spin retry count",
        12 => "Power cycles",
        169 => "Remaining life",
        171 => "Program fail count",
        172 => "Erase fail count",
        173 => "Wear leveling",
        174 => "Unexpected power loss",
        177 => "Wear leveling count",
        179 => "Used reserved blocks",
        181 => "Program fail count",
        182 => "Erase fail count",
        183 => "Runtime bad blocks",
        184 => "End-to-end errors",
        187 => "Reported uncorrectable errors",
        188 => "Command timeouts",
        190 => "Airflow temperature",
        192 => "Power-off retracts",
        193 => "Load/unload cycles",
        194 => "Temperature",
        196 => "Reallocation events",
        197 => "Pending sectors",
        198 => "Uncorrectable sectors",
        199 => "CRC errors",
        202 => "Percent lifetime remaining",
        231 => "SSD life left",
        232 => "Available reserved space",
        233 => "Media wearout indicator",
        235 => "Power fail backup health",
        241 => "Total LBAs written",
        242 => "Total LBAs read",
        _ => $"Attribute {id}"
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);
}
