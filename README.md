# Gc — A Production-Leaning Generational GC Model (C#)

> A compact, heavily-commented reference implementation of a .NET-style generational GC using unmanaged memory, raw pointer references (`nint`), card tables (bitmap-like), a heap-wide brick index, thread-local allocation (TLH) for Gen0, minor (ephemeral) GC with compaction + promotion, and optional non-moving regions/arenas with free-all semantics.

---

## Why this exists

- Mirrors key CLR GC concepts while staying approachable.
- Uses unmanaged memory per segment so object references are actual addresses.
- No console I/O in the engine; exposes a report object for inspection/tests.
- Clean, modular structure — easy to extend to multi-segment, pinning, background GC, etc.

---

## Core Architecture

### Segments (unmanaged)

- Gen0 / Gen1 / Gen2 / LOH / Region each have a `Segment`:
    - Unmanaged buffer (`IntPtr BasePtr`, `SizeBytes`), bump pointer `AllocPtr`
    - Card table (bitmap-like) per segment for write barrier tracking
- Sorted vector of segments by base address for fast address→segment mapping.

### Object Model

- Objects are `[Header | Payload]`:
    - Header: 16 bytes — `[SyncBlock(8)][MethodTable(8=TypeId)]`
    - Payload: computed by `TypeDesc` (explicit alignment/padding)
- References are raw 64-bit addresses (`nint`) into segments.

### Type System

- Fixed-size, C-like types only (classes and nested structs).
- Arrays/variable-sized objects are out of scope for simplicity.

### Thread-Local Heap (TLH)

- Gen0 allocation uses TLH slabs (e.g., 32 KiB) carved from the Gen0 segment.
- `ThreadLocalHeap` holds `{ SlabStart, SlabCursor, SlabLimit }`.
- When a slab is full, reserve a new slab (or trigger minor GC first).

### Write Barrier (Old → Gen0)

- On every reference write, if `parent ∈ {Gen1, Gen2, LOH}` and `child ∈ Gen0`:
    - Mark the card covering that write as dirty in the parent’s segment.
- During minor GC, only dirty card ranges are scanned for Gen0 children.

---

## Data Structures (bitmaps & indexes)

### Card Table (bitmap-like, per segment)

- A card covers a small contiguous range of bytes in a segment (default: 256 B).
- Implementation uses 1 byte per card (`0 = clean`, `1 = dirty`). Conceptually, this is a bitmap.
- Overhead: `segmentSize / cardSize`.
    - Example: 32 KiB segment / 256 B per card ⇒ 128 cards ⇒ 128 bytes overhead.
- Purpose: bound scanning during minor GC to just modified ranges in old generations.

ASCII intuition:

```
Segment bytes (e.g. 32 KiB)
[--256B card0--][--256B card1--] ... [--card127--]

CardTable bytes:
index 0..127, value {0=clean, 1=dirty}
```

Real runtimes pick a small size that balances precision and overhead, and fits CPU cache lines well (e.g., 64–512 B). 256 is a common sweet spot; tweak to explore trade-offs.

### Brick Index (heap-wide coarse index)

- For each brick (e.g., 2 KiB), store the last object start ≤ brick start.
- Lets the collector snap an arbitrary interior address to a nearby object start.
- Used when scanning dirty card ranges (you know a byte range, not object boundaries).

Arbitrary interior address: any address inside an object (not necessarily the header).  
Given a dirty range `[A, B)`, we:
1. Snap A back to a known object start with the brick index.
2. Walk object-by-object until we pass `B`.

---

## Algorithms

### Minor (Ephemeral) GC (Gen0 + Gen1)

1. Mark:
    - Seed with roots that point into Gen0/Gen1.
    - Add Gen0 children while scanning dirty cards in Gen1/Gen2/LOH.
    - Traverse only inside Gen0/Gen1.
2. Compact Gen0:
    - Copy survivors densely within Gen0; build relocation map (oldAbs→newAbs).
    - Rebuild brick entries for the Gen0 address range.
    - Fix all references (roots + heap) via relocation map.
3. Promote Gen0 → Gen1:
    - Copy remaining Gen0 survivors into Gen1; add entries to the brick index.
    - Apply relocation, fix references again.
4. Reset TLHs and clear dirty cards in old generations.

### Full GC (mark-only)

- Mark from all roots (including region→GC external roots).
- Traversal visits all generations.
- Sweep/compaction of Gen2/LOH is intentionally omitted to keep the model compact.

---

## Regions (Arenas)

- Region is a non-moving arena with free-all semantics:
    - `AllocInRegion(type, region)` bumps inside the region’s segment.
    - Disposing the region makes all its objects unreachable immediately.
- Reference policy:
    - GC→Region references are disallowed (could dangle after `Dispose()`).
    - Region→GC references are allowed; we record these addresses as external GC roots so the GC keeps them alive while the region lives.
- Typical uses:
    - Transient graphs with clear lexical lifetime.
    - High-throughput allocations with cheap teardown (`Dispose()`).

---


## Benchmarks

| Method                               | Count  | Mean      | Error     | StdDev    | Median    | Ratio | RatioSD | Gen0      | Allocated | Alloc Ratio |
|------------------------------------- |------- |----------:|----------:|----------:|----------:|------:|--------:|----------:|----------:|------------:|
| 'GC: Gen0 TLH (minor GCs as needed)' | 10000  |  1.574 ms | 0.2579 ms | 0.7605 ms |  1.333 ms |  1.31 |    0.98 |         - |   2.52 MB |        1.00 |                                                                                                                                                                   
| 'GC: Region (arena) free-all'        | 10000  |  2.126 ms | 0.0404 ms | 0.0758 ms |  2.107 ms |  1.76 |    0.92 |         - |   1.91 MB |        0.76 |
|                                      |        |           |           |           |           |       |         |           |           |             |
| 'GC: Gen0 TLH (minor GCs as needed)' | 50000  |  5.435 ms | 0.1071 ms | 0.2139 ms |  5.399 ms |  1.00 |    0.05 | 1000.0000 |  12.59 MB |        1.00 |
| 'GC: Region (arena) free-all'        | 50000  |  2.852 ms | 0.0682 ms | 0.1748 ms |  2.844 ms |  0.53 |    0.04 | 1000.0000 |   9.54 MB |        0.76 |
|                                      |        |           |           |           |           |       |         |           |           |             |
| 'GC: Gen0 TLH (minor GCs as needed)' | 200000 | 27.924 ms | 0.2964 ms | 0.2314 ms | 27.954 ms |  1.00 |    0.01 | 6000.0000 |  50.36 MB |        1.00 |
| 'GC: Region (arena) free-all'        | 200000 | 10.750 ms | 0.2301 ms | 0.6711 ms | 10.691 ms |  0.39 |    0.02 | 4000.0000 |  38.15 MB |        0.76 |

~~~~