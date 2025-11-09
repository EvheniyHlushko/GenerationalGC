using System.Runtime.InteropServices;

namespace GenerationalGC.Runtime.Core;

/// <summary>Unmanaged memory helpers for header/payload I/O on segments.</summary>
public static class Mem
{

    public static void Free(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
    }

    public static void Zero(IntPtr basePtr, int start, int length)
    {
        for (var i = 0; i < length; i++)
            Marshal.WriteByte(basePtr, start + i, 0);
    }


    public static byte ReadByte(IntPtr basePtr, int absOff)
    {
        return Marshal.ReadByte(basePtr, absOff);
    }

    public static void WriteByte(IntPtr basePtr, int absOff, byte v)
    {
        Marshal.WriteByte(basePtr, absOff, v);
    }

    public static int ReadI32(IntPtr basePtr, int absOff)
    {
        return Marshal.ReadInt32(basePtr, absOff);
    }
    
    

    public static void WriteI32(IntPtr basePtr, int absOff, int v)
    {
        Marshal.WriteInt32(basePtr, absOff, v);
    }

    public static long ReadI64(IntPtr basePtr, int absOff)
    {
        return Marshal.ReadInt64(basePtr, absOff);
    }

    public static void WriteI64(IntPtr basePtr, int absOff, long v)
    {
        Marshal.WriteInt64(basePtr, absOff, v);
    }


    public static void WriteHeader(IntPtr basePtr, int objOff, int typeId)
    {
        WriteI64(basePtr, objOff + 0, 0L);
        WriteI64(basePtr, objOff + 8, typeId);
    }

    public static int ReadTypeId(IntPtr basePtr, int objOff)
    {
        return checked((int)ReadI64(basePtr, objOff + 8));
    }


    public static long ReadRef64(IntPtr basePtr, int objOff, int payloadOffset)
    {
        return ReadI64(basePtr, objOff + Layout.HeaderSize + payloadOffset);
    }

    public static void WriteRef64(IntPtr basePtr, int objOff, int payloadOffset, long value)
    {
        WriteI64(basePtr, objOff + Layout.HeaderSize + payloadOffset, value);
    }
    
    public static decimal ReadDecimal(IntPtr basePtr, int absOff)
    {
        byte[] buffer = new byte[16];
        Marshal.Copy(basePtr + absOff, buffer, 0, 16);

        unsafe
        {
            fixed (byte* b = buffer)
            {
                return *(decimal*)b;
            }
        }
    }

    public static void WriteDecimal(IntPtr basePtr, int absOff, decimal value)
    {
        unsafe
        {
            byte[] buffer = new byte[16];
            fixed (byte* b = buffer)
            {
                *(decimal*)b = value;
            }
            Marshal.Copy(buffer, 0, basePtr + absOff, 16);
        }
    }

}