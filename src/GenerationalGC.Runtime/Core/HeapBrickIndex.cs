using System;
using System.Collections.Generic;

namespace GenerationalGC.Runtime.Core
{
    /// <summary>
    /// Heap-wide coarse index: for each brick (e.g., 2KB), store "last object start ≤ brickStart".
    /// Supports multiple discontiguous heap ranges (e.g., one per logical CPU or multiple segments).
    /// </summary>
    public sealed class HeapBrickIndex
    {
        private readonly int _brickSize;

        // Multiple ranges to support per-core heaps or multiple segments
        private readonly List<Range> _ranges = new();

        // Cache the minimum base across ranges for fallback (legacy snap)
        private long _minBase = long.MaxValue;

        private sealed class Range
        {
            public long Base;
            public long Length;
            public long[] LastStart = Array.Empty<long>();
        }

        public HeapBrickIndex(long heapBase, int brickSizeBytes, long heapLength)
        {
            // Power-of-two helps if you ever switch to bit math; also validates inputs.
            if ((brickSizeBytes & (brickSizeBytes - 1)) != 0)
                throw new ArgumentException("brickSizeBytes must be a power of two.", nameof(brickSizeBytes));

            _brickSize = Math.Max(512, brickSizeBytes);
            EnsureRange((ulong)heapBase, checked((int)heapLength));
        }

        /// <summary>Register an additional contiguous address range and seed its first brick to the base.</summary>
        public void EnsureRange(ulong segmentBase, int segmentSize)
        {
            var sBase = (long)segmentBase;
            if (segmentSize <= 0) return;

            foreach (var r in _ranges)
            {
                if (r.Base == sBase && r.Length == segmentSize) return; // already registered
            }

            var count = (int)(((long)segmentSize + _brickSize - 1) / _brickSize);
            var lastStart = new long[count];
            for (var i = 0; i < count; i++) lastStart[i] = -1;

            // Seed brick 0 to the range base so snap never walks into a different range
            lastStart[0] = sBase;

            _ranges.Add(new Range
            {
                Base = sBase,
                Length = segmentSize,
                LastStart = lastStart
            });

            if (sBase < _minBase) _minBase = sBase;
        }

        private int FindRangeIndexByAddress(long absAddr)
        {
            for (int i = 0; i < _ranges.Count; i++)
            {
                var r = _ranges[i];
                if (absAddr >= r.Base && absAddr < r.Base + r.Length)
                    return i;
            }
            return -1;
        }

        private static int BrickIndexUnchecked(long absAddr, long baseAddr, int brickSize, int lastStartLength)
        {
            var delta = absAddr - baseAddr;
            if (delta < 0) return 0;
            var idx = delta / brickSize;
            if (idx >= lastStartLength) return lastStartLength - 1;
            return (int)idx;
        }

        public void OnAllocation(long absoluteStart)
        {
            var ri = FindRangeIndexByAddress(absoluteStart);
            if (ri < 0)
                throw new InvalidOperationException("HeapBrickIndex: address not covered by any registered range. EnsureRange must be called for all segments.");

            var r = _ranges[ri];
            var i = BrickIndexUnchecked(absoluteStart, r.Base, _brickSize, r.LastStart.Length);
            if (r.LastStart[i] < absoluteStart) r.LastStart[i] = absoluteStart;
        }

        /// <summary>
        /// Snap an arbitrary address to a known object start, clamped to the containing segment.
        /// If the current brick has no record, walk left within the same range; otherwise return the range base.
        /// </summary>
        public long SnapWithinSegment(long absoluteAddress, long segmentBase)
        {
            var ri = FindRangeIndexByAddress(absoluteAddress);
            if (ri < 0) return segmentBase;

            var r = _ranges[ri];
            var i = BrickIndexUnchecked(absoluteAddress, r.Base, _brickSize, r.LastStart.Length);
            while (i >= 0 && r.LastStart[i] < 0) i--;
            var snap = i >= 0 ? r.LastStart[i] : r.Base;
            return snap < segmentBase ? segmentBase : snap;
        }

        /// <summary>
        /// Legacy snap (kept for back-compat); may return a base from another range. Prefer SnapWithinSegment.
        /// </summary>
        public long SnapToObjectStart(long absoluteAddress)
        {
            var ri = FindRangeIndexByAddress(absoluteAddress);
            if (ri < 0)
            {
                return _minBase == long.MaxValue ? 0 : _minBase;
            }

            var r = _ranges[ri];
            var i = BrickIndexUnchecked(absoluteAddress, r.Base, _brickSize, r.LastStart.Length);
            while (i >= 0 && r.LastStart[i] < 0) i--;
            return i >= 0 ? r.LastStart[i] : r.Base;
        }

        /// <summary>Clear brick entries overlapped by [segmentBase, segmentBase + segmentSize).</summary>
        public void ClearRange(ulong segmentBase, int segmentSize)
        {
            var sBase = (long)segmentBase;
            var ri = FindRangeIndexByAddress(sBase);
            if (ri < 0) return;

            var r = _ranges[ri];
            var start = sBase;
            var end = start + segmentSize;
            var i0 = BrickIndexUnchecked(start, r.Base, _brickSize, r.LastStart.Length);
            var i1 = BrickIndexUnchecked(end - 1, r.Base, _brickSize, r.LastStart.Length);
            for (var i = i0; i <= i1 && i < r.LastStart.Length; i++) r.LastStart[i] = -1;

            // Reseed brick 0 to the base so future snaps remain range-local
            if (i0 == 0) r.LastStart[0] = r.Base;
        }
    }
}