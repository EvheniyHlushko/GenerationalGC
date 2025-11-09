using System.Runtime.InteropServices;

namespace GenerationalGC.Runtime.Core;

public interface INumaAllocator
{
    IntPtr Alloc(int bytes, int numaNodeId);
    void   Free (IntPtr ptr, int bytes);
}

// Portable fallback: ignores nodeId
public sealed class StubNumaAllocator : INumaAllocator
{
    public IntPtr Alloc(int bytes, int _) => Marshal.AllocHGlobal(bytes);
    public void   Free (IntPtr p, int  _) => Marshal.FreeHGlobal(p);
}

// Optional Windows NUMA allocator (use when running on Windows)
public sealed class WindowsNumaAllocator : INumaAllocator
{
    private const uint MEM_COMMIT_RESERVE = 0x3000;  // MEM_COMMIT | MEM_RESERVE
    private const uint PAGE_READWRITE     = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocExNuma(
        IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize,
        uint flAllocationType, uint flProtect, uint nndPreferred);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    public IntPtr Alloc(int bytes, int nodeId)
    {
        var p = VirtualAllocExNuma(GetCurrentProcess(), IntPtr.Zero,
            (UIntPtr)bytes, MEM_COMMIT_RESERVE, PAGE_READWRITE, (uint)nodeId);
        if (p == IntPtr.Zero)
            throw new OutOfMemoryException($"VirtualAllocExNuma failed (node {nodeId}).");
        return p;
    }

    public void Free(IntPtr ptr, int bytes)
    {
        // MEM_RELEASE = 0x8000
        _ = VirtualFree(ptr, UIntPtr.Zero, 0x8000);
    }
}