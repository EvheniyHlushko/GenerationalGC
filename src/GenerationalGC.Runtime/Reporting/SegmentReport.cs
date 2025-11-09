using GenerationalGC.Runtime.Core;

namespace GenerationalGC.Runtime.Reporting;

public sealed class SegmentReport
{
    public SegmentReport(Generation gen, ulong @base, int sizeBytes, int allocatedBytes, int dirtyCardCount)
    {
        Gen = gen;
        Base = @base;
        SizeBytes = sizeBytes;
        AllocatedBytes = allocatedBytes;
        DirtyCardCount = dirtyCardCount;
    }

    public Generation Gen { get; }
    public ulong Base { get; }
    public int SizeBytes { get; }
    public int AllocatedBytes { get; }
    public int DirtyCardCount { get; }
    public List<ObjectInstance> Objects { get; } = new();

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"Segment {Gen}  Base=0x{Base:X}  Alloc={AllocatedBytes}/{SizeBytes}  DirtyCards={DirtyCardCount}",
            $"  Objects: {Objects.Count}"
        };
        lines.AddRange(Objects.Select(o => "  " + o));

        return string.Join(Environment.NewLine, lines);
    }
}