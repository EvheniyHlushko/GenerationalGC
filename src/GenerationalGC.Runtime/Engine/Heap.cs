using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GenerationalGC.Runtime.Core;
using GenerationalGC.Runtime.Reporting;

namespace GenerationalGC.Runtime.Engine
{
    public sealed class Heap : IDisposable
    {
        public const int LargeObjectThreshold = 85_000;
        public const int Gen0SizeBytes        = 1_000_000;
        public const int Gen1SizeBytes        = 1_000_000;
        public const int Gen2SizeBytes        = 2_000_000;
        public const int LohSizeBytes         = 2_000_000;
        public static int CardSizeBytes       = 256;
        public static int BrickSizeBytes      = 2048;
        public const int TlhSlabBytes         = 32 * 1024;

        private readonly Segment _gen0;
        private readonly Segment _gen1;
        private readonly Segment _gen2;
        private readonly Segment _loh;

        private readonly List<Segment> _segmentsByAddress = new();

        private readonly List<Region> _regions = new();
        private readonly Dictionary<string, ObjRef> _roots = new();
        private readonly Dictionary<int, TypeDesc> _types = new();

        private readonly ThreadLocal<ThreadLocalHeap> _tlsTlh =
            new(() => new ThreadLocalHeap($"tlh-{Thread.CurrentThread.ManagedThreadId}"), trackAllValues: true);

        private readonly IGen0Allocator _gen0Allocator;

        public string Name { get; }

        public Heap(string name)
        {
            Name = name;
            _gen0 = new Segment(Generation.Gen0, Gen0SizeBytes, CardSizeBytes);
            _gen1 = new Segment(Generation.Gen1, Gen1SizeBytes, CardSizeBytes);
            _gen2 = new Segment(Generation.Gen2, Gen2SizeBytes, CardSizeBytes);
            _loh  = new Segment(Generation.Loh , LohSizeBytes , CardSizeBytes);

            _segmentsByAddress.AddRange(new[] { _gen0, _gen1, _gen2, _loh });
            _segmentsByAddress.Sort((a, b) => a.BasePtr.ToInt64().CompareTo(b.BasePtr.ToInt64()));

            _gen0Allocator = new Gen0Allocator(_gen0, TlhSlabBytes);
        }

        public void Dispose()
        {
            foreach (var region in _regions.ToArray()) DisposeRegion(region);
            _gen0.Dispose(); _gen1.Dispose(); _gen2.Dispose(); _loh.Dispose();
            _segmentsByAddress.Clear();
            _roots.Clear();
            _types.Clear();
            _tlsTlh.Dispose();
        }

        public void RegisterType(TypeDesc type)
        {
            ArgumentNullException.ThrowIfNull(type);
            if (type.TypeId == 0) { type.TypeId = 1; type.ComputeLayout(); }
            if (type.Size == 0) type.ComputeLayout();
            _types[type.TypeId] = type;
        }

        // ------------------------------------------------------------ Mapping

        public bool Contains(nint address) => TryMap(address, out _, out _);

        private bool TryMap(nint address, out Segment seg, out int segmentOffset)
        {
            if (address == 0) { seg = null!; segmentOffset = -1; return false; }

            var abs = (long)address;
            int lo = 0, hi = _segmentsByAddress.Count - 1;
            while (lo <= hi)
            {
                var mid   = (lo + hi) >> 1;
                var s     = _segmentsByAddress[mid];
                var sBase = s.BasePtr.ToInt64();
                var sEnd  = sBase + s.SizeBytes;

                if      (abs <  sBase) hi = mid - 1;
                else if (abs >= sEnd ) lo = mid + 1;
                else { seg = s; segmentOffset = (int)(abs - sBase); return true; }
            }
            seg = null!; segmentOffset = -1; return false;
        }

        private (Segment seg, int off, TypeDesc type) Resolve(ObjRef r)
        {
            if (r.IsNull) throw new InvalidOperationException("Null reference.");
            if (!TryMap(r.Address, out var seg, out var off))
                throw new InvalidOperationException($"Address {r} not in any segment.");
            var typeId = Mem.ReadTypeId(seg.BasePtr, off);
            if (!_types.TryGetValue(typeId, out var t))
                throw new InvalidOperationException($"Unknown typeId {typeId} at {r}.");
            return (seg, off, t);
        }

        // ------------------------------------------------------------ Allocation

