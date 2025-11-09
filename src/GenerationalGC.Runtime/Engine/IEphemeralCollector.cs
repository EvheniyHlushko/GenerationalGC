using GenerationalGC.Runtime.Core;

namespace GenerationalGC.Runtime.Engine;

/// <summary>Minor GC for Gen0/Gen1: mark (roots + dirty cards), compact Gen0, promote, fix references.</summary>
public interface IEphemeralCollector
{
    void CollectLocal(IDictionary<string, ObjRef> roots, IList<Region> regions);

    void CollectWithCrossHeap(
        IDictionary<string, ObjRef> roots,
        IList<Region> regions,
        IEnumerable<Segment> oldSegmentsFromAllHeaps,
        out Dictionary<long, long> relocFromCompaction,
        out Dictionary<long, long> relocFromPromotion);
}