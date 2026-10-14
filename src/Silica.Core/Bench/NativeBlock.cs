using System.Buffers;
using System.Runtime.InteropServices;

namespace Silica.Core.Bench;

/// <summary>Aligned unmanaged memory that is freed exactly once (finalizer as a safety net).</summary>
internal sealed unsafe class NativeBlock : IDisposable
{
    private byte* _ptr;

    public NativeBlock(nuint length, nuint alignment = 4096)
    {
        Length = length;
        _ptr = (byte*)NativeMemory.AlignedAlloc(length, alignment);
    }

    public byte* Pointer => _ptr != null ? _ptr : throw new ObjectDisposedException(nameof(NativeBlock));
    public nuint Length { get; }

    public void Dispose()
    {
        var p = _ptr;
        _ptr = null;
        if (p == null) return;
        NativeMemory.AlignedFree(p);
        GC.SuppressFinalize(this);
    }

    ~NativeBlock() => Dispose();
}

/// <summary>Sector-aligned buffer usable as <see cref="Memory{T}"/> for unbuffered file I/O.</summary>
internal sealed unsafe class AlignedMemory : MemoryManager<byte>
{
    private readonly NativeBlock _block;
    private readonly int _length;

    public AlignedMemory(int length, int alignment = 4096)
    {
        _length = length;
        _block = new NativeBlock((nuint)length, (nuint)alignment);
    }

    public byte* Pointer => _block.Pointer;

    public override Span<byte> GetSpan() => new(_block.Pointer, _length);

    public override MemoryHandle Pin(int elementIndex = 0) => new(_block.Pointer + elementIndex);

    public override void Unpin() { }

    protected override void Dispose(bool disposing) => _block.Dispose();
}
