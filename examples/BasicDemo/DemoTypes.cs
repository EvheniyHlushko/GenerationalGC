using GenerationalGC.Runtime.Core;

namespace BasicDemo;

public static class DemoTypes
{
    public static readonly TypeDesc TestObject = new()
    {
        Name = "TestObject",
        Kind = TypeKind.Class,
        Fields =
        {
            new FieldDesc { Name = "A", Kind = FieldKind.Decimal },
            new FieldDesc { Name = "B", Kind = FieldKind.Decimal },
            new FieldDesc { Name = "Child", Kind = FieldKind.Ref },
            new FieldDesc { Name = "L1", Kind = FieldKind.Long },
            new FieldDesc { Name = "Child2", Kind = FieldKind.Ref }
            
        }
    };
    
    public static readonly TypeDesc TestChildObject = new()
    {
        Name = "TestChildObject",
        Kind = TypeKind.Class,
        Fields =
        {
            new FieldDesc { Name = "L1", Kind = FieldKind.Long }
            
        }
    };
    
    public static readonly TypeDesc TestObjects = new()
    {
        Name = "TestObjects",
        Kind = TypeKind.Class,
        Fields =
        {
            new FieldDesc { Name = "Length", Kind = FieldKind.Int32 },
            new FieldDesc { Name = "R1", Kind = FieldKind.Ref },
            new FieldDesc { Name = "R2", Kind = FieldKind.Ref },
            new FieldDesc { Name = "R3", Kind = FieldKind.Ref },
            new FieldDesc { Name = "R4", Kind = FieldKind.Ref },
            new FieldDesc { Name = "R5", Kind = FieldKind.Ref },
            new FieldDesc { Name = "R6", Kind = FieldKind.Ref }
        }
    };
}