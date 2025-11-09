using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using GenerationalGC.Runtime.Core;

namespace GenerationalGC.Runtime.Engine
{
    /// <summary>
    /// Server-style runtime: one Heap per logical CPU; threads are mapped to a home heap.
    ///
    /// Implements a stop-the-world **parallel ephemeral GC** (Gen0/Gen1) with:
    ///  - Global mark-first (via concurrent CAS on a dictionary): each object
    ///    address is inserted once; losers skip enqueueing — prevents duplicate work.
    ///  - Per-heap worklists + **work stealing**: each worker prefers its home
    ///    heap’s queue and steals from others to balance load.
    ///  - Heap-agnostic **seeding**: scan all roots + old-gen dirty cards on all heaps
    ///    using a **global “is this address ephemeral?” predicate** (young on any heap).
    ///  - **Cross-heap routing**: when discovering an object, push it to the worklist
    ///    owned by the heap that contains that address.
    ///  - **Broadcast fixups**: after each heap compacts/promotes, we broadcast the
    ///    relocation table and ask every heap to rewrite its references (so cross-heap
    ///    old objects update pointers to young objects that moved).
    /// </summary>
    public sealed class ServerGcRuntime : IDisposable
    {
        // Thread→heap mapping: each mutator thread (and our GC workers by 'home' index)
        // has an integer index telling which heap it belongs to.
        private readonly ThreadLocal<int> _heapIndex;

        // All heaps in the runtime (one per logical CPU by default).
        private readonly List<Heap> _heaps = new();

        // Tracks globally registered type IDs so type layout is stable across heaps.
        private readonly HashSet<int> _registeredTypeIds = new();

        // Global monotonic TypeId counter (assigned the first time a type is registered).
        private int _nextGlobalTypeId = 1;

        // -------------------- Parallel mark state (re-created per GC) --------------------

        // Global mark set: address -> byte (value unused). We rely on TryAdd as a CAS to
        // ensure **mark-first** semantics. This is the single source of truth for "visited".
        private ConcurrentDictionary<long, byte> _visited = new();

        // Per-heap worklists: each index has a LIFO stack (stack == better cache locality).
        // Workers push discovered objects to the owner heap’s stack.
        private ConcurrentStack<WorkItem>[] _workByHeap = Array.Empty<ConcurrentStack<WorkItem>>();

        // Number of items currently being processed (popped but not yet finished).
        // Once queues are empty AND inflight==0, the mark is converged.
        private int _inflight;

        // Worker threads spin while !_stopRequested; we set this when GC finishes.
        private volatile bool _stopRequested;

        // Cached heap count for quick loops.
        private int _heapCount;

        public ServerGcRuntime()
        {
            // Default to one heap per logical CPU.
            var heapCount = Math.Max(1, Environment.ProcessorCount);
            for (var i = 0; i < heapCount; i++)
                _heaps.Add(new Heap(i.ToString()));

            // Map thread to its current CPU modulo heapCount (affine mapping).
            // (A simple round-robin mapping would also work.)
            _heapIndex = new ThreadLocal<int>(() =>
            {
                int cpu = Thread.GetCurrentProcessorId();
                return cpu % heapCount;
            }, trackAllValues: false);
        }

        public IEnumerable<Heap> AllHeaps => _heaps;
        public Heap CurrentHeap => _heaps[_heapIndex.Value];

        public void Dispose()
        {
            foreach (var h in _heaps) h.Dispose();
            _heapIndex.Dispose();
        }

        // =================================================================================
        //                                Type registration
        // =================================================================================

        public void RegisterType(TypeDesc type)
        {
            ArgumentNullException.ThrowIfNull(type);

            // Assign a **global** TypeId on first registration (so it is the same on all heaps).
            if (type.TypeId == 0)
            {
                type.TypeId = _nextGlobalTypeId++;
                type.ComputeLayout();
            }
            else if (!_registeredTypeIds.Contains(type.TypeId))
            {
                // If caller gave a fixed TypeId, ensure layout is computed once.
                if (type.Size == 0) type.ComputeLayout();
            }

            // Broadcast the type to all heaps.
            foreach (var h in _heaps)
                h.RegisterType(type);

            _registeredTypeIds.Add(type.TypeId);
        }

