namespace GenerationalGC.Runtime.Core;

/// <summary>Per-thread bump cursor inside a reserved Gen0 slab.</summary>
public sealed class ThreadLocalHeap(string name)
{
    public string Name { get; } = name;
    public Segment? Segment { get; internal set; }
    public int SlabStart { get; internal set; }
    public int SlabCursor { get; internal set; }
    public int SlabLimit { get; internal set; }

    public void Invalidate()
    {
        Segment = null;
        SlabStart = SlabCursor = SlabLimit = 0;
    }
}