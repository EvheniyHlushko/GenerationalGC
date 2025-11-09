// benchmarks/RegionVsGenBench.cs

using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using GenerationalGC.Runtime.Core;
using GenerationalGC.Runtime.Engine;

namespace Benchmarks;

[MemoryDiagnoser]
public class RegionVsGenBench
{
    private static long _sink;

    private Heap _heap = null!;
    private int _nodeObjectSize;
    private TypeDesc _nodeType = null!;

    private Region? _region;

    private ThreadLocalHeap _tlh = null!;

    [Params(10_000, 50_000, 200_000)] public int Count;


    [GlobalSetup]
    public void GlobalSetup()
    {
        _heap = new Heap("test");

        _nodeType = new TypeDesc
        {
            Name = "Node",
            Kind = TypeKind.Class,
            Fields =
            {
                new FieldDesc { Name = "Id", Kind = FieldKind.Int32 },
                new FieldDesc { Name = "Next", Kind = FieldKind.Ref }
            }
        };

        _heap.RegisterType(_nodeType);

        _tlh = new ThreadLocalHeap("bench");

        _nodeObjectSize = AlignUp(Layout.HeaderSize + _nodeType.Size, IntPtr.Size);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _heap.Dispose();
    }

    [IterationSetup(Target = nameof(Alloc_Region))]
    public void IterSetup_Region()
    {
        var capacity = AlignUp(_nodeObjectSize * Count + 4096, IntPtr.Size);
        _region = _heap.CreateRegion(capacity);
    }

    [IterationCleanup(Target = nameof(Alloc_Region))]
    public void IterCleanup_Region()
    {
        _region?.Dispose();
        _region = null;
    }

    [IterationCleanup(Target = nameof(Alloc_Generations))]
    public void IterCleanup_Gen()
    {
        _heap.CollectEphemeral();
    }


    [Benchmark(Baseline = true, Description = "GC: Gen0 TLH (minor GCs as needed)")]
    public void Alloc_Generations()
    {
        var count = Count;

        ObjRef prev = default;
        long checksum = 0;

        for (var i = 0; i < count; i++)
        {
            var cur = _heap.Alloc(_nodeType, _tlh);
            _heap.SetInt32(cur, "Id", i);
            if (!prev.IsNull) _heap.SetRef(cur, "Next", prev);
            prev = cur;

            checksum += cur.Address;
        }

        _sink ^= checksum;
    }

    [Benchmark(Description = "GC: Region (arena) free-all")]
    public void Alloc_Region()
    {
        var count = Count;
        var region = _region!;

        ObjRef prev = default;
        long checksum = 0;

        for (var i = 0; i < count; i++)
        {
            var cur = _heap.AllocInRegion(_nodeType, region);
            _heap.SetInt32(cur, "Id", i);
            if (!prev.IsNull) _heap.SetRef(cur, "Next", prev);
            prev = cur;

            checksum += cur.Address;
        }

        _sink ^= checksum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AlignUp(int value, int alignment)
    {
        return (value + (alignment - 1)) & ~(alignment - 1);
    }
}