        // =================================================================================
        //                                Mutator forwards
        // =================================================================================

        public ObjRef Alloc(TypeDesc type, Generation forced = Generation.Gen0)
        {
            // Allocate on the current thread’s home heap.
            return CurrentHeap.Alloc(type, forced: forced);
        }

        public void SetRoot(string name, ObjRef value)
        {
            // Route to the heap that owns 'value' (or current heap if null/unknown).
            FindHeapByAddressOrCurrent(value).SetRoot(name, value);
        }

        public void SetInt32(ObjRef obj, string fieldName, int value)
        {
            FindHeapByAddressOrCurrent(obj).SetInt32(obj, fieldName, value);
        }

        public void SetRef(ObjRef obj, string fieldName, ObjRef child)
        {
            FindHeapByAddressOrCurrent(obj).SetRef(obj, fieldName, child);
        }

        public void SetStructRef(ObjRef obj, string structField, string nestedRefField, ObjRef child)
        {
            FindHeapByAddressOrCurrent(obj).SetStructRef(obj, structField, nestedRefField, child);
        }

        private Heap FindHeapByAddressOrCurrent(ObjRef r)
        {
            // Linear scan is fine for a small heap count; a global segment map would be faster.
            if (r.IsNull) return CurrentHeap;
            foreach (var h in _heaps)
                if (h.Contains(r.Address))
                    return h;
            return CurrentHeap;
        }

        // =================================================================================
        //                        Parallel Ephemeral GC (Stop-The-World)
        // =================================================================================

        public void CollectEphemeralAll_Parallel()
        {
            // --- STW begin (suspend mutators outside this sample) ---
            SetupParallelState();

            // Build a **global** ephemeral predicate: “is this address in any heap’s Gen0/Gen1?”
            // This lets remembered-set scanning and marking be heap-agnostic.
            var isEphemeralGlobal = BuildGlobalEphemeralPredicate();

            // **Seeding**: enqueue all ephemeral roots + all old->young pointers from dirty cards.
            // We do this across ALL heaps using the global predicate.
            SeedEphemeral(isEphemeralGlobal);

            // **Parallel mark**: workers pop, scan object refs, route discovered objects by owner.
            RunParallelMark(isEphemeralGlobal);

            // **Moves**: compact Gen0 and promote survivors to Gen1 (per-heap),
            // then **broadcast** relocation maps to every heap to rewrite references.
            DoMovesAndFixupsAfterEphemeral();

            // Post-GC housekeeping: toss thread-local nurseries and clear old-gen card tables.
            foreach (var h in _heaps)
            {
                h.InvalidateAllTlhs();
                h.ClearOldCards();
            }

            _stopRequested = true;
            // --- STW end (resume mutators) ---
        }

        /// <summary>
        /// Diagnostic helper: run **only** the parallel mark (no moving/compaction).
        /// Useful to verify graph closure across heaps and correctness of mark-first/routing.
        /// </summary>
        public void MarkEphemeralAll_Parallel()
        {
            SetupParallelState();
            var isEphemeralGlobal = BuildGlobalEphemeralPredicate();
            SeedEphemeral(isEphemeralGlobal);
            RunParallelMark(isEphemeralGlobal);
            _stopRequested = true;
        }

        // =================================================================================
        //                              Parallel GC infrastructure
        // =================================================================================

        /// <summary>
        /// Prepare per-GC global state: reset queues/mark set/counters.
        /// </summary>
        private void SetupParallelState()
        {
            _heapCount = _heaps.Count;

            // Ensure we have one work stack per heap (reused between GCs to avoid realloc).
            if (_workByHeap.Length != _heapCount)
                _workByHeap = Enumerable.Range(0, _heapCount)
                    .Select(_ => new ConcurrentStack<WorkItem>())
                    .ToArray();

            // Clear global mark set & counters.
            _visited.Clear();
            _inflight = 0;
            _stopRequested = false;
        }

