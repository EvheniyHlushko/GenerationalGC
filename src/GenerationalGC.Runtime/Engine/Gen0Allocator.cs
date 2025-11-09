using System;
using GenerationalGC.Runtime.Core;

namespace GenerationalGC.Runtime.Engine
{
    public interface IGen0Allocator
    {
        void EnsureTlh(ThreadLocalHeap tlh, int requiredBytes, Func<bool> onSpaceNeeded);
        nint AllocateGen0(ThreadLocalHeap tlh, int sizeBytes, int typeId);
    }

    public sealed class Gen0Allocator : IGen0Allocator
    {
        private readonly Segment _gen0;
        private readonly int _tlhSlabBytes;

        public Gen0Allocator(Segment gen0, int tlhSlabBytes)
        {
            _gen0 = gen0 ?? throw new ArgumentNullException(nameof(gen0));
            _tlhSlabBytes = Math.Max(IntPtr.Size, tlhSlabBytes);
        }

        public void EnsureTlh(ThreadLocalHeap tlh, int requiredBytes, Func<bool> onSpaceNeeded)
        {
            if (tlh is null) throw new ArgumentNullException(nameof(tlh));
            if (onSpaceNeeded is null) throw new ArgumentNullException(nameof(onSpaceNeeded));

            if (tlh.Segment == _gen0 && tlh.SlabCursor + requiredBytes <= tlh.SlabLimit)
                return;

            if (!TryReserveSlabFromGen0(requiredBytes, out var slabStart, out var slabLimit))
            {
                if (!onSpaceNeeded()) throw new OutOfMemoryException("Gen0 allocation failed and onSpaceNeeded declined.");
                if (!TryReserveSlabFromGen0(requiredBytes, out slabStart, out slabLimit))
                    throw new OutOfMemoryException("Gen0 out of space after minor GC.");
            }

            tlh.Segment    = _gen0;
            tlh.SlabStart  = slabStart;
            tlh.SlabCursor = slabStart;
            tlh.SlabLimit  = slabLimit;
        }

        public nint AllocateGen0(ThreadLocalHeap tlh, int sizeBytes, int typeId)
        {
            if (tlh is null) throw new ArgumentNullException(nameof(tlh));
            if (tlh.Segment != _gen0) throw new InvalidOperationException("TLH is not bound to Gen0. Call EnsureTlh first.");

            sizeBytes = AlignUp(sizeBytes, IntPtr.Size);
            if (tlh.SlabCursor + sizeBytes > tlh.SlabLimit)
                throw new InvalidOperationException("TLH slab is full. Call EnsureTlh before AllocateGen0.");

            var objOff = tlh.SlabCursor;
            tlh.SlabCursor += sizeBytes;

            Mem.WriteHeader(_gen0.BasePtr, objOff, typeId);
            var abs = _gen0.BasePtr.ToInt64() + objOff;
          //  _gen0.Bricks.OnAllocation(abs);   // <-- per-segment brick table
            return (nint)abs;
        }

        private bool TryReserveSlabFromGen0(int minBytes, out int start, out int limit)
        {
            var slabBytes = Math.Max(_tlhSlabBytes, AlignUp(minBytes, IntPtr.Size));
            if (_gen0.AllocPtr + slabBytes > _gen0.SizeBytes)
            {
                start = limit = -1;
                return false;
            }

            start = _gen0.AllocPtr;
            limit = start + slabBytes;
            _gen0.AllocPtr = limit;
            return true;
        }

        private static int AlignUp(int v, int a) => (v + (a - 1)) & ~(a - 1);
    }
}