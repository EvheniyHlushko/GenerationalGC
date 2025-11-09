using GenerationalGC.Runtime.Core;

namespace GenerationalGC.Runtime.Engine;

public sealed class EphemeralCollector : IEphemeralCollector
{
    private readonly Func<int, int, int> _alignUp = static (v, a) => (v + (a - 1)) & ~(a - 1);
    private readonly HeapBrickIndex _bricks;
    private readonly Segment _gen0, _gen1, _gen2, _loh;
    private readonly Func<nint, (bool ok, Segment seg, int off)> _mapTuple; // friendlier for local lambdas

    // Local mark set reused per-collection
    private readonly HashSet<long> _markSet = new();
    private readonly Func<nint, bool> _tryMap; // TryMap(addr, out seg, out off) => bool
    private readonly IReadOnlyDictionary<int, TypeDesc> _types;

    public EphemeralCollector(
        Segment gen0,
        Segment gen1,
        Segment gen2,
        Segment loh,
        HeapBrickIndex bricks,
        IReadOnlyDictionary<int, TypeDesc> types,
        Func<nint, bool> tryMap,
        Func<nint, (bool ok, Segment seg, int off)> tryMapTuple)
    {
        _gen0 = gen0 ?? throw new ArgumentNullException(nameof(gen0));
        _gen1 = gen1 ?? throw new ArgumentNullException(nameof(gen1));
        _gen2 = gen2 ?? throw new ArgumentNullException(nameof(gen2));
        _loh = loh ?? throw new ArgumentNullException(nameof(loh));
        _bricks = bricks ?? throw new ArgumentNullException(nameof(bricks));
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _tryMap = tryMap ?? throw new ArgumentNullException(nameof(tryMap));
        _mapTuple = tryMapTuple ?? throw new ArgumentNullException(nameof(tryMapTuple));
    }

    // Single-heap ephemeral collection (scan this heap’s old gens only)
    public void CollectLocal(IDictionary<string, ObjRef> roots, IList<Region> regions)
    {
        _markSet.Clear();
        var work = new Stack<ObjRef>();

        bool IsEphemeral(ObjRef r)
        {
            if (!_tryMap(r.Address)) return false;
            var (ok, seg, _) = _mapTuple(r.Address);
            return ok && seg.Gen is Generation.Gen0 or Generation.Gen1;
        }

        // 1) roots into Gen0/Gen1
        foreach (var r in roots.Values)
            if (!r.IsNull && IsEphemeral(r) && _markSet.Add(r.Address))
                work.Push(r);

        // region→GC roots
        foreach (var region in regions)
        foreach (var abs in region.ExternalGcRoots)
        {
            var rr = new ObjRef((nint)abs);
            if (IsEphemeral(rr) && _markSet.Add(abs)) work.Push(rr);
        }

        // 2) dirty cards in *this heap’s* old segments
        ScanDirtyCardsForGen0Children(_gen1, work);
        ScanDirtyCardsForGen0Children(_gen2, work);
        ScanDirtyCardsForGen0Children(_loh, work);

        // 3) traverse within Gen0/Gen1
        Traverse(work, IsEphemeral);

        // 4) compact gen0 + fix refs
        var gen0Reloc = CompactGen0();
        if (gen0Reloc.Count > 0) FixAllReferences(gen0Reloc, roots, regions);

        // 5) promote gen0 survivors + fix refs
        var promotionReloc = PromoteGen0ToGen1();
        if (promotionReloc.Count > 0) FixAllReferences(promotionReloc, roots, regions);

        // Clear cards we used
        _gen1.Cards.ClearAll();
        _gen2.Cards.ClearAll();
        _loh.Cards.ClearAll();
    }