        public ObjRef Alloc(TypeDesc type, ThreadLocalHeap? explicitTlh = null, Generation forced = Generation.Gen0)
        {
            ArgumentNullException.ThrowIfNull(type);
            if (type.Kind != TypeKind.Class) throw new InvalidOperationException("Only classes are heap objects.");
            var sizeBytes = AlignUp(Layout.HeaderSize + type.Size, IntPtr.Size);

            if (sizeBytes >= LargeObjectThreshold || forced == Generation.Loh)
            {
                if (!_loh.TryAllocate(sizeBytes, out var off)) throw new OutOfMemoryException("LOH full.");
                Mem.WriteHeader(_loh.BasePtr, off, type.TypeId);
                var abs = _loh.BasePtr.ToInt64() + off;
                _loh.Bricks.OnAllocation(abs);
                return new ObjRef((nint)abs);
            }

            if (forced is Generation.Gen1 or Generation.Gen2)
            {
                var seg = forced == Generation.Gen1 ? _gen1 : _gen2;
                if (!seg.TryAllocate(sizeBytes, out var off)) throw new OutOfMemoryException($"{forced} full.");
                Mem.WriteHeader(seg.BasePtr, off, type.TypeId);
                var abs = seg.BasePtr.ToInt64() + off;
                seg.Bricks.OnAllocation(abs);
                return new ObjRef((nint)abs);
            }

            // Default: Gen0 via TLH + allocator
            var tlh = explicitTlh ?? _tlsTlh.Value;
            _gen0Allocator.EnsureTlh(tlh, sizeBytes, () =>
            {
                CollectEphemeral(); // local fallback (may be replaced by runtime parallel)
                return true;
            });

            var addr = _gen0Allocator.AllocateGen0(tlh, sizeBytes, type.TypeId);
            return new ObjRef(addr);
        }

        // ------------------------------------------------------------ Write barrier & roots

        public void SetRoot(string name, ObjRef value)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Root name must be nonempty.", nameof(name));
            _roots[name] = value;
        }

        public void SetInt32(ObjRef obj, string fieldName, int value)
        {
            var (seg, off, type) = Resolve(obj);
            var field = type.Fields.First(f => f.Name == fieldName);
            Mem.WriteI32(seg.BasePtr, off + Layout.HeaderSize + field.Offset, value);
        }

        public void SetRef(ObjRef obj, string fieldName, ObjRef child)
        {
            var (seg, off, type) = Resolve(obj);
            var field = type.Fields.First(f => f.Name == fieldName);
            Mem.WriteRef64(seg.BasePtr, off, field.Offset, child.Address);
            RecordWrite(obj, field.Offset, child);
        }

        public void SetStructRef(ObjRef obj, string structField, string nestedRefField, ObjRef child)
        {
            var (seg, off, type) = Resolve(obj);
            var sf = type.Fields.First(f => f.Name == structField);
            var st = sf.StructType!;
            var rf = st.Fields.First(f => f.Name == nestedRefField);

            var structPayload = off + Layout.HeaderSize + sf.Offset;
            Mem.WriteI64(seg.BasePtr, structPayload + rf.Offset, child.Address);

            RecordWrite(obj, sf.Offset + rf.Offset, child);
        }

        private void RecordWrite(ObjRef parent, int fieldPayloadOffset, ObjRef child)
        {
            if (child.IsNull) return;

            if (!TryMap(parent.Address, out var parentSeg, out var parentObjOff)) return;

            // Forbid GC→Region edges (region might be disposed)
            if (parentSeg.Gen is Generation.Gen0 or Generation.Gen1 or Generation.Gen2 or Generation.Loh)
            {
                if (TryMap(child.Address, out var childSegTmp, out _) && childSegTmp.Gen == Generation.Region)
                    throw new InvalidOperationException("GC-managed objects cannot hold references into a Region.");
            }

            // If an OLD parent writes a reference that *might* be ephemeral (in any heap), dirty the card.
            var parentIsOld = parentSeg.Gen is Generation.Gen1 or Generation.Gen2 or Generation.Loh;

            bool childCouldBeEphemeral;
            if (TryMap(child.Address, out var childSeg, out _))
            {
                childCouldBeEphemeral = (childSeg.Gen is Generation.Gen0 or Generation.Gen1) && childSeg.Gen != parentSeg.Gen;
            }
            else
            {
                // Cross-heap (unmapped here) -> conservative: treat as ephemeral.
                childCouldBeEphemeral = true;
            }

            if (parentIsOld && childCouldBeEphemeral)
            {
                var writeOffInSeg = parentObjOff + Layout.HeaderSize + fieldPayloadOffset;
                parentSeg.Cards?.MarkDirtyByOffset(writeOffInSeg);
            }

            // Region → GC: keep child alive while region lives (conservative root)
            if (parentSeg.Gen == Generation.Region &&
                TryMap(child.Address, out var childSeg2, out _) &&
                childSeg2.Gen is Generation.Gen0 or Generation.Gen1 or Generation.Gen2 or Generation.Loh)
            {
                var owner = _regions.FirstOrDefault(r => ReferenceEquals(r.Segment, parentSeg));
                owner?.AddExternalRoot(child.Address);
            }
        }

