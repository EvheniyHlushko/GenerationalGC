using GenerationalGC.Runtime.Core;

namespace GenerationalGC.Runtime.Reporting;

public sealed class StructSummary
{
    public StructSummary(string name, List<FieldValue> fields)
    {
        Name = name;
        Fields = fields;
    }

    public string Name { get; }
    public List<FieldValue> Fields { get; }

    public override string ToString()
    {
        var parts = Fields.Select(f => f.Kind switch
            {
                FieldKind.Int32 => $"{f.Name}:{f.IntValue}",
                FieldKind.Long => $"{f.Name}:{f.LongValue}",
                FieldKind.Decimal => $"{f.Name}:{f.DecimalValue}",
                FieldKind.Ref => $"{f.Name}:{(f.RefAddress == 0 ? "null" : $"0x{f.RefAddress:X}")}",
                FieldKind.Struct => $"{f.Name}:<struct>",
                _ => $"{f.Name}:?"
            })
            .ToList();
        return "{ " + string.Join(", ", parts) + " }";
    }
}