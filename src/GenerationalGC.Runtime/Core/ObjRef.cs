namespace GenerationalGC.Runtime.Core;

public readonly struct ObjRef(nint address)
{
    public readonly nint Address = address;
    public bool IsNull => Address == 0;

    public override string ToString()
    {
        return IsNull ? "null" : $"0x{(long)Address:X}";
    }
}