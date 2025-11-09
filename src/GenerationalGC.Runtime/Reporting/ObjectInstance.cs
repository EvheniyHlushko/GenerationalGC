using GenerationalGC.Runtime.Core;

namespace GenerationalGC.Runtime.Reporting;

public sealed class ObjectInstance
{
    public ObjectInstance(int index, string typeName, ulong address, int sizeBytes)
    {
        Index = index;
        TypeName = typeName;
        Address = address;
        SizeBytes = sizeBytes;
    }

    public int Index { get; }
    public string TypeName { get; }
    public ulong Address { get; }
    public int SizeBytes { get; }
    public List<FieldValue> Fields { get; } = new();
    public List<StructSummary> StructSummaries { get; } = new();

    public override string ToString()
    {
        var parts = new List<string> { $"#{Index} {TypeName}@0x{Address:X} size={SizeBytes}" };
        parts.AddRange(Fields.Select(f => f.Kind switch
        {
            FieldKind.Int32 => $"[{f.Name}={f.IntValue}]",
            FieldKind.Long => $"[{f.Name}={f.LongValue}]",
            FieldKind.Decimal => $"[{f.Name}={f.DecimalValue}]",
            FieldKind.Ref => $"[{f.Name}={(f.RefAddress == 0 ? "null" : $"0x{f.RefAddress:X}")}]",
            _ => $"[{f.Name}=<struct>]"
        }));

        parts.AddRange(StructSummaries.Select(s => $"[{s.Name}={s}]"));

        return string.Join(" ", parts);
    }
}