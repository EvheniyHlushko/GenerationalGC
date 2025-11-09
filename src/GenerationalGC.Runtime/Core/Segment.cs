using System;
using GenerationalGC.Runtime.Core;

namespace GenerationalGC.Runtime.Core;

/// <summary>
/// Contiguous unmanaged buffer representing a GC segment.
/// - Holds a bump pointer (AllocPtr)
/// - Owns a per-segment card table
/// - Owns a per-segment brick table
/// - Optionally NUMA-aware via INumaAllocator
/// </summary>
public sealed class Segment : IDisposable
{
    public Segment(Generation gen, int sizeBytes, int cardSizeBytes)
        : this(gen, sizeBytes, cardSizeBytes, numaNodeId: 0, allocator: new StubNumaAllocator())
    {
    }

    public Segment(Generation gen, int sizeBytes, int cardSizeBytes, int numaNodeId, INumaAllocator allocator)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        Gen         = gen;
        SizeBytes   = sizeBytes;
        NumaNodeId  = Math.Max(0, numaNodeId);
        _allocator  = allocator ?? throw new ArgumentNullException(nameof(allocator));

        BasePtr   = _allocator.Alloc(sizeBytes, NumaNodeId);
        AllocPtr  = 0;
        if (gen != Generation.Gen0)
        {
            Cards     = new CardTable(sizeBytes, Math.Max(64, cardSizeBytes));
            Bricks    = new SegmentBrickIndex(BasePtr, sizeBytes);
        }
       
    }

    public Generation Gen { get; }
    public IntPtr BasePtr { get; private set; }
    public int SizeBytes { get; }
    public int AllocPtr { get; set; }
    public CardTable Cards { get; }
    public SegmentBrickIndex Bricks { get; }
    public int NumaNodeId { get; }

    private readonly INumaAllocator _allocator;
    private bool _disposed;

    public bool TryAllocate(int sizeBytes, out int offset)
    {
        sizeBytes = AlignUp(sizeBytes, IntPtr.Size);
        if (AllocPtr + sizeBytes > SizeBytes)
        {
            offset = -1;
            return false;
        }

        offset   = AllocPtr;
        AllocPtr += sizeBytes;
        return true;
    }

    public void ResetNurseryLayout()
    {
        Mem.Zero(BasePtr, 0, SizeBytes);
        AllocPtr = 0;
        Cards?.ClearAll();
        Bricks?.ClearAll();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (BasePtr != IntPtr.Zero)
        {
            _allocator.Free(BasePtr, SizeBytes);
            BasePtr = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    private static int AlignUp(int v, int a) => (v + (a - 1)) & ~(a - 1);
}