    // Server-style: scan dirty cards from old segments across *all heaps*
    public void CollectWithCrossHeap(
        IDictionary<string, ObjRef> roots,
        IList<Region> regions,
        IEnumerable<Segment> oldSegmentsFromAllHeaps,
        out Dictionary<long, long> relocFromCompaction,
        out Dictionary<long, long> relocFromPromotion)
    {
        _markSet.Clear();
        var work = new Stack<ObjRef>();

        // 1) roots into Gen0/Gen1
        foreach (var r in roots.Values)
            if (!r.IsNull && IsEphemeral(r) && _markSet.Add(r.Address))
                work.Push(r);

        // region→GC roots
        foreach (var region in regions)
        foreach (var abs in region.ExternalGcRoots)
        {
            var rr = new ObjRef((nint)abs);
            if (IsEphemeral(rr) && _markSet.Add(abs)) work.Push(rr);
        }

        // 2) dirty cards in *all heaps’* old segments
        foreach (var seg in oldSegmentsFromAllHeaps)
            ScanDirtyCardsForGen0Children(seg, work);

        // 3) traverse within Gen0/Gen1
        Traverse(work, IsEphemeral);

        // 4) compact gen0 + reloc A
        relocFromCompaction = CompactGen0();
        if (relocFromCompaction.Count > 0) FixAllReferences(relocFromCompaction, roots, regions);

        // 5) promote gen0 survivors + reloc B
        relocFromPromotion = PromoteGen0ToGen1();
        if (relocFromPromotion.Count > 0) FixAllReferences(relocFromPromotion, roots, regions);

        _gen1.Cards.ClearAll();
        _gen2.Cards.ClearAll();
        _loh.Cards.ClearAll();
        return;

        bool IsEphemeral(ObjRef r)
        {
            if (!_tryMap(r.Address)) return false;
            var (ok, seg, _) = _mapTuple(r.Address);
            return ok && seg.Gen is Generation.Gen0 or Generation.Gen1;
        }
    }

    // --- internals ----------------------------------------------------------

    private void ScanDirtyCardsForGen0Children(Segment seg, Stack<ObjRef> work)
    {
        foreach (var (start, end) in seg.Cards.DirtyRanges())
        {
            var absStart = seg.BasePtr.ToInt64() + start;
            var absEnd = seg.BasePtr.ToInt64() + end;

            var scanAbs = _bricks.SnapToObjectStart(absStart);
            if (scanAbs < seg.BasePtr.ToInt64()) scanAbs = seg.BasePtr.ToInt64();

            while (scanAbs < absEnd)
            {
                var (ok, owner, objOff) = _mapTuple((nint)scanAbs);
                if (!ok || !ReferenceEquals(owner, seg)) break;

                var typeId = Mem.ReadTypeId(owner.BasePtr, objOff);
                if (!_types.TryGetValue(typeId, out var t)) break;

                var objSize = _alignUp(Layout.HeaderSize + t.Size, IntPtr.Size);

                ScanRefsInObject(owner, objOff, t, child =>
                {
                    if (child.IsNull) return;
                    var (ok2, childSeg, _) = _mapTuple(child.Address);
                    if (ok2 && childSeg.Gen == Generation.Gen0 && _markSet.Add(child.Address))
                        work.Push(child);
                });

                scanAbs += objSize;
            }
        }
    }

    private Dictionary<long, long> CompactGen0()
    {
        if (_gen0.AllocPtr == 0)
        {
            _bricks.ClearRange((ulong)_gen0.BasePtr.ToInt64(), _gen0.SizeBytes);
            return new Dictionary<long, long>();
        }

        var live = new List<(int Off, TypeDesc T, int Size)>();
        var cursor = 0;

        while (cursor < _gen0.AllocPtr)
        {
            var typeId = Mem.ReadTypeId(_gen0.BasePtr, cursor);
            if (!_types.TryGetValue(typeId, out var t)) break;
            var size = _alignUp(Layout.HeaderSize + t.Size, IntPtr.Size);

            var abs = _gen0.BasePtr.ToInt64() + cursor;
            if (_markSet.Contains(abs)) live.Add((cursor, t, size));

            cursor += size;
        }

        _bricks.ClearRange((ulong)_gen0.BasePtr.ToInt64(), _gen0.SizeBytes);

        if (live.Count == 0)
        {
            _gen0.ResetNurseryLayout();
            return new Dictionary<long, long>();
        }

        var scratch = new byte[_gen0.SizeBytes];
        var writePtr = 0;
        var reloc = new Dictionary<long, long>(live.Count);

        foreach (var s in live)
        {
            CopyUnmanagedToManaged(_gen0.BasePtr, s.Off, scratch, writePtr, s.Size);

            var oldAbs = _gen0.BasePtr.ToInt64() + s.Off;
            var newAbs = _gen0.BasePtr.ToInt64() + writePtr;
            reloc[oldAbs] = newAbs;

            _bricks.OnAllocation(newAbs);
            writePtr += s.Size;
        }

        CopyManagedToUnmanaged(scratch, 0, _gen0.BasePtr, 0, writePtr);
        Mem.Zero(_gen0.BasePtr, writePtr, _gen0.SizeBytes - writePtr);
        _gen0.AllocPtr = writePtr;

        return reloc;
    }

