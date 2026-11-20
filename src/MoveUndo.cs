using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AIFile;

// Undo only reverses a same-volume rename of the same filesystem object.
internal sealed record MoveUndo(string Original, string Current, MoveUndo.FileStamp Stamp, MoveUndo.FileId Parent)
{
    const uint ShareAll = 7, ShareReadWrite = 3, OpenExisting = 3, OpenDirectoryOrLink = 0x02200000, DeleteAccess = 0x10000;
    const int FileIdInfo = 18, FileRenameInfo = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct FileId(ulong Volume, ulong Low, ulong High);
    internal readonly record struct FileStamp(FileId Id, ulong Created, ulong Modified, ulong Length);
    internal sealed record Candidate(string Original, string Expected, FileStamp Stamp, FileId Parent);

    internal static Candidate? Capture(string source, string destination)
    {
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
            || File.Exists(destination) || Directory.Exists(destination)
            || Path.GetDirectoryName(source) is not { } parent) return null;
        using var handle = Open(source);
        using var originalParent = Open(parent);
        if (Read(handle) is not { } stamp || Read(originalParent) is not { } folder) return null;
        return new(source, destination, stamp, folder.Id);
    }

    internal static MoveUndo? Confirm(Candidate candidate, string actual)
    {
        if (!string.Equals(candidate.Expected, actual, StringComparison.OrdinalIgnoreCase)) return null;
        using var moved = Open(actual);
        return Read(moved) == candidate.Stamp ? new(candidate.Original, actual, candidate.Stamp, candidate.Parent) : null;
    }

    internal static FileId? Identity(string path)
    {
        using var handle = Open(path);
        return Read(handle)?.Id;
    }

    internal bool SourceIsUnchanged()
    {
        using var handle = Open(Current);
        return Read(handle) == Stamp;
    }

    internal void Restore()
    {
        using var moved = Open(Current, DeleteAccess);
        using var parent = Open(Path.GetDirectoryName(Original)!, sharing: ShareReadWrite);
        if (Read(moved) != Stamp || Read(parent)?.Id != Parent)
            throw new IOException("项目或原目录已变化，无法撤销。");

        // Keep the verified parent open without delete sharing while renaming the
        // verified source handle. The kernel rejects any destination collision.
        var name = Encoding.Unicode.GetBytes(Original);
        const int lengthOffset = 16, nameOffset = 20;
        int size = nameOffset + name.Length + sizeof(char);
        nint buffer = Marshal.AllocHGlobal(size);
        try {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            if (!SetFileInformationByHandle(moved, FileRenameInfo, buffer, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            GC.KeepAlive(parent);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static SafeFileHandle Open(string path, uint access = 0, uint sharing = ShareAll) =>
        CreateFile(path, access, sharing, 0, OpenExisting, OpenDirectoryOrLink, 0);

    static FileStamp? Read(SafeFileHandle handle)
    {
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileIdInfo, out var id, (uint)Marshal.SizeOf<FileId>())
            || !GetFileInformationByHandle(handle, out var metadata)
            || (metadata.Attributes & (uint)FileAttributes.ReparsePoint) != 0) return null;
        return new(id, Combine(metadata.CreateHigh, metadata.CreateLow), Combine(metadata.WriteHigh, metadata.WriteLow),
            Combine(metadata.SizeHigh, metadata.SizeLow));
    }

    static ulong Combine(uint high, uint low) => ((ulong)high << 32) | low;

    [StructLayout(LayoutKind.Sequential)]
    struct HandleInformation
    {
        public uint Attributes, CreateLow, CreateHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out FileId item, uint size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, nint data, uint size);
}