        /// <summary>
        /// Returns a function that answers: “is this address in Gen0/Gen1 of ANY heap?”
        /// NOTE: For more speed, replace the naive scan with a global segment directory of
        /// ephemeral ranges (address-sorted ranges + binary search).
        /// </summary>
        private Func<nint, bool> BuildGlobalEphemeralPredicate()
        {
            return addr =>
            {
                return _heaps.Any(t => t.Contains(addr) && t.IsEphemeralByThisHeap(addr));
            };
        }

        /// <summary>
        /// Seed the global worklists with:
        ///   - Ephemeral roots from all heaps (thread roots, globals, region external roots).
        ///   - Old→ephemeral references found via card tables (remembered sets) in all heaps.
        /// Objects are **enqueued only if** they win the global mark-first CAS.
        /// </summary>
        private void SeedEphemeral(Func<nint, bool> isEphemeralGlobal)
        {
            // Ephemeral roots: each heap publishes its roots; we filter by global predicate.
            foreach (var h in _heaps)
                foreach (var r in h.EnumerateEphemeralRoots(isEphemeralGlobal))
                    EnqueueIfFirst(r);

            // Remembered set (OLD→YOUNG): scan old-gen dirty cards on every heap; discover
            // child refs that point into the global ephemeral range and enqueue them.
            foreach (var h in _heaps)
                h.ForEachEphemeralChildFromOldDirtyCards(isEphemeralGlobal, child => EnqueueIfFirst(child));
        }

        /// <summary>
        /// Launch one worker per heap. Each worker:
        ///   - Pops from its home queue, or steals from others if empty.
        ///   - Scans references of the popped object via the object’s owner heap.
        ///   - Filters to ephemeral-only (minor collection).
        ///   - Enqueues discovered children using **global mark-first** and
        ///     **owner-based routing**.
        /// Convergence rule: stop when all queues are empty AND _inflight == 0.
        /// </summary>
        private void RunParallelMark(Func<nint, bool> isEphemeralGlobal)
        {
            var threads = new List<Thread>(_heapCount);

            for (int i = 0; i < _heapCount; i++)
            {
                int home = i; // capture loop variable
                var t = new Thread(() => MarkWorker(home, isEphemeralGlobal))
                {
                    Name = $"GC-Marker-{home}",
                    IsBackground = true
                };
                threads.Add(t);
                t.Start();
            }

            // STW: just wait for all workers
            foreach (var t in threads) t.Join();
        }

        /// <summary>
        /// Worker loop:
        ///  - Try pop from home queue; if empty, try to steal from other queues.
        ///  - If no work in the system and inflight == 0 → terminate.
        ///  - For each popped object: increment _inflight, scan refs, enqueue children,
        ///    decrement _inflight.
        /// </summary>
        private void MarkWorker(int home, Func<nint, bool> isEphemeralGlobal)
        {
            var spin = new SpinWait();

            while (!_stopRequested)
            {
                // 1) Get work from home queue; if empty, attempt to steal.
                if (!_workByHeap[home].TryPop(out var item))
                {
                    bool stole = false;

                    // Simple round scan for stealing; could be randomized for fairness.
                    for (int i = 0; i < _heapCount; i++)
                    {
                        if (i == home) continue;
                        if (_workByHeap[i].TryPop(out item)) { stole = true; break; }
                    }

                    if (!stole)
                    {
                        // No local/stealable work right now.
                        // If every queue is empty AND nothing is inflight → we’re done.
                        if (AllQueuesEmpty() && Volatile.Read(ref _inflight) == 0)
                            break;

                        // Otherwise, spin briefly and retry (cheap busy-wait, STW GC).
                        spin.SpinOnce();
                        continue;
                    }
                }

                // 2) We got a work item: mark it “inflight” for convergence accounting.
                Interlocked.Increment(ref _inflight);
                try
                {
                    // Find which heap owns this object (linear scan; a directory would be faster).
                    int owner = item.Owner;
                    if (owner < 0) continue; // Not found (could be a stale address); skip.

                    var heap = _heaps[owner];

                    // 3) Scan all references inside 'item' using the owning heap (has its metadata).
                    heap.ForEachRef(item.Obj, child =>
                    {
                        if (child.IsNull) return;

                        // Minor GC boundary: ignore non-ephemeral edges.
                        if (!isEphemeralGlobal(child.Address)) return;

                        // 4) Enqueue child if it wins the **global mark-first** CAS,
                        //    routed to the owner heap’s queue (cross-heap routing).
                        EnqueueIfFirst(child);
                    });
                }
                finally
                {
                    // 5) Done with this item.
                    Interlocked.Decrement(ref _inflight);
                }
            }
        }

