using System.Runtime.InteropServices;
using System.Text;

namespace AIFile;

internal enum FileAction { Copy, Move, Rename, Recycle, NewFolder }

internal sealed class RecycleUnavailableException() : IOException("此位置无法使用系统回收站。文件没有删除。");

internal enum FileOutcome { Pending, Completed, Skipped, Failed }
internal sealed record FileOperationItem(string Path, FileOutcome Outcome, int Code = 0, string? Destination = null);

internal sealed record FileOperationResult(bool Incomplete, IReadOnlyList<int> Errors, IReadOnlyList<FileOperationItem> Items)
{
    internal int Completed => Items.Count(item => item.Outcome == FileOutcome.Completed);
    internal string Message => Errors is [var first, ..]
        ? $"完成 {Completed}/{Items.Count} 项。{Marshal.GetExceptionForHR(first)?.Message}"
        : Incomplete ? $"完成 {Completed}/{Items.Count} 项。其余项目已跳过、取消或未得到确认。"
        : Items.Count == 0 ? "文件已在目标目录。" : $"已完成 {Completed} 项。";
}

internal static class ShellFileOperations
{
    const uint RecycleOnDelete = 0x00080000, WantNukeWarning = 0x00004000;
    const uint NoConfirmMakeDirectory = 0x00000200;
    const int Cancelled = unchecked((int)0x800704C7);
    static readonly Guid OperationClass = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    static readonly Guid ItemInterface = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    internal static Task<FileOperationResult> Run(nint owner, FileAction action, string[] paths,
        string? destination = null, string? name = null)
    {
        var completion = new TaskCompletionSource<FileOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try { completion.SetResult(Perform(owner, action, paths, destination, name)); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = false, Name = "AIFile Windows Shell operation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    static FileOperationResult Perform(nint owner, FileAction action, string[] paths, string? destination, string? name)
    {
        // Keep file commands bound to the existing source items and their actual destination.
        if (action == FileAction.Move)
            paths = paths.Where(path => !string.Equals(Path.GetDirectoryName(path), destination, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (paths.Length == 0 && action != FileAction.NewFolder) return new(false, [], []);
        if (action == FileAction.Recycle) {
            foreach (string volume in paths.Select(VolumeOf).Distinct(StringComparer.OrdinalIgnoreCase)) {
                var info = new RecycleBinInfo { Size = (uint)Marshal.SizeOf<RecycleBinInfo>() };
                if (GetDriveType(volume) != 3 || SHQueryRecycleBin(volume, ref info) < 0)
                    throw new RecycleUnavailableException();
            }
        }

        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(OperationClass)!)!;
        var heldItems = new List<IShellItem>();
        string? requestedNewPath = action == FileAction.NewFolder ? Path.Combine(destination!, name!) : null;
        var progress = new Progress(action, requestedNewPath == null ? paths : [requestedNewPath], requestedNewPath);
        uint cookie = 0;
        try {
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(NoConfirmMakeDirectory |
                (action == FileAction.Recycle ? RecycleOnDelete | WantNukeWarning : 0));
            operation.Advise(progress, out cookie);
            var target = destination != null ? Item(destination) : null;
            if (action == FileAction.NewFolder) operation.NewItem(target!, 0x10, name!, null, 0);
            bool queued = action == FileAction.NewFolder;
            foreach (string path in paths) {
                try {
                    var source = Item(path);
                    switch (action) {
                        case FileAction.Copy: operation.CopyItem(source, target!, null, 0); break;
                        case FileAction.Move: operation.MoveItem(source, target!, null, 0); break;
                        case FileAction.Rename: operation.RenameItem(source, name!, 0); break;
                        case FileAction.Recycle: operation.DeleteItem(source, 0); break;
                    }
                    queued = true;
                }
                catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException) {
                    progress.Mark(path, error.HResult, null);
                }
            }

            bool aborted = false;
            if (queued) {
                progress.Record(operation.PerformOperations());
                operation.GetAnyOperationsAborted(out aborted);
            }
            var items = progress.Items.Values.ToArray();
            return new(aborted || progress.Incomplete || items.Any(item => item.Outcome != FileOutcome.Completed), progress.Errors, items);
        }
        finally {
            try { if (cookie != 0) operation.Unadvise(cookie); }
            finally {
                foreach (var item in heldItems) Marshal.ReleaseComObject(item);
                Marshal.ReleaseComObject(operation);
            }
        }

        IShellItem Item(string path)
        {
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, in ItemInterface, out IShellItem item));
            heldItems.Add(item);
            return item;
        }
    }

