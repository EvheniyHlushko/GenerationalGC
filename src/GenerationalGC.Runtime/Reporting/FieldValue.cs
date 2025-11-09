using GenerationalGC.Runtime.Core;

namespace GenerationalGC.Runtime.Reporting;

public sealed class FieldValue
{
    public FieldValue(string name, FieldKind kind, object? nestedOrInt, ulong refAddr)
    {
        Name = name;
        Kind = kind;
        switch (kind)
        {
            case FieldKind.Int32:
                IntValue = (int)nestedOrInt!;
                break;
            case FieldKind.Long:
                LongValue = (long)nestedOrInt!;
                break;
            case FieldKind.Decimal:
                DecimalValue = (decimal)nestedOrInt!;
                break;
            case FieldKind.Ref:
            case FieldKind.Struct:
            default:
                Nested = nestedOrInt;
                break;
        }
        RefAddress = refAddr;
    }

    public string Name { get; }
    public FieldKind Kind { get; }
    public int IntValue { get; }
    public long LongValue { get; }
    public decimal DecimalValue { get; }
    public ulong RefAddress { get; }
    public object? Nested { get; }
}