        /// <summary>
        /// Returns true if **every** per-heap worklist is empty right now (snapshot).
        /// </summary>
        private bool AllQueuesEmpty()
        {
            for (int i = 0; i < _heapCount; i++)
                if (!_workByHeap[i].IsEmpty) return false;
            return true;
        }

        /// <summary>
        /// Try to enqueue 'obj' into the owner heap’s queue **only if** it has not been
        /// seen before globally. This implements **mark-first**:
        ///   - The first thread that calls TryAdd(address) wins and enqueues.
        ///   - Everyone else observes the add failed and skips.
        /// </summary>
        
        private void EnqueueIfFirst(ObjRef obj)
        {
            if (obj.IsNull) return;
            if (!_visited.TryAdd((long)obj.Address, 0)) return;
            
            int owner = FindHeapIndexByAddress(obj);  // fast (see Option C)
            if (owner < 0) return;
            Console.WriteLine($"Enqueued obj {obj.Address:X}");
            _workByHeap[owner].Push(new WorkItem(obj, owner));
        }

        /// <summary>
        /// Map address→heap by scanning heaps. Good enough for small N; replace with a
        /// global segment directory (address range → heap index) for O(log N) or O(1).
        /// </summary>
        private int FindHeapIndexByAddress(ObjRef r)
        {
            for (int i = 0; i < _heapCount; i++)
                if (_heaps[i].Contains(r.Address)) return i;
            return -1;
        }

        // =================================================================================
        //                        Compaction/Promotion + global fixups
        // =================================================================================

        /// <summary>
        /// After mark completes:
        ///  1) For each heap: compact Gen0 into itself using the **global live set**.
        ///     Broadcast the relocation map to **every** heap to rewrite references.
        ///  2) For each heap: promote the remaining Gen0 survivors to Gen1.
        ///     Broadcast that relocation map as well.
        /// This keeps cross-heap references correct (e.g., an old object on heap A
        /// pointing to a young object that moved on heap B).
        /// </summary>
        private void DoMovesAndFixupsAfterEphemeral()
        {
            // Freeze the live set (addresses that won mark-first).
            var live = _visited.Count == 0
                ? new HashSet<long>()
                : new HashSet<long>(_visited.Keys);

            // 1) Gen0 compaction per heap, then **broadcast** relocations.
            foreach (var h in _heaps)
            {
                var relocCompaction = h.CompactGen0(live);
                if (relocCompaction.Count == 0) continue;

                // Every heap must fix references because any heap may store a pointer
                // to an object that just moved on heap 'h'.
                foreach (var target in _heaps)
                    target.FixReferencesInThisHeap(relocCompaction);
            }

            // 2) Gen0 → Gen1 promotion per heap, then broadcast relocations again.
            foreach (var h in _heaps)
            {
                var relocPromotion = h.PromoteGen0ToGen1();
                if (relocPromotion.Count == 0) continue;

                foreach (var target in _heaps)
                    target.FixReferencesInThisHeap(relocPromotion);
            }
        }

        // =================================================================================
        //                           Sequential (single-heap) fallbacks
        // =================================================================================

        /// <summary> Single-threaded mark-only across heaps (debug aid). </summary>
        public void MarkEphemeralAll(bool markAllOldCards = false)
        {
            foreach (var h in _heaps)
                h.MarkEphemeralOnly(markAllOldCards);
        }

        /// <summary> Single-threaded ephemeral GC per heap (legacy path). </summary>
        public void CollectEphemeralAll()
        {
            foreach (var h in _heaps)
                h.CollectEphemeral();
        }

        /// <summary> Single-threaded full mark-only across heaps. </summary>
        public void CollectFullAll()
        {
            foreach (var h in _heaps)
                h.CollectFull();
        }
    }
    
    readonly struct WorkItem(ObjRef obj, int owner)
    {
        public readonly ObjRef Obj = obj;
        public readonly int Owner = owner;
    }
}