    internal static string VolumeOf(string path)
    {
        var buffer = new StringBuilder(32768);
        if (GetVolumePathName(path, buffer, (uint)buffer.Capacity)) return buffer.ToString();
        return Path.GetPathRoot(path) ?? "";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RecycleBinInfo { public uint Size; public long Bytes, Items; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    static extern int SHQueryRecycleBin(string root, ref RecycleBinInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetDriveType(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVolumePathName(string fileName, StringBuilder volumePath, uint length);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(nint bindContext, in Guid handler, in Guid iid, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out nint name);
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation
    {
        void Advise(IProgress sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint properties);
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem source, [MarshalAs(UnmanagedType.LPWStr)] string name, nint sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object sources, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem source, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, nint sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object sources, IShellItem destination);
        void CopyItem(IShellItem source, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, nint sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object sources, IShellItem destination);
        void DeleteItem(IShellItem source, nint sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object sources);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, nint sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComImport, Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IProgress
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, nint item, nint name);
        [PreserveSig] int PostRenameItem(uint flags, nint item, nint name, int result, nint created);
        [PreserveSig] int PreMoveItem(uint flags, nint item, nint destination, nint name);
        [PreserveSig] int PostMoveItem(uint flags, nint item, nint destination, nint name, int result, nint created);
        [PreserveSig] int PreCopyItem(uint flags, nint item, nint destination, nint name);
        [PreserveSig] int PostCopyItem(uint flags, nint item, nint destination, nint name, int result, nint created);
        [PreserveSig] int PreDeleteItem(uint flags, nint item);
        [PreserveSig] int PostDeleteItem(uint flags, nint item, int result, nint created);
        [PreserveSig] int PreNewItem(uint flags, nint destination, nint name);
        [PreserveSig] int PostNewItem(uint flags, nint destination, nint name, nint templateName, uint attributes, int result, nint created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    sealed class Progress(FileAction action, string[] paths, string? requestedNewPath) : IProgress
    {
        const uint RecycleIfPossible = 0x80;
        const int UserIgnored = 0x00270005, CopyEngineCancelled = unchecked((int)0x80270000);
        internal List<int> Errors { get; } = [];
        internal Dictionary<string, FileOperationItem> Items { get; } = paths.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, path => new FileOperationItem(path, FileOutcome.Pending), StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<nint, string> started = [];
        internal bool Incomplete { get; private set; }
        internal int Record(int result)
        {
            if (result is Cancelled or CopyEngineCancelled or UserIgnored) Incomplete = true;
            else if (result < 0 && !Errors.Contains(result)) Errors.Add(result);
            return 0;
        }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => Record(result);
        internal int Mark(string? path, int result, string? destination)
        {
            if (path != null && Items.TryGetValue(path, out var entry)) {
                var outcome = result is Cancelled or CopyEngineCancelled or UserIgnored ? FileOutcome.Skipped
                    : result < 0 ? FileOutcome.Failed : FileOutcome.Completed;
                Items[path] = entry with { Outcome = outcome, Code = result, Destination = destination };
            }
            return Record(result);
        }
        int Before(nint item)
        {
            if (DisplayPath(item) is { } path && Items.ContainsKey(path)) started[item] = path;
            return 0;
        }
        int After(nint item, int result, nint created) =>
            Mark(started.Remove(item, out string? path) ? path : DisplayPath(item), result, DisplayPath(created));

        static string? DisplayPath(nint item)
        {
            if (item == 0) return null;
            nint name = 0;
            try {
                ((IShellItem)Marshal.GetObjectForIUnknown(item)).GetDisplayName(0x80058000, out name); // SIGDN_FILESYSPATH
                return Marshal.PtrToStringUni(name);
            }
            catch (Exception error) when (error is COMException or InvalidCastException) { return null; }
            finally { if (name != 0) Marshal.FreeCoTaskMem(name); }
        }
        public int PreRenameItem(uint flags, nint item, nint name) => Before(item);
        public int PostRenameItem(uint flags, nint item, nint name, int result, nint created) => After(item, result, created);
        public int PreMoveItem(uint flags, nint item, nint destination, nint name) => Before(item);
        public int PostMoveItem(uint flags, nint item, nint destination, nint name, int result, nint created) => After(item, result, created);
        public int PreCopyItem(uint flags, nint item, nint destination, nint name) => Before(item);
        public int PostCopyItem(uint flags, nint item, nint destination, nint name, int result, nint created) => After(item, result, created);
        public int PreDeleteItem(uint flags, nint item)
        {
            if (action != FileAction.Recycle || (flags & RecycleIfPossible) != 0) return Before(item);
            Mark(DisplayPath(item), unchecked((int)0x80070032), null);
            return Cancelled;
        }
        public int PostDeleteItem(uint flags, nint item, int result, nint created) => After(item, result, created);
        public int PreNewItem(uint flags, nint destination, nint name) => 0;
        public int PostNewItem(uint flags, nint destination, nint name, nint templateName, uint attributes, int result, nint created) =>
            Mark(requestedNewPath, result, DisplayPath(created));
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