        // ------------------------------------------------------------ Mark-only (local)

        public void MarkEphemeralOnly(bool markAllOldCards = false)
        {
            if (markAllOldCards) { MarkAllCardsDirty(_gen1); MarkAllCardsDirty(_gen2); MarkAllCardsDirty(_loh); }

            var markSet = new HashSet<long>();
            var work = new Stack<ObjRef>();

            bool IsEphemeral(ObjRef r) =>
                TryMap(r.Address, out var s, out _) && (s.Gen == Generation.Gen0 || s.Gen == Generation.Gen1);

            foreach (var r in _roots.Values)
                if (!r.IsNull && IsEphemeral(r) && markSet.Add((long)r.Address)) work.Push(r);

            foreach (var region in _regions)
            foreach (var a in region.ExternalGcRoots)
            {
                var rr = new ObjRef((nint)a);
                if (IsEphemeral(rr) && markSet.Add(a)) work.Push(rr);
            }

            ScanDirtyCardsForGen0Children(_gen1, work, markSet);
            ScanDirtyCardsForGen0Children(_gen2, work, markSet);
            ScanDirtyCardsForGen0Children(_loh , work, markSet);

            Traverse(work, IsEphemeral, markSet);
        }

        private static void MarkAllCardsDirty(Segment seg)
        {
            for (var off = 0; off < seg.SizeBytes; off += 64)
                seg.Cards.MarkDirtyByOffset(off);
        }

        // ------------------------------------------------------------ Minor GC (local fallback)

        public void CollectEphemeral()
        {
            var markSet = new HashSet<long>();
            var work = new Stack<ObjRef>();

            bool IsEphemeral(ObjRef r) =>
                TryMap(r.Address, out var s, out _) && (s.Gen == Generation.Gen0 || s.Gen == Generation.Gen1);

            foreach (var r in _roots.Values)
                if (!r.IsNull && IsEphemeral(r) && markSet.Add((long)r.Address))
                    work.Push(r);

            foreach (var region in _regions)
            foreach (var a in region.ExternalGcRoots)
            {
                var rr = new ObjRef((nint)a);
                if (IsEphemeral(rr) && markSet.Add(a)) work.Push(rr);
            }

            ScanDirtyCardsForGen0Children(_gen1, work, markSet);
            ScanDirtyCardsForGen0Children(_gen2, work, markSet);
            ScanDirtyCardsForGen0Children(_loh , work, markSet);

            Traverse(work, IsEphemeral, markSet);

            var relocCompaction = CompactGen0(markSet);
            if (relocCompaction.Count > 0) FixReferencesInThisHeap(relocCompaction);

            var relocPromotion = PromoteGen0ToGen1();
            if (relocPromotion.Count > 0) FixReferencesInThisHeap(relocPromotion);

            foreach (var tlh in _tlsTlh.Values) tlh.Invalidate();
            _gen1.Cards.ClearAll(); _gen2.Cards.ClearAll(); _loh.Cards.ClearAll();
        }

        public void CollectFull()
        {
            var markSet = new HashSet<long>();
            var work = new Stack<ObjRef>();

            foreach (var r in _roots.Values)
                if (!r.IsNull && markSet.Add((long)r.Address)) work.Push(r);

            foreach (var region in _regions)
            foreach (var a in region.ExternalGcRoots)
                if (markSet.Add(a)) work.Push(new ObjRef((nint)a));

            Traverse(work, _ => true, markSet);

            _gen1.Cards.ClearAll(); _gen2.Cards.ClearAll(); _loh.Cards.ClearAll();
        }