    private Dictionary<long, long> PromoteGen0ToGen1()
    {
        var reloc = new Dictionary<long, long>();
        var cursor = 0;

        while (cursor < _gen0.AllocPtr)
        {
            var typeId = Mem.ReadTypeId(_gen0.BasePtr, cursor);
            if (!_types.TryGetValue(typeId, out var t)) break;
            var size = _alignUp(Layout.HeaderSize + t.Size, IntPtr.Size);

            if (!_gen1.TryAllocate(size, out var dstOff))
                throw new OutOfMemoryException("Gen1 full during promotion.");

            CopyUnmanagedToUnmanaged(_gen0.BasePtr, cursor, _gen1.BasePtr, dstOff, size);

            var oldAbs = _gen0.BasePtr.ToInt64() + cursor;
            var newAbs = _gen1.BasePtr.ToInt64() + dstOff;
            reloc[oldAbs] = newAbs;

            _bricks.OnAllocation(newAbs);
            cursor += size;
        }

        _bricks.ClearRange((ulong)_gen0.BasePtr.ToInt64(), _gen0.SizeBytes);
        _gen0.ResetNurseryLayout();

        return reloc;
    }

    private void FixAllReferences(Dictionary<long, long> reloc, IDictionary<string, ObjRef> roots,
        IList<Region> regions)
    {
        // Roots
        foreach (var key in roots.Keys.ToList())
        {
            var r = roots[key];
            if (r.IsNull) continue;
            var a = (long)r.Address;
            if (reloc.TryGetValue(a, out var n))
                roots[key] = new ObjRef((nint)n);
        }

        // Heap segments (owned by this collector)
        FixSegment(_gen0, reloc);
        FixSegment(_gen1, reloc);
        FixSegment(_gen2, reloc);
        FixSegment(_loh, reloc);

        // Region external roots (if their target moved/promoted)
        foreach (var region in regions)
        {
            if (region.ExternalGcRoots.Count == 0) continue;
            var updated = new HashSet<long>();
            foreach (var a in region.ExternalGcRoots)
                updated.Add(reloc.GetValueOrDefault(a, a));
            region.ExternalGcRoots.Clear();
            foreach (var a in updated) region.ExternalGcRoots.Add(a);
        }
    }

    private void FixSegment(Segment seg, Dictionary<long, long> reloc)
    {
        var cursor = 0;
        while (cursor < seg.AllocPtr)
        {
            var typeId = Mem.ReadTypeId(seg.BasePtr, cursor);
            if (!_types.TryGetValue(typeId, out var t)) break;
            var size = _alignUp(Layout.HeaderSize + t.Size, IntPtr.Size);

            RewriteRefsInObject(seg, cursor, t, reloc);
            cursor += size;
        }
    }

    private void Traverse(Stack<ObjRef> work, Func<ObjRef, bool> follow)
    {
        while (work.Count > 0)
        {
            var cur = work.Pop();
            if (cur.IsNull) continue;

            var (ok, seg, off) = _mapTuple(cur.Address);
            if (!ok) continue;

            var typeId = Mem.ReadTypeId(seg.BasePtr, off);
            if (!_types.TryGetValue(typeId, out var t)) continue;

            ScanRefsInObject(seg, off, t, child =>
            {
                if (child.IsNull) return;
                var addr = (long)child.Address;
                if (_markSet.Add(addr) && follow(child))
                    work.Push(child);
            });
        }
    }

