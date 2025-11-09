using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenerationalGC.Runtime.Core;
using GenerationalGC.Runtime.Engine;
using GenerationalGC.Runtime.Reporting;
using Xunit;

namespace GenerationalGC.Tests
{
    [CollectionDefinition("gc-tests", DisableParallelization = true)]
    public class GcCollectionDefinition { }

    [Collection("gc-tests")]
    public class GenerationalGcPerCoreTests
    {
        private static void RegisterAll(ServerGcRuntime rt)
        {
            rt.RegisterType(ExampleTypes.Location);
            rt.RegisterType(ExampleTypes.Node);
            rt.RegisterType(ExampleTypes.Holder);
        }

        private static Heap OwningHeap(ServerGcRuntime rt, ObjRef obj) =>
            rt.AllHeaps.First(h => h.Contains(obj.Address));

        private static (SegmentReport gen0, SegmentReport gen1, SegmentReport gen2, SegmentReport loh)
            GetSegs(Heap heap)
        {
            var rep = heap.GetReport();
            var seg0 = rep.Segments.Single(s => s.Gen == Generation.Gen0);
            var seg1 = rep.Segments.Single(s => s.Gen == Generation.Gen1);
            var seg2 = rep.Segments.Single(s => s.Gen == Generation.Gen2);
            var loh  = rep.Segments.Single(s => s.Gen == Generation.Loh);
            return (seg0, seg1, seg2, loh);
        }

