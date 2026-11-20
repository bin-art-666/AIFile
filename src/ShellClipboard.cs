using System.Runtime.InteropServices;
using System.Text;

namespace AIFile;

internal sealed record ClipboardFiles(string[] Paths, bool Move, uint Sequence);

internal static class ShellClipboard
{
    const uint FileDrop = 15, Copy = 1, Move = 2;
    const int DropHeaderSize = 20;
    static readonly uint PreferredEffect = RegisterClipboardFormat("Preferred DropEffect");

    internal static void Put(nint owner, string[] paths, bool move)
    {
        byte[] names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var drop = new byte[DropHeaderSize + names.Length];
        BitConverter.GetBytes(DropHeaderSize).CopyTo(drop, 0);
        drop[16] = 1; // DROPFILES.fWide
        names.CopyTo(drop, DropHeaderSize);
        nint fileHandle = 0, effectHandle = 0;
        try {
            fileHandle = Allocate(drop);
            effectHandle = Allocate(BitConverter.GetBytes(move ? Move : Copy));
            WithClipboard(owner, () => {
                if (!EmptyClipboard()) throw new IOException("无法清空剪贴板。");
                Publish(PreferredEffect, ref effectHandle);
                Publish(FileDrop, ref fileHandle);
            });
        }
        finally {
            if (effectHandle != 0) GlobalFree(effectHandle);
            if (fileHandle != 0) GlobalFree(fileHandle);
        }
    }

    internal static ClipboardFiles Read(nint owner)
    {
        string[] paths = [];
        bool move = false;
        uint sequence = 0;
        WithClipboard(owner, () => {
            nint files = GetClipboardData(FileDrop);
            if (files == 0) return;
            uint count = DragQueryFile(files, uint.MaxValue, null, 0);
            var found = new List<string>();
            for (uint index = 0; index < count; index++) {
                var buffer = new StringBuilder((int)DragQueryFile(files, index, null, 0) + 1);
                if (DragQueryFile(files, index, buffer, (uint)buffer.Capacity) > 0) found.Add(buffer.ToString());
            }
            paths = found.ToArray();
            nint effect = GetClipboardData(PreferredEffect);
            if (effect != 0 && GlobalSize(effect) >= sizeof(int)) {
                nint pointer = GlobalLock(effect);
                if (pointer != 0) {
                    try { move = Marshal.ReadInt32(pointer) == Move; }
                    finally { GlobalUnlock(effect); }
                }
            }
            sequence = GetClipboardSequenceNumber();
        });
        return new(paths, move, sequence);
    }

    internal static void ClearIfCurrent(nint owner, uint sequence) => WithClipboard(owner, () => {
        if (GetClipboardSequenceNumber() == sequence) EmptyClipboard();
    });

    static void WithClipboard(nint owner, Action action)
    {
        if (!OpenClipboard(owner)) throw new IOException("剪贴板正在使用，请重试。");
        try { action(); }
        finally { CloseClipboard(); }
    }

    static nint Allocate(byte[] bytes)
    {
        nint handle = GlobalAlloc(2, (nuint)bytes.Length);
        if (handle == 0) throw new OutOfMemoryException();
        nint pointer = GlobalLock(handle);
        if (pointer == 0) {
            GlobalFree(handle);
            throw new IOException("无法准备剪贴板数据。");
        }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        finally { GlobalUnlock(handle); }
        return handle;
    }

    static void Publish(uint format, ref nint handle)
    {
        if (SetClipboardData(format, handle) == 0) throw new IOException("无法写入剪贴板。");
        handle = 0; // Windows owns the allocation after SetClipboardData.
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll")] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint memory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(nint drop, uint index, StringBuilder? path, uint length);
}