        // ------------------------------------------------------------ Parallel-GC helpers (for runtime)

        internal bool TryMapPublic(nint address, out Segment seg, out int off)
            => TryMap(address, out seg, out off);

        internal bool IsEphemeralByThisHeap(nint address)
        {
            if (!TryMap(address, out var s, out _)) return false;
            return s.Gen is Generation.Gen0 or Generation.Gen1;
        }

        /// Enumerate this heap’s ephemeral roots (requires global predicate for cross-heap correctness).
        internal IEnumerable<ObjRef> EnumerateEphemeralRoots(Func<nint, bool> isEphemeralGlobal)
        {
            foreach (var r in _roots.Values)
                if (!r.IsNull && isEphemeralGlobal(r.Address))
                    yield return r;

            foreach (var region in _regions)
            foreach (var a in region.ExternalGcRoots)
            {
                var rr = new ObjRef((nint)a);
                if (!rr.IsNull && isEphemeralGlobal(rr.Address))
                    yield return rr;
            }
        }

        /// Scan this heap’s OLD gens’ dirty cards, push children that are ephemeral by the GLOBAL predicate.
        internal void ForEachEphemeralChildFromOldDirtyCards(Func<nint, bool> isEphemeralGlobal, Action<ObjRef> onEphemeralChild)
        {
            ScanDirtyCardsForEphemeralChildren(_gen1, isEphemeralGlobal, onEphemeralChild);
            ScanDirtyCardsForEphemeralChildren(_gen2, isEphemeralGlobal, onEphemeralChild);
            ScanDirtyCardsForEphemeralChildren(_loh , isEphemeralGlobal, onEphemeralChild);
        }

        /// Invoke onRef for each reference field of obj (no filtering here).
        internal void ForEachRef(ObjRef obj, Action<ObjRef> onRef)
        {
            if (obj.IsNull) return;
            if (!TryMap(obj.Address, out var seg, out var off)) return;

            var typeId = Mem.ReadTypeId(seg.BasePtr, off);
            if (!_types.TryGetValue(typeId, out var t)) return;

            ScanRefsInObject(seg, off, t, onRef);
        }

        public void ClearOldCards()
        {
            _gen1.Cards.ClearAll();
            _gen2.Cards.ClearAll();
            _loh.Cards.ClearAll();
        }

        public void InvalidateAllTlhs()
        {
            foreach (var tlh in _tlsTlh.Values) tlh.Invalidate();
        }

        private void ScanDirtyCardsForEphemeralChildren(Segment seg, Func<nint, bool> isEphemeralGlobal, Action<ObjRef> onEphemeralChild)
        {
            foreach (var (start, end) in seg.Cards.DirtyRanges())
            {
                var absStart = seg.BasePtr.ToInt64() + start; // inclusive
                var absEnd   = seg.BasePtr.ToInt64() + end;   // exclusive

                var scanAbs = seg.Bricks.SnapToObjectStart(absStart);
                if (scanAbs < seg.BasePtr.ToInt64()) scanAbs = seg.BasePtr.ToInt64();

                while (scanAbs < absEnd)
                {
                    if (!TryMap((nint)scanAbs, out var owner, out var objOff)) break;
                   // if (!ReferenceEquals(owner, seg)) break;

                    var typeId = Mem.ReadTypeId(owner.BasePtr, objOff);
                    if (!_types.TryGetValue(typeId, out var t)) break;
                    var objSize  = AlignUp(Layout.HeaderSize + t.Size, IntPtr.Size);
                    var objStart = scanAbs;
                    var objEnd   = objStart + objSize;

                    if (objEnd <= absStart) {
                        Console.WriteLine($"Skipping obj x{scanAbs:X}");
                        scanAbs = objEnd; 
                        continue; 
                    }
                    
                    if (objStart >= absEnd) break;
                    
                    Console.WriteLine($"Scanning obj x{scanAbs:X}");
                    

                    ScanRefsInObject(owner, objOff, t, child =>
                    {
                        if (child.IsNull) return;
                        if (isEphemeralGlobal(child.Address))
                            onEphemeralChild(child);
                    });

                    scanAbs = objEnd;
                }
                // optional: seg.Cards.ClearRange(start, end);
            }
        }

        // ------------------------------------------------------------ Internal traversal & relocation

