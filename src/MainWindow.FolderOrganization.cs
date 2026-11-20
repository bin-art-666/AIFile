using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AIFile;

public sealed partial class MainWindow
{
    sealed record FolderUndo(string Folder, MoveUndo.FileId Parent, MoveUndo.FileId? Identity,
        bool Recreate, DateTime CreationTime);
    FolderUndo? folderUndo;

    static bool IsValidName(string name) => name.Length is > 0 and <= 255 && name is not "." and not ".."
        && !name.EndsWith('.') && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    async Task GroupSelection(FileEntry[] selection)
    {
        if (directory is not { } parent || selection.Length < 2 || busy || !available) return;
        var input = new TextBox { Header = "新文件夹名称", Text = "新建文件夹", MaxLength = 255 };
        StyleStationDialogInput(input);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"将 {selection.Length} 项放入新文件夹",
            Content = input, PrimaryButtonText = "移动", CloseButtonText = "取消" };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = IsValidName(input.Text.Trim());
        PanelTheme.CompactDialog(dialog);
        if (await ShowStationDialog(dialog, () => {
            input.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            input.SelectAll();
        }) != ContentDialogResult.Primary || !IsValidName(input.Text.Trim())) return;
        await GroupIntoNewFolder(parent, input.Text.Trim(), selection);
    }

    async Task GroupIntoNewFolder(string parent, string name, FileEntry[] selection)
    {
        if (Directory.Exists(Path.Combine(parent, name)) || File.Exists(Path.Combine(parent, name))) {
            SetStatus("已存在同名项目，请为新文件夹选一个不同的名称。", warning: true);
            return;
        }
        var created = await Perform(FileAction.NewFolder, [], parent, name);
        if (created is not { Incomplete: false, Items: [{ Outcome: FileOutcome.Completed, Destination: string target }] }
            || !PathComparer.Equals(Path.GetDirectoryName(target), parent)
            || !Directory.Exists(target) || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) {
            SetStatus("未确认创建的新文件夹位置，文件未移动。", warning: true);
            return;
        }
        var identity = MoveUndo.Identity(target);
        var parentId = MoveUndo.Identity(parent);
        var moved = await Perform(FileAction.Move, selection.Select(item => item.Path).ToArray(), target);
        if (moved is { Incomplete: false } && lastMoves.Length == selection.Length && identity != null && parentId is { } knownParent)
            folderUndo = new(target, knownParent, identity, false, default);
    }

    async Task DissolveFolder(FileEntry folder)
    {
        if (busy || !available || Path.GetDirectoryName(folder.Path) is not { } parent) return;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "解散文件夹？",
            Content = $"将「{folder.Name}」里的项目移到上一级。全部移动成功后，只移除空文件夹。",
            PrimaryButtonText = "解散", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
        };
        PanelTheme.CompactDialog(dialog);
        if (await ShowStationDialog(dialog) == ContentDialogResult.Primary) await MoveOutAndRemoveEmptyFolder(folder.Path, parent);
    }

    async Task MoveOutAndRemoveEmptyFolder(string folder, string parent)
    {
        var attributes = File.GetAttributes(folder);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("此项目无法作为普通文件夹解散。");
        var identity = MoveUndo.Identity(folder);
        var parentId = MoveUndo.Identity(parent);
        var creation = Directory.GetCreationTimeUtc(folder);
        folderUndo = null;
        var children = await Task.Run(() => Directory.GetFileSystemEntries(folder));
        if (children.Length > 0) {
            var moved = await Perform(FileAction.Move, children, parent);
            if (moved is not { Incomplete: false, Errors.Count: 0 }) return;
        }
        // Non-recursive removal atomically rejects newly created or skipped children.
        if (identity == null || MoveUndo.Identity(folder) != identity)
            throw new IOException("文件夹已变更，已保留原目录。");
        Directory.Delete(folder, recursive: false);
        if (children.Length == 0) lastMoves = [];
        if (children.Length == lastMoves.Length && parentId is { } knownParent)
            folderUndo = new(folder, knownParent, null, true, creation);
        else lastMoves = [];
        SetStatus("文件夹已解散。");
        ScheduleRefresh();
    }

    MoveUndo[] PrepareFolderUndo(FolderUndo change, MoveUndo[] moves)
    {
        if (Path.GetDirectoryName(change.Folder) is not { } parent || MoveUndo.Identity(parent) != change.Parent)
            throw new IOException("原上级目录已变化，无法撤销整组操作。");
        if (change.Identity is { } expected) {
            if (MoveUndo.Identity(change.Folder) != expected)
                throw new IOException("文件夹已变化，无法撤销整组操作。");
            return moves;
        }
        if (moves.Any(move => !move.SourceIsUnchanged()))
            throw new IOException("部分项目已变化，原文件夹尚未重建。");
        if (!CreateDirectory(change.Folder, 0))
            throw new IOException("无法重建原文件夹：" + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        var rebuilt = MoveUndo.Identity(change.Folder)
            ?? throw new IOException("未能验证重建后的文件夹。");
        folderUndo = change with { Identity = rebuilt };
        try { Directory.SetCreationTimeUtc(change.Folder, change.CreationTime); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return moves.Select(move => move with { Parent = rebuilt }).ToArray();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateDirectory(string path, nint attributes);
}
