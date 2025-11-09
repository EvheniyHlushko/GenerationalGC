namespace GenerationalGC.Runtime.Core;

public enum TypeKind
{
    Class,
    Struct
}

public enum FieldKind
{
    Int32,
    Long,
    Decimal,
    Ref,
    Struct
}

public sealed class FieldDesc
{
    public int Alignment;
    public FieldKind Kind;
    public string Name = string.Empty;
    public int Offset;
    public int Size;
    public TypeDesc? StructType;
}

/// <summary>Fixed-size C-like layout (no arrays).</summary>
public sealed class TypeDesc
{
    public readonly List<FieldDesc> Fields = [];
    private int _alignment;
    public TypeKind Kind;
    public string Name = "";
    public int Size;
    public int TypeId = 0;

 public void ComputeLayout()
{
    int cursor = 0;
    int maxAlign = 1;
    int pointer = IntPtr.Size; // 8 on x64, 4 on x86

    static int AlignUp(int v, int a) => (v + (a - 1)) & ~(a - 1);

    foreach (var f in Fields)
    {
        // Determine natural size & alignment per CLR rules.
        // NOTE:
        // - decimal: size 16, ALIGNMENT 4 (special-case)
        // - 8-byte primitives align to 8 on x64, 4 on x86
        switch (f.Kind)
        {
            case FieldKind.Int32:
                f.Size = 4;
                f.Alignment = 4;
                break;

            case FieldKind.Long:
                f.Size = 8;
                f.Alignment = (pointer == 8) ? 8 : 4;
                break;

            case FieldKind.Decimal:
                f.Size = 16;
                f.Alignment = 4; // important: NOT 8/16 on CLR
                break;

            case FieldKind.Ref:
                f.Size = pointer;
                f.Alignment = pointer;
                break;

            case FieldKind.Struct:
                if (f.StructType == null)
                    throw new InvalidOperationException("Struct field missing type.");
                f.StructType.ComputeLayout();          // recurse with same rules
                f.Size = f.StructType.Size;
                f.Alignment = f.StructType._alignment; // use nested effective alignment
                break;

            default:
                throw new NotSupportedException($"Unsupported field kind: {f.Kind}");
        }

        // Place field at its required alignment
        cursor = AlignUp(cursor, f.Alignment);
        f.Offset = cursor;
        cursor += f.Size;

        if (f.Alignment > maxAlign) maxAlign = f.Alignment;
    }

    if (Kind == TypeKind.Struct)
    {
        // Empty struct must have size 1 so array elements are distinct
        if (cursor == 0) cursor = 1;

        // Round struct size up to max alignment so arrays of this struct keep fields aligned
        Size = AlignUp(cursor, maxAlign);
        _alignment = maxAlign;
    }
    else
    {
        // Classes: total size rounding not required for array stride
        Size = cursor;
        _alignment = maxAlign;
    }
}
}