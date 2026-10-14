using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Silica.Core.Bench;

namespace Silica.Core.Stress;

internal interface IPattern
{
    static abstract ulong At(ulong pass, nuint index, ulong address);
}

internal static unsafe partial class MemoryStress
{
    private const long MinBytes = 64L << 20, MaxBytes = 4L << 30;
    private const nuint Chunk = 16 << 20;

    private struct WalkingOnes : IPattern
    {
        public static ulong At(ulong pass, nuint index, ulong address) => 1UL << (int)((index + pass) & 63);
    }

    private struct WalkingZeros : IPattern
    {
        public static ulong At(ulong pass, nuint index, ulong address) => ~(1UL << (int)((index + pass) & 63));
    }

    private struct RandomWords : IPattern
    {
        public static ulong At(ulong pass, nuint index, ulong address) => Rng.SplitMix((pass << 40) ^ index);
    }

    private struct OwnAddress : IPattern
    {
        public static ulong At(ulong pass, nuint index, ulong address) => address;
    }

    private struct InvertedAddress : IPattern
    {
        public static ulong At(ulong pass, nuint index, ulong address) => ~address;
    }

    private struct Checkerboard : IPattern
    {
        public static ulong At(ulong pass, nuint index, ulong address) =>
            ((index ^ pass) & 1) == 0 ? 0x5555_5555_5555_5555UL : 0xAAAA_AAAA_AAAA_AAAAUL;
    }

    private static readonly string[] PatternNames = ["walking ones", "walking zeros", "random", "address", "inverted address", "checkerboard"];

    public static long BytesPerThread(int threads)
    {
        var status = new MemoryStatusEx { Length = (uint)sizeof(MemoryStatusEx) };
        var avail = GlobalMemoryStatusEx(ref status) ? (long)status.AvailPhys : 2L << 30;
        return Math.Clamp(avail / 2 / threads, MinBytes, MaxBytes) & ~((1L << 20) - 1);
    }

    public static void Run(StressEngine engine, int index, long bytes, CancellationToken token)
    {
        NativeBlock? block = null;
        while (block is null)
        {
            try { block = new NativeBlock((nuint)bytes, 4096); }
            catch (OutOfMemoryException) when (bytes > MinBytes) { bytes /= 2; }
        }

        using (block)
        {
            var words = block.Length / 8;
            var p = (ulong*)block.Pointer;
            for (var pass = (ulong)index * 7; !token.IsCancellationRequested; pass++)
            {
                var kind = (int)(pass % (ulong)PatternNames.Length);
                var result = kind switch
                {
                    0 => Test<WalkingOnes>(p, words, pass, token),
                    1 => Test<WalkingZeros>(p, words, pass, token),
                    2 => Test<RandomWords>(p, words, pass, token),
                    3 => Test<OwnAddress>(p, words, pass, token),
                    4 => Test<InvertedAddress>(p, words, pass, token),
                    _ => Test<Checkerboard>(p, words, pass, token)
                };
                if (result is null) break;
                var (bad, at, expected, actual) = result.Value;
                if (bad > 0)
                    engine.Fail(StressComponent.Memory, "Memory verification failed",
                        $"{PatternNames[kind]}, {bad} words, first at {at:X}, expected {expected:X16}, read {actual:X16}");
                engine.Tick();
            }
        }
    }

    /// <summary>Fills the whole block, then verifies it. Returns null when cancelled midway.</summary>
    private static (long Bad, ulong At, ulong Expected, ulong Actual)? Test<T>(ulong* p, nuint words, ulong pass, CancellationToken token)
        where T : struct, IPattern
    {
        var chunkWords = Chunk / 8;
        for (nuint start = 0; start < words; start += chunkWords)
        {
            if (token.IsCancellationRequested) return null;
            var end = Math.Min(words, start + chunkWords);
            for (var i = start; i < end; i++) p[i] = T.At(pass, i, (ulong)(p + i));
        }

        long bad = 0;
        ulong at = 0, expected = 0, actual = 0;
        for (nuint start = 0; start < words; start += chunkWords)
        {
            if (token.IsCancellationRequested) return null;
            var end = Math.Min(words, start + chunkWords);
            for (var i = start; i < end; i++)
            {
                var e = T.At(pass, i, (ulong)(p + i));
                var v = Volatile.Read(ref p[i]);
                if (v == e) continue;
                if (bad++ == 0) (at, expected, actual) = ((ulong)(p + i), e, v);
            }
        }
        return (bad, at, expected, actual);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}

internal static unsafe class DiskStress
{
    private const long FileBytes = 1L << 30, MinFileBytes = 64L << 20, Reserve = 256L << 20;
    private const int Block = 4 << 20;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static void Run(StressEngine engine, int index, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var size = FileBytes;
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
            size = Math.Min(size, (free - Reserve) / Block * Block);
        }
        catch (ArgumentException) { }
        if (size < MinFileBytes)
        {
            engine.Info("Not enough free disk space for the disk test");
            return;
        }

        var path = Path.Combine(directory, $"Silica-stress-{Environment.ProcessId}-{index}.tmp");
        engine.RegisterTempFile(path);
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                NoBuffering | FileOptions.WriteThrough | FileOptions.DeleteOnClose);
            using var write = new AlignedMemory(Block);
            using var read = new AlignedMemory(Block);
            var blocks = size / Block;
            for (var pass = 0UL; !token.IsCancellationRequested; pass++)
            {
                for (long b = 0; b < blocks; b++)
                {
                    if (token.IsCancellationRequested) return;
                    Fill(write.GetSpan(), pass, b);
                    RandomAccess.Write(handle, write.GetSpan(), b * Block);
                }

                long badBlocks = 0, firstBad = -1;
                for (long b = 0; b < blocks; b++)
                {
                    if (token.IsCancellationRequested) return;
                    var n = RandomAccess.Read(handle, read.GetSpan(), b * Block);
                    Fill(write.GetSpan(), pass, b);
                    if (n == Block && read.GetSpan().SequenceEqual(write.GetSpan())) continue;
                    if (badBlocks++ == 0) firstBad = b;
                }
                if (badBlocks > 0)
                    engine.Fail(StressComponent.Disk, "Disk verification failed",
                        $"{badBlocks} blocks of 4 MiB, first at offset {firstBad * Block}");
                engine.Tick();
            }
        }
        finally
        {
            engine.ReleaseTempFile(path);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fill(Span<byte> buffer, ulong pass, long block)
    {
        var words = MemoryMarshal.Cast<byte, ulong>(buffer);
        var rng = new Rng(Rng.SplitMix((pass << 32) ^ (ulong)block));
        foreach (ref var w in words) w = rng.Next();
    }
}
