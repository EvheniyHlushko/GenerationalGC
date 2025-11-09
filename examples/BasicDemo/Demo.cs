// examples/BasicDemo/Program.cs
using System;
using System.Threading.Tasks;
using GenerationalGC.Runtime.Core;    // ExampleTypes, Generation, ObjRef
using GenerationalGC.Runtime.Engine;  // ServerGcRuntime

namespace BasicDemo;

internal static class Demo
{
    public static async Task Run()
    {
        using var rt = new ServerGcRuntime(); // one Heap per logical CPU

        // Register types into all heaps (broadcast to every per-core heap)
        rt.RegisterType(ExampleTypes.Location);
        rt.RegisterType(ExampleTypes.Node);
        rt.RegisterType(ExampleTypes.Holder);

        // === Heap A (main thread): allocate one old (Gen1) node + one young (Gen0) node
        
        /*
         21 - alive
         11 - alive - has reference from Heap B (holder)
         22 - alive
         12 - alive
         13 - dead
         holder - alive
         211 - alive has reference from 11 young A
         212 - dead
         213 - alive
         
         holder - alive
         
        Enqueued obj 1706F0000
           Skipping segment brick index 0. Last obj start offset 32 Card start offset 0
           Scanning obj x1706F0000
           Enqueued obj 174018000
           Scanning obj x1706F0020
           Enqueued obj 1705F8020
           Start scanning from the brick index 0 with last obj offset 0 Card start offset 0
           Scanning obj x171860000
           Enqueued obj 1705F8000
           Enqueued obj 174018040
           
           
           
         */
        
        var oldOnA   = rt.Alloc(ExampleTypes.Node, forced: Generation.Gen1);
        rt.SetInt32(oldOnA, "Id", 21);
        rt.SetRoot("rootA_Node", oldOnA);

        var youngOnA = rt.Alloc(ExampleTypes.Node); // Gen0 on Heap A
        rt.SetInt32(youngOnA, "Id", 11);
        
        var oldB = rt.Alloc(ExampleTypes.Node, Generation.Gen1); // Gen0 on Heap A
        rt.SetInt32(oldB, "Id", 22);
        
        var youngOnB = rt.Alloc(ExampleTypes.Node); // Gen0 on Heap A
        rt.SetInt32(youngOnB, "Id", 12);
        
        var youngOnC = rt.Alloc(ExampleTypes.Node); // Gen0 on Heap A
        rt.SetInt32(youngOnC, "Id", 13);
        
        rt.SetRef(oldB, "Next", youngOnB);

        Console.WriteLine($"Main thread heap {rt.CurrentHeap.Name}");

        // === Run two background tasks in parallel and wait for both with WhenAll ===
        ObjRef holderOnB = default;

        var tMakeCrossHeapOldWriter = Task.Run(() =>
        {
        
            Console.WriteLine($"Thread 1 heap {rt.CurrentHeap.Name}");
            // Heap B (some other thread): old Holder points to Gen0 on Heap A  => OLD->Gen0 (dirty card)
            var h = rt.Alloc(ExampleTypes.Holder, forced: Generation.Gen1);
            rt.SetRef(h, "Child", youngOnA);                // OLD -> Gen0 (cross-heap)
            rt.SetStructRef(h, "Loc", "RefToNode", oldOnA); // OLD -> OLD (cross-heap)
           // rt.SetRoot("rootB_Holder", h);
            holderOnB = h;
        });

        var tChurn = Task.Run(() =>
        {
            Console.WriteLine($"Thread 2 heap {rt.CurrentHeap.Name}");
            
            var young2OnC = rt.Alloc(ExampleTypes.Node); 
            rt.SetInt32(young2OnC, "Id", 211);
            var young2OnD = rt.Alloc(ExampleTypes.Node); 
            rt.SetInt32(young2OnD, "Id", 212);
            
            var young2OnE = rt.Alloc(ExampleTypes.Node); 
            rt.SetInt32(young2OnE, "Id", 213);
            
            
            
            rt.SetRef(youngOnC, "Next", young2OnD);
            rt.SetRef(youngOnA, "Next", young2OnE);
            
            
            
            
            rt.SetRef(oldOnA, "Next", young2OnC);
            
        });

        await Task.WhenAll(tMakeCrossHeapOldWriter, tChurn);

        // --------------------------------------------------------------------
        // STEP 1: MARK PHASE ONLY (pause here so you can see DirtyCards)
        // --------------------------------------------------------------------
        // markAllOldCards:true additionally forces all cards in Gen1/Gen2/LOH to dirty,
        // useful if your writes happened before the demo starts or you want to visualize cards clearly.
       // rt.MarkEphemeralAll(markAllOldCards: true);

        Console.WriteLine("=== AFTER MARK-ONLY (DirtyCards should be > 0) ===");
        var idx = 0;
        foreach (var heap in rt.AllHeaps)
        {
            Console.WriteLine($"--- Heap #{heap.Name} ---");
            var report = heap.GetReport();
            if (report.Segments.All(s => s.Objects.Count == 0))
            {
                Console.WriteLine("Empty");
                Console.WriteLine();
                continue;
            }
            Console.WriteLine(report);
            Console.WriteLine();
        }

        // --------------------------------------------------------------------
        // STEP 2: COMPLETE THE MINOR GC (COMPACT + PROMOTE)
        // --------------------------------------------------------------------
        rt.CollectEphemeralAll_Parallel();

        Console.WriteLine("=== AFTER FULL MINOR GC (Gen0 compacted/promotion applied) ===");
        idx = 0;
        foreach (var heap in rt.AllHeaps)
        {
            Console.WriteLine($"--- Heap #{idx++} ---");
            Console.WriteLine(heap.GetReport());
            Console.WriteLine();
        }

   
    }
}
