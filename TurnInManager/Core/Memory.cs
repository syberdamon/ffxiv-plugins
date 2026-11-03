using System.Runtime.InteropServices;

namespace TurnInManager.Core;

/// <summary>Check that a block of game memory is committed and readable before reading it.</summary>
public static class Memory
{
    public static bool IsReadable(nint address, int length)
    {
        if (address == 0 || length <= 0) return false;
        var cur = (nuint)address;
        var end = cur + (nuint)length;
        while (cur < end)
        {
            if (VirtualQuery((nint)cur, out var mbi, (nuint)Marshal.SizeOf<MemInfo>()) == 0) return false;
            const uint Commit = 0x1000, NoAccess = 0x01, Guard = 0x100, Readable = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
            if (mbi.State != Commit || (mbi.Protect & (NoAccess | Guard)) != 0 || (mbi.Protect & Readable) == 0) return false;
            var next = (nuint)mbi.BaseAddress + mbi.RegionSize;
            if (next <= cur) return false;
            cur = next;
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemInfo
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, out MemInfo info, nuint size);
}