        private void ScanDirtyCardsForGen0Children(Segment seg, Stack<ObjRef> work, HashSet<long> markSet)
        {
            var dirtyRanges = seg.Cards.DirtyRanges();
            foreach (var (start, end) in dirtyRanges)
            {
                var absStart = seg.BasePtr.ToInt64() + start;
                var absEnd   = seg.BasePtr.ToInt64() + end;

                var scanAbs = seg.Bricks.SnapToObjectStart(absStart);
                if (scanAbs < seg.BasePtr.ToInt64()) scanAbs = seg.BasePtr.ToInt64();

                while (scanAbs < absEnd)
                {
                    if (!TryMap((nint)scanAbs, out var owner, out var objOff)) break;
                    if (!ReferenceEquals(owner, seg)) break;

                    var typeId = Mem.ReadTypeId(owner.BasePtr, objOff);
                    if (!_types.TryGetValue(typeId, out var t)) break;
                    var objSize  = AlignUp(Layout.HeaderSize + t.Size, IntPtr.Size);
                    var objStart = scanAbs;
                    var objEnd   = objStart + objSize;

                    if (objEnd <= absStart) { scanAbs = objEnd; continue; }
                    if (objStart >= absEnd) break;

                    ScanRefsInObject(owner, objOff, t, child =>
                    {
                        if (child.IsNull) return;
                        if (TryMap(child.Address, out var childSeg, out _) && childSeg.Gen == Generation.Gen0)
                        {
                            var a = (long)child.Address;
                            if (markSet.Add(a)) work.Push(child);
                        }
                    });

                    scanAbs = objEnd;
                }
            }
        }

        private void Traverse(Stack<ObjRef> work, Func<ObjRef, bool> follow, HashSet<long> markSet)
        {
            while (work.Count > 0)
            {
                var cur = work.Pop();
                if (cur.IsNull) continue;
                if (!TryMap(cur.Address, out var seg, out var off)) continue;

                var typeId = Mem.ReadTypeId(seg.BasePtr, off);
                if (!_types.TryGetValue(typeId, out var type)) continue;

                ScanRefsInObject(seg, off, type, child =>
                {
                    if (child.IsNull) return;
                    var addr = (long)child.Address;
                    if (markSet.Add(addr) && follow(child)) work.Push(child);
                });
            }
        }

        internal Dictionary<long, long> CompactGen0(HashSet<long> markSet)
        {
            if (_gen0.AllocPtr == 0)
            {
               // _gen0.Bricks.ClearAll();
                return new();
            }

            var survivors = new List<(int Off, TypeDesc T, int Size)>();
            var cursor = 0;

            while (cursor < _gen0.AllocPtr)
            {
                var typeId = Mem.ReadTypeId(_gen0.BasePtr, cursor);
                if (!_types.TryGetValue(typeId, out var t)) break;
                var size = AlignUp(Layout.HeaderSize + t.Size, IntPtr.Size);

                var abs = _gen0.BasePtr.ToInt64() + cursor;
                if (markSet.Contains(abs)) survivors.Add((cursor, t, size));

                cursor += size;
            }


            if (survivors.Count == 0)
            {
                _gen0.ResetNurseryLayout();
                return new();
            }

            var scratch = new byte[_gen0.SizeBytes];
            var writePtr = 0;
            var reloc = new Dictionary<long, long>(survivors.Count);

            foreach (var s in survivors)
            {
                CopyUnmanagedToManaged(_gen0.BasePtr, s.Off, scratch, writePtr, s.Size);

                var oldAbs = _gen0.BasePtr.ToInt64() + s.Off;
                var newAbs = _gen0.BasePtr.ToInt64() + writePtr;
                reloc[oldAbs] = newAbs;

                writePtr += s.Size;
            }

            CopyManagedToUnmanaged(scratch, 0, _gen0.BasePtr, 0, writePtr);
            Mem.Zero(_gen0.BasePtr, writePtr, _gen0.SizeBytes - writePtr);
            _gen0.AllocPtr = writePtr;

            return reloc;
        }