        // --------------------------------------------------------------------
        // 1) Per-thread heap assignment is round-robin-ish and thread-local
        // --------------------------------------------------------------------
        [Fact]
        public void PerThreadHeap_AssignsDifferentHeaps()
        {
            using var rt = new ServerGcRuntime();
            RegisterAll(rt);

            // Capture the heap name seen on each new thread
            var seen = new ConcurrentBag<string>();

            // Spin up a few threads and record their home heap
            var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                // Touch CurrentHeap to force assignment for this thread
                var name = rt.CurrentHeap.Name;
                seen.Add(name);
            })).ToArray();

            Task.WaitAll(tasks);

            // We should see at least 2 distinct heap names (on most machines > 1 CPU)
            var distinct = seen.Distinct().Count();
            Assert.True(distinct >= 2,
                $"Expected ≥ 2 distinct heaps across threads, saw {distinct}: [{string.Join(", ", seen)}]");
        }

        // --------------------------------------------------------------------
        // 2) Dirty card when OLD -> Gen0 store (barrier on parent’s heap)
        // --------------------------------------------------------------------
        [Fact]
        public void DirtyCards_OldToGen0Write_DirtiesParentGen1()
        {
            using var rt = new ServerGcRuntime();
            RegisterAll(rt);

            // Make Gen0 child on MAIN thread / its heap
            var young = rt.Alloc(ExampleTypes.Node); // Gen0
            rt.SetInt32(young, "Id", 1);

            // Make Gen1 parent on ANOTHER thread (so likely a different heap), then OLD->Gen0 store
            ObjRef holder = default;
            Task.Run(() =>
            {
                var h = rt.Alloc(ExampleTypes.Holder, forced: Generation.Gen1); // OLD
                rt.SetRef(h, "Child", young); // OLD -> Gen0  (should dirty a card on parent's heap Gen1)
                holder = h;
            }).Wait();

            var parentHeap = OwningHeap(rt, holder);
            var (_, gen1, _, _) = GetSegs(parentHeap);

            Assert.True(gen1.DirtyCardCount > 0,
                $"Expected ≥1 dirty Gen1 card from OLD→Gen0 write, got {gen1.DirtyCardCount}.");
        }

        // --------------------------------------------------------------------
        // 4) Minor GC: Gen0 compaction+promotion and reference fixups
        //
        // NOTE: To avoid depending on cross-heap scanning (not present in your
        // Heap.CollectEphemeral), we deliberately place parent+child on the SAME heap.
        // --------------------------------------------------------------------
        [Fact]
        public void MinorGc_Gen0PromotionAndFixups_SameHeap()
        {
            using var rt = new ServerGcRuntime();
            RegisterAll(rt);

            // Force all work on the SAME heap (main thread).
            // Parent is OLD (Gen1), Child is Gen0 on same heap.
            var parent = rt.Alloc(ExampleTypes.Holder, forced: Generation.Gen1);
            var child  = rt.Alloc(ExampleTypes.Node); // Gen0
            rt.SetInt32(child, "Id", 123);
            rt.SetRef(parent, "Child", child);
            rt.SetRoot("rootParent", parent);

            var heap = OwningHeap(rt, parent);

            // Sanity: before collection, child lives in Gen0 on the same heap
            {
                var rep = heap.GetReport();
                var gen0 = rep.Segments.Single(s => s.Gen == Generation.Gen0);
                Assert.True(gen0.AllocatedBytes > 0, "Expected Gen0 to have allocations before the minor GC.");
            }

            // Run minor collection across all heaps (each heap does local compaction/promotion)
            rt.CollectEphemeralAll();

            // After collection:
            //  - Gen0 on that heap should be compacted and then emptied by promotion
            //  - The parent's Child reference should point to a Gen1 address (fixups applied)
            {
                var rep = heap.GetReport();

                var gen0 = rep.Segments.Single(s => s.Gen == Generation.Gen0);
                Assert.True(gen0.AllocatedBytes == 0, $"Expected Gen0 to be empty after promotion. Alloc={gen0.AllocatedBytes}");

                var gen1 = rep.Segments.Single(s => s.Gen == Generation.Gen1);
                // Find the Holder instance and read its Child ref
                var holderObj = gen1.Objects.FirstOrDefault(o => o.TypeName == "Holder");
                Assert.NotNull(holderObj);

                var childField = holderObj!.Fields.FirstOrDefault(f => f.Name == "Child" && f.Kind == FieldKind.Ref);
                Assert.NotNull(childField);

                var childPtr = childField!.RefAddress;
                // It must be within the Gen1 address range for this heap
                var gen1Start = (ulong)gen1.Base;
                var gen1End   = gen1Start + (ulong)gen1.SizeBytes;
                Assert.True(childPtr >= gen1Start && childPtr < gen1End,
                    "After minor GC, Holder.Child should reference a Gen1 object (promoted).");
            }
        }

        // --------------------------------------------------------------------
        // 5) Before/After: Mark vs Collect
        // --------------------------------------------------------------------
        [Fact]
        public void BeforeAfter_MarkOnly_vs_Collect()
        {
            using var rt = new ServerGcRuntime();
            RegisterAll(rt);

            // Prepare: OLD object referencing a Gen0 object on the same heap (ensure barrier fires)
            var parent = rt.Alloc(ExampleTypes.Holder, forced: Generation.Gen1);
            var child  = rt.Alloc(ExampleTypes.Node); // Gen0
            rt.SetRef(parent, "Child", child);
            rt.SetRoot("p", parent);

            var heap = OwningHeap(rt, parent);

            // Snapshot A: Pre-mark
            var pre = heap.GetReport();
            var preGen1Cards = pre.Segments.Single(s => s.Gen == Generation.Gen1).DirtyCardCount;
            var preGen0Alloc = pre.Segments.Single(s => s.Gen == Generation.Gen0).AllocatedBytes;

            // Mark-only (visualization)
            rt.MarkEphemeralAll(markAllOldCards: false);
            var mid = heap.GetReport();
            var midGen1Cards = mid.Segments.Single(s => s.Gen == Generation.Gen1).DirtyCardCount;
            var midGen0Alloc = mid.Segments.Single(s => s.Gen == Generation.Gen0).AllocatedBytes;

            // Cards should remain at least as dirty (mark doesn't clear), gen0 alloc unchanged
            Assert.True(midGen1Cards >= preGen1Cards, "Mark-only should not reduce dirty cards.");
            Assert.Equal(preGen0Alloc, midGen0Alloc);

            // Minor collect
            rt.CollectEphemeralAll();
            var post = heap.GetReport();
            var postGen1Cards = post.Segments.Single(s => s.Gen == Generation.Gen1).DirtyCardCount;
            var postGen0Alloc = post.Segments.Single(s => s.Gen == Generation.Gen0).AllocatedBytes;

            // After collect, old-gen cards are cleared; Gen0 was compacted+promoted so 0 alloc
            Assert.Equal(0, postGen1Cards);
            Assert.Equal(0, postGen0Alloc);
        }


        [Fact]
        public void Allign()
        {
            var testStruct1 = new TypeDesc()
            {
                Name = "TestStruct1",
                Kind = TypeKind.Struct,
                Fields =
                {
                    new FieldDesc { Name = "X", Kind = FieldKind.Int32 },
                    new FieldDesc { Name = "Y", Kind = FieldKind.Int32 },
                    new FieldDesc { Name = "Z", Kind = FieldKind.Long },
                }
            };
            
            var testStruct2 = new TypeDesc()
            {
                Name = "TestStruct1",
                Kind = TypeKind.Struct,
                Fields =
                {
                    new FieldDesc { Name = "X", Kind = FieldKind.Int32 },
                    new FieldDesc { Name = "Y", Kind = FieldKind.Long },
                    new FieldDesc { Name = "Z", Kind = FieldKind.Int32 },
                }
            };
            
            
            using var rt = new ServerGcRuntime();
            
            rt.RegisterType(testStruct1);
            rt.RegisterType(testStruct2);
            
            Assert.Equal(16, testStruct1.Size);
            Assert.Equal(24, testStruct2.Size);

        }
    }
}