    private static void ScanRefsInObject(Segment seg, int objectOffset, TypeDesc type, Action<ObjRef> onRef)
    {
        foreach (var f in type.Fields)
            switch (f.Kind)
            {
                case FieldKind.Ref:
                {
                    var p = Mem.ReadRef64(seg.BasePtr, objectOffset, f.Offset);
                    if (p != 0) onRef(new ObjRef((nint)p));
                    break;
                }
                case FieldKind.Struct:
                {
                    var structPayload = objectOffset + Layout.HeaderSize + f.Offset;
                    ScanRefsInStruct(seg, structPayload, f.StructType!, onRef);
                    break;
                }
            }
    }

    private static void ScanRefsInStruct(Segment seg, int structPayloadOffset, TypeDesc structType,
        Action<ObjRef> onRef)
    {
        foreach (var sf in structType.Fields)
            switch (sf.Kind)
            {
                case FieldKind.Ref:
                {
                    var p = Mem.ReadI64(seg.BasePtr, structPayloadOffset + sf.Offset);
                    if (p != 0) onRef(new ObjRef((nint)p));
                    break;
                }
                case FieldKind.Struct:
                    ScanRefsInStruct(seg, structPayloadOffset + sf.Offset, sf.StructType!, onRef);
                    break;
            }
    }

    private static void RewriteRefsInObject(Segment seg, int objectOffset, TypeDesc type, Dictionary<long, long> reloc)
    {
        foreach (var f in type.Fields)
            switch (f.Kind)
            {
                case FieldKind.Ref:
                {
                    var p = Mem.ReadRef64(seg.BasePtr, objectOffset, f.Offset);
                    if (p != 0 && reloc.TryGetValue(p, out var n))
                        Mem.WriteRef64(seg.BasePtr, objectOffset, f.Offset, n);
                    break;
                }
                case FieldKind.Struct:
                {
                    var structPayload = objectOffset + Layout.HeaderSize + f.Offset;
                    RewriteRefsInStruct(seg, structPayload, f.StructType!, reloc);
                    break;
                }
            }
    }

    private static void RewriteRefsInStruct(Segment seg, int structPayloadOffset, TypeDesc structType,
        Dictionary<long, long> reloc)
    {
        foreach (var sf in structType.Fields)
            switch (sf.Kind)
            {
                case FieldKind.Ref:
                {
                    var p = Mem.ReadI64(seg.BasePtr, structPayloadOffset + sf.Offset);
                    if (p != 0 && reloc.TryGetValue(p, out var n))
                        Mem.WriteI64(seg.BasePtr, structPayloadOffset + sf.Offset, n);
                    break;
                }
                case FieldKind.Struct:
                    RewriteRefsInStruct(seg, structPayloadOffset + sf.Offset, sf.StructType!, reloc);
                    break;
            }
    }

    private static void CopyUnmanagedToManaged(IntPtr srcBase, int srcOff, byte[] dst, int dstOff, int len)
    {
        var i = 0;
        for (; i + 8 <= len; i += 8)
            BitConverter.TryWriteBytes(new Span<byte>(dst, dstOff + i, 8), Mem.ReadI64(srcBase, srcOff + i));
        for (; i < len; i++)
            dst[dstOff + i] = Mem.ReadByte(srcBase, srcOff + i);
    }

    private static void CopyManagedToUnmanaged(byte[] src, int srcOff, IntPtr dstBase, int dstOff, int len)
    {
        var i = 0;
        for (; i + 8 <= len; i += 8)
        {
            var v = BitConverter.ToInt64(src, srcOff + i);
            Mem.WriteI64(dstBase, dstOff + i, v);
        }

        for (; i < len; i++)
            Mem.WriteByte(dstBase, dstOff + i, src[srcOff + i]);
    }

    private static void CopyUnmanagedToUnmanaged(IntPtr srcBase, int srcOff, IntPtr dstBase, int dstOff, int len)
    {
        var i = 0;
        for (; i + 8 <= len; i += 8)
            Mem.WriteI64(dstBase, dstOff + i, Mem.ReadI64(srcBase, srcOff + i));
        for (; i < len; i++)
            Mem.WriteByte(dstBase, dstOff + i, Mem.ReadByte(srcBase, srcOff + i));
    }
}