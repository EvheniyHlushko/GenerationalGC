using GenerationalGC.Runtime.Core;
using GenerationalGC.Runtime.Engine;

namespace BasicDemo;

public static class SimpleDemo
{
    public static void Run()
    {
        Heap.BrickSizeBytes = 256;
        Heap.CardSizeBytes = 128;
        using var rt = new ServerGcRuntime();
        rt.RegisterType(DemoTypes.TestObject);
        rt.RegisterType(DemoTypes.TestObjects);
        rt.RegisterType(DemoTypes.TestChildObject);


        var arr = rt.Alloc(DemoTypes.TestObjects, Generation.Gen1);

        rt.SetInt32(arr, "Length", 6);


        ObjRef parent = default;
        for (var i = 0; i < 6; i++)
        {
            var obj = rt.Alloc(DemoTypes.TestObject, Generation.Gen1);
            rt.SetRef(arr, $"R{i + 1}", obj);

            if (i == 4) parent = obj;
        }

        for (var i = 0; i < 6; i++)
        {
            var obj = rt.Alloc(DemoTypes.TestChildObject);
            if (i == 2) rt.SetRef(parent, "Child", obj);
        }
        


        // rt.MarkEphemeralAll(true);

        foreach (var heap in rt.AllHeaps)
        {
            Console.WriteLine($"--- Heap #{heap.Name} ---");
            Console.WriteLine(heap.GetReport());
            Console.WriteLine();
        }
        
        rt.CollectEphemeralAll_Parallel();
        
        foreach (var heap in rt.AllHeaps)
        {
            Console.WriteLine($"--- Heap #{heap.Name} ---");
            Console.WriteLine(heap.GetReport());
            Console.WriteLine();
        }
    }
}