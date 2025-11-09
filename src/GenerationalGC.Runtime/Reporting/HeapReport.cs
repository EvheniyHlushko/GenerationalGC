namespace GenerationalGC.Runtime.Reporting;

public sealed class HeapReport
{
    public List<RootEntry> Roots { get; } = new();
    public List<SegmentReport> Segments { get; } = new();

    public override string ToString()
    {
        var lines = new List<string>
        {
            "=== Heap Report ===",
            $"Roots: {Roots.Count}"
        };

        if (Segments.All(x => x.Objects.Count == 0))
        {
            lines.Add("Heap empty");
            return string.Join(Environment.NewLine, lines);
        }
        lines.AddRange(Roots.OrderBy(r => r.Name)
            .Select(r => $"  {r.Name}: {(r.Address == 0 ? "null" : $"0x{(long)r.Address:X}")}"));

        foreach (var s in Segments)
        {
            lines.Add("");
            lines.Add(s.ToString());
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class RootEntry(string name, nint address)
{
    public string Name { get; } = name;
    public nint Address { get; } = address;
}