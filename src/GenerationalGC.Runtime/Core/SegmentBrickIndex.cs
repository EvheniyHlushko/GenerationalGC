using System;
using GenerationalGC.Runtime.Engine;

namespace GenerationalGC.Runtime.Core;

/// <summary>
/// Per-segment brick table (like CLR GC):
/// - Brick = fixed-size chunk (e.g., 2KB)
/// - Entry = last object start offset (segment-relative) <= brickStart; -1 if none
/// Dense array for O(1) lookups and fast left-walk.
/// </summary>
public sealed class SegmentBrickIndex
{
    public const int DefaultBrickSizeBytes = 2048;

    private readonly int _brickShift;

    private readonly long[] _lastStartByBrick; // segment-relative offsets, -1 = no object
    private readonly IntPtr _segBase;
    private readonly int _segSize;

    public SegmentBrickIndex(IntPtr segmentBase, int segmentSize)
    {
        var brickSizeBytes = Heap.BrickSizeBytes;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(brickSizeBytes);

        _segBase = segmentBase;
        _segSize = segmentSize;

        _brickShift = BitOperations_TrailingZeroCount((uint)brickSizeBytes);

        var brickCount = (segmentSize + brickSizeBytes - 1) >> _brickShift;
        _lastStartByBrick = new long[brickCount];
        for (var i = 0; i < brickCount; i++) _lastStartByBrick[i] = -1;
    }

    /// <summary>Record that an object started at absolute address <paramref name="absoluteStart"/>.</summary>
    public void OnAllocation(long absoluteStart)
    {
        var off = checked((int)(absoluteStart - _segBase.ToInt64()));
        if (off < 0 || off >= _segSize) return; // ignore allocations not in this segment
        var key = off >> _brickShift;

        var cur = _lastStartByBrick[key];
        if (cur < 0 || cur < off) _lastStartByBrick[key] = off;
    }

    /// <summary>
    /// Return the absolute address of the last object start ≤ <paramref name="absAddress"/>.
    /// If none known in this or prior bricks, returns the segment base (safe left-walk start).
    /// </summary>
    public long SnapToObjectStart(long absAddress)
    {
        var segBase = _segBase.ToInt64();
        var off = checked((int)(absAddress - segBase));
        if (off < 0) return segBase;
        if (off >= _segSize) off = _segSize - 1;

        var key = off >> _brickShift;
        for (var k = key; k >= 0; k--)
        {
            var last = _lastStartByBrick[k];
            // Only accept starts that are <= the address we’re snapping
            if (last >= 0 && last <= off)
            {
                Console.WriteLine($"Start scanning from the brick index {k} with last obj offset {last} Card start offset {off}");
                return segBase + last;
            }
            Console.WriteLine($"Skipping segment brick index {k}. Last obj start offset {last} Card start offset {off}");
        }

        // No known start in/left of this point; safest is segment base
        return segBase;
    }


    /// <summary>Clear all brick entries for this segment (e.g., after compaction/promotion).</summary>
    public void ClearAll()
    {
        for (var i = 0; i < _lastStartByBrick.Length; i++) _lastStartByBrick[i] = -1;
    }

    // Small helper since we don't reference System.Numerics.BitOperations everywhere
    private static int BitOperations_TrailingZeroCount(uint v)
    {
        // v is power of two here.
        var c = 0;
        while ((v & 1u) == 0) { v >>= 1; c++; }
        return c;
    }
}