namespace GenerationalGC.Runtime.Core;

/// <summary>Object header layout used by this model.</summary>
public static class Layout
{
    private const int SyncBlockSize = 8;
    private const int MethodTableSize = 8;
    public const int HeaderSize = SyncBlockSize + MethodTableSize;
}