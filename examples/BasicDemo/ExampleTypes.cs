using GenerationalGC.Runtime.Core;

namespace BasicDemo;

public static class ExampleTypes
{
    public static readonly TypeDesc Location = new()
    {
        Name = "Location",
        Kind = TypeKind.Struct,
        Fields =
        {
            new FieldDesc { Name = "X", Kind = FieldKind.Int32 },
            new FieldDesc { Name = "Y", Kind = FieldKind.Int32 },
            new FieldDesc { Name = "RefToNode", Kind = FieldKind.Ref }
        }
    };

    public static readonly TypeDesc Node = new()
    {
        Name = "Node",
        Kind = TypeKind.Class,
        Fields =
        {
            new FieldDesc { Name = "Id", Kind = FieldKind.Int32 },
            new FieldDesc { Name = "Next", Kind = FieldKind.Ref }
        }
    };

    public static readonly TypeDesc Holder = new()
    {
        Name = "Holder",
        Kind = TypeKind.Class,
        Fields =
        {
            new FieldDesc { Name = "Child", Kind = FieldKind.Ref },
            new FieldDesc { Name = "Loc", Kind = FieldKind.Struct, StructType = Location }
        }
    };
}