        internal Dictionary<long, long> PromoteGen0ToGen1()
        {
            var reloc = new Dictionary<long, long>();
            var cursor = 0;

            while (cursor < _gen0.AllocPtr)
            {
                var typeId = Mem.ReadTypeId(_gen0.BasePtr, cursor);
                if (!_types.TryGetValue(typeId, out var t)) break;
                var size = AlignUp(Layout.HeaderSize + t.Size, IntPtr.Size);

                if (!_gen1.TryAllocate(size, out var dstOff))
                    throw new OutOfMemoryException("Gen1 full during promotion.");

                CopyUnmanagedToUnmanaged(_gen0.BasePtr, cursor, _gen1.BasePtr, dstOff, size);

                var oldAbs = _gen0.BasePtr.ToInt64() + cursor;
                var newAbs = _gen1.BasePtr.ToInt64() + dstOff;
                reloc[oldAbs] = newAbs;

                _gen1.Bricks.OnAllocation(newAbs);
                cursor += size;
            }

        //    _gen0.Bricks.ClearAll();
            _gen0.ResetNurseryLayout();
            return reloc;
        }

        public void FixReferencesInThisHeap(Dictionary<long, long> reloc)
        {
            if (reloc.Count == 0) return;

            foreach (var key in _roots.Keys.ToList())
            {
                var r = _roots[key];
                if (r.IsNull) continue;
                var a = (long)r.Address;
                if (reloc.TryGetValue(a, out var n))
                    _roots[key] = new ObjRef((nint)n);
            }

            FixSegmentRefs(_gen0, reloc);
            FixSegmentRefs(_gen1, reloc);
            FixSegmentRefs(_gen2, reloc);
            FixSegmentRefs(_loh , reloc);
        }

        private void FixSegmentRefs(Segment seg, Dictionary<long, long> reloc)
        {
            var cursor = 0;
            while (cursor < seg.AllocPtr)
            {
                var typeId = Mem.ReadTypeId(seg.BasePtr, cursor);
                if (!_types.TryGetValue(typeId, out var t)) break;
                var size = AlignUp(Layout.HeaderSize + t.Size, IntPtr.Size);

                RewriteRefsInObject(seg, cursor, t, reloc);
                cursor += size;
            }
        }

        private static void ScanRefsInObject(Segment seg, int objectOffset, TypeDesc type, Action<ObjRef> onRef)
        {
            foreach (var f in type.Fields)
            {
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
        }

        private static void ScanRefsInStruct(Segment seg, int structPayloadOffset, TypeDesc structType, Action<ObjRef> onRef)
        {
            foreach (var sf in structType.Fields)
            {
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
        }

        private static void RewriteRefsInObject(Segment seg, int objectOffset, TypeDesc type, Dictionary<long, long> reloc)
        {
            foreach (var f in type.Fields)
            {
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
        }

        private static void RewriteRefsInStruct(Segment seg, int structPayloadOffset, TypeDesc structType, Dictionary<long, long> reloc)
        {
            foreach (var sf in structType.Fields)
            {
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
        }

        // ------------------------------------------------------------ Regions

        public Region CreateRegion(int capacityBytes = TlhSlabBytes)
        {
            var regionSeg = new Segment(Generation.Region, AlignUp(capacityBytes, IntPtr.Size), CardSizeBytes);
            var region    = new Region(regionSeg);
            region.RegionDisposed += (_, _) => DisposeRegion(region);

            _regions.Add(region);
            _segmentsByAddress.Add(regionSeg);
            _segmentsByAddress.Sort((a, b) => a.BasePtr.ToInt64().CompareTo(b.BasePtr.ToInt64()));
            return region;
        }

        public ObjRef AllocInRegion(TypeDesc type, Region region)
        {
            if (type.Kind != TypeKind.Class) throw new InvalidOperationException("Only classes are heap objects.");
            var sizeBytes = AlignUp(Layout.HeaderSize + type.Size, IntPtr.Size);
            if (!region.Segment.TryAllocate(sizeBytes, out var off))
                throw new OutOfMemoryException("Region capacity exhausted.");
            Mem.WriteHeader(region.Segment.BasePtr, off, type.TypeId);
            var abs = region.Segment.BasePtr.ToInt64() + off;
            return new ObjRef((nint)abs);
        }

        private void DisposeRegion(Region region)
        {
            _segmentsByAddress.Remove(region.Segment);
            region.Segment.Dispose();
            region.ExternalGcRoots.Clear();
            _regions.Remove(region);
        }

        // ------------------------------------------------------------ Reporting

        public HeapReport GetReport()
        {
            var report = new HeapReport();
            foreach (var kv in _roots) report.Roots.Add(new RootEntry(kv.Key, kv.Value.Address));
            report.Segments.Add(BuildSegmentReport(_gen0));
            report.Segments.Add(BuildSegmentReport(_gen1));
            report.Segments.Add(BuildSegmentReport(_gen2));
            report.Segments.Add(BuildSegmentReport(_loh));
            foreach (var region in _regions) report.Segments.Add(BuildSegmentReport(region.Segment));
            return report;
        }

        private SegmentReport BuildSegmentReport(Segment seg)
        {
            var sr = new SegmentReport(seg.Gen, (ulong)seg.BasePtr.ToInt64(), seg.SizeBytes, seg.AllocPtr, seg.Cards?.CountDirty() ?? 0);
            int cursor = 0, index = 0;
            while (cursor < seg.AllocPtr)
            {
                var typeId = Mem.ReadTypeId(seg.BasePtr, cursor);
                if (!_types.TryGetValue(typeId, out var t)) break;
                var size = AlignUp(Layout.HeaderSize + t.Size, IntPtr.Size);
                var abs = (ulong)(seg.BasePtr.ToInt64() + cursor);

                var instance = new ObjectInstance(index++, t.Name, abs, size);
                foreach (var f in t.Fields)
                {
                    switch (f.Kind)
                    {
                        case FieldKind.Int32:
                            instance.Fields.Add(new FieldValue(f.Name, FieldKind.Int32, Mem.ReadI32(seg.BasePtr, cursor + Layout.HeaderSize + f.Offset), 0));
                            break;
                        case FieldKind.Long:
                            instance.Fields.Add(new FieldValue(f.Name, FieldKind.Long, Mem.ReadI64(seg.BasePtr, cursor + Layout.HeaderSize + f.Offset), 0));
                            break;
                        case FieldKind.Decimal:
                            instance.Fields.Add(new FieldValue(f.Name, FieldKind.Decimal, Mem.ReadDecimal(seg.BasePtr, cursor + Layout.HeaderSize + f.Offset), 0));
                            break;
                        case FieldKind.Ref:
                        {
                            var p = Mem.ReadRef64(seg.BasePtr, cursor, f.Offset);
                            instance.Fields.Add(new FieldValue(f.Name, FieldKind.Ref, null, (ulong)(p < 0 ? 0 : p)));
                            break;
                        }
                        case FieldKind.Struct:
                        {
                            var summary = SummarizeStruct(seg, cursor + Layout.HeaderSize + f.Offset, f.StructType!);
                            instance.StructSummaries.Add(new StructSummary(f.Name, summary));
                            break;
                        }
                    }
                }

                sr.Objects.Add(instance);
                cursor += size;
            }
            return sr;
        }

        private static List<FieldValue> SummarizeStruct(Segment seg, int structPayloadOffset, TypeDesc structType)
        {
            var list = new List<FieldValue>();
            foreach (var sf in structType.Fields)
            {
                switch (sf.Kind)
                {
                    case FieldKind.Int32:
                        list.Add(new FieldValue(sf.Name, FieldKind.Int32, Mem.ReadI32(seg.BasePtr, structPayloadOffset + sf.Offset), 0));
                        break;
                    case FieldKind.Long:
                        list.Add(new FieldValue(sf.Name, FieldKind.Long, Mem.ReadI64(seg.BasePtr, structPayloadOffset + sf.Offset), 0));
                        break;
                    case FieldKind.Decimal:
                        list.Add(new FieldValue(sf.Name, FieldKind.Decimal, Mem.ReadDecimal(seg.BasePtr, structPayloadOffset + sf.Offset), 0));
                        break;
                    case FieldKind.Ref:
                    {
                        var p = Mem.ReadI64(seg.BasePtr, structPayloadOffset + sf.Offset);
                        list.Add(new FieldValue(sf.Name, FieldKind.Ref, null, (ulong)(p < 0 ? 0 : p)));
                        break;
                    }
                    case FieldKind.Struct:
                    {
                        var nested = SummarizeStruct(seg, structPayloadOffset + sf.Offset, sf.StructType!);
                        list.Add(new FieldValue(sf.Name, FieldKind.Struct, nested, 0));
                        break;
                    }
                }
            }
            return list;
        }

        // ------------------------------------------------------------ utils

        private static int AlignUp(int v, int a) => (v + (a - 1)) & ~(a - 1);

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
}
