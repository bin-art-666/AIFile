using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;
using Windows.System;

namespace AIFile;

public sealed partial class MainWindow
{
    static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    string[]? ownDragPaths;
    DataPackageView? hoverPackage;
    Task<string[]>? hoverPaths;
    FileOperationResult? lastOperation;
    MoveUndo[] lastMoves = [];

    FileEntry[] SelectedFiles() => ActiveFiles.SelectedItems.OfType<FileEntry>().Where(file => file.EmptySlot < 0).ToArray();

    void InstallShortcuts()
    {
        Key(Root, VirtualKey.Left, VirtualKeyModifiers.Menu, () => GoHistory(-1));
        Key(Root, VirtualKey.Right, VirtualKeyModifiers.Menu, () => GoHistory(1));
        Key(Root, VirtualKey.Up, VirtualKeyModifiers.Menu, GoUp);
        Key(Root, VirtualKey.F5, VirtualKeyModifiers.None, RefreshLocation);
        Key(Root, VirtualKey.L, VirtualKeyModifiers.Control, () => {
            ShowAddress();
            return Task.CompletedTask;
        });
        Key(Root, VirtualKey.D, VirtualKeyModifiers.Menu, () => {
            ShowAddress();
            return Task.CompletedTask;
        });
        Key(Root, VirtualKey.F, VirtualKeyModifiers.Control, ShowSearch);
        Key(Search, VirtualKey.Escape, VirtualKeyModifiers.None, HideSearch);
        Key(SearchResults, VirtualKey.Escape, VirtualKeyModifiers.None, HideSearch);
        foreach (var list in new ListViewBase[] { Cards, Rows }) {
            Key(list, VirtualKey.Enter, VirtualKeyModifiers.None, OpenSelected);
            Key(list, VirtualKey.Space, VirtualKeyModifiers.None, PreviewSelected);
            Key(list, VirtualKey.F2, VirtualKeyModifiers.None, RenameSelected);
            Key(list, VirtualKey.Delete, VirtualKeyModifiers.None, RecycleSelected);
            Key(list, VirtualKey.C, VirtualKeyModifiers.Control, () => CopyToClipboard(false));
            Key(list, VirtualKey.X, VirtualKeyModifiers.Control, () => CopyToClipboard(true));
            Key(list, VirtualKey.V, VirtualKeyModifiers.Control, Paste);
            Key(list, VirtualKey.Z, VirtualKeyModifiers.Control, UndoLastMove);
            Key(list, VirtualKey.A, VirtualKeyModifiers.Control, () => {
                SelectAllFiles();
                return Task.CompletedTask;
            });
            Key(list, VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => PromptName());
        }
    }

    void Key(FrameworkElement owner, VirtualKey key, VirtualKeyModifiers modifiers, Func<Task> action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers, ScopeOwner = owner };
        accelerator.Invoked += (sender, args) => {
            args.Handled = true;
            _ = RunUi(action);
        };
        owner.KeyboardAccelerators.Add(accelerator);
    }

    void FilesDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (EntryAt(args.OriginalSource) is not { } file) return;
        args.Handled = true;
        _ = RunUi(() => OpenFile(file));
    }

    Task OpenSelected() => ActiveFiles.SelectedItem is FileEntry file ? OpenFile(file) : Task.CompletedTask;
    Task RenameSelected() => SelectedFiles() is [var file] ? PromptName(file) : Task.CompletedTask;
    Task PreviewSelected() => SelectedFiles() is [var file] ? PreviewFile(file) : Task.CompletedTask;

    Task OpenFile(FileEntry file)
    {
        if (file.Directory) return Navigate(file.Path);
        Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    Task CopyToClipboard(bool cut)
    {
        var items = SelectedFiles();
        if (items.Length == 0 || !available) return Task.CompletedTask;
        ShellClipboard.Put(handle, items.Select(file => file.Path).ToArray(), cut);
        SetStatus(cut ? $"已剪切 {items.Length} 项。请到目标目录粘贴。" : $"已复制 {items.Length} 项。");
        return Task.CompletedTask;
    }

    async Task Paste()
    {
        if (!available || busy || directory is not { } target) return;
        var clipboard = ShellClipboard.Read(handle);
        if (clipboard.Paths.Length == 0) {
            SetStatus("剪贴板中没有文件或文件夹。");
            return;
        }
        var result = await Perform(clipboard.Move ? FileAction.Move : FileAction.Copy, clipboard.Paths, target);
        if (clipboard.Move && result is { Incomplete: false, Errors.Count: 0 }) {
            try { ShellClipboard.ClearIfCurrent(handle, clipboard.Sequence); }
            catch (IOException) { /* Paste has already succeeded. */ }
        }
    }

    async Task PromptName(FileEntry? file = null)
    {
        if (!available || busy || directory is not { } target) return;
        var input = new TextBox { Text = file?.Name ?? "新建文件夹", MinWidth = 280, MaxLength = 255 };
        StyleStationDialogInput(input);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "新名称");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var dialog = new ContentDialog {
            XamlRoot = Root.XamlRoot, Title = file == null ? "新建文件夹" : "重命名",
            Content = new StackPanel { Spacing = 6, Children = { input, error } },
            PrimaryButtonText = file == null ? "新建" : "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
        };
        PanelTheme.CompactDialog(dialog);
        void FocusName() {
            input.Focus(FocusState.Programmatic);
            string extension = file is { Directory: false } ? Path.GetExtension(file.Name) : "";
            input.Select(0, extension.Length < input.Text.Length ? input.Text.Length - extension.Length : input.Text.Length);
        }
        string ValidName() => input.Text.Trim();
        bool CanSubmit() => IsValidName(ValidName());
        bool saving = false;
        input.TextChanged += (_, _) => {
            bool valid = CanSubmit();
            dialog.IsPrimaryButtonEnabled = valid && !saving;
            error.Text = valid ? "" : "请输入有效的文件名。";
            error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        };
        dialog.PrimaryButtonClick += async (_, args) => {
            if (!CanSubmit() || saving) { args.Cancel = true; return; }
            string name = ValidName();
            if (file?.Name == name) return;
            var deferral = args.GetDeferral();
            saving = input.IsReadOnly = true;
            try {
                var result = file == null ? await Perform(FileAction.NewFolder, [], target, name)
                    : await Perform(FileAction.Rename, [file.Path], name: name);
                if (result is not { Incomplete: false, Errors.Count: 0 }) {
                    args.Cancel = true;
                    error.Text = result?.Message ?? Status.Text;
                    error.Visibility = Visibility.Visible;
                } else if (file is { Directory: true } && Path.GetDirectoryName(file.Path) is { } parent) {
                    try { FolderColors.Move(file.Path, Path.Combine(parent, name)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("已重命名；未能更新文件夹颜色。"); }
                }
            }
            finally {
                saving = input.IsReadOnly = false;
                deferral.Complete();
            }
        };
        dialog.Closing += (_, args) => { if (saving) args.Cancel = true; };
        await ShowStationDialog(dialog, FocusName);
    }

    async Task RecycleSelected()
    {
        var files = SelectedFiles();
        if (!available || busy || files.Length == 0) return;
        var dialog = new ContentDialog {
            XamlRoot = Root.XamlRoot, Title = "移到系统回收站？",
            Content = files.Length == 1 ? files[0].Path : $"将所选的 {files.Length} 项移到 Windows 回收站。",
            PrimaryButtonText = "移到回收站", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowStationDialog(dialog) == ContentDialogResult.Primary)
            await Perform(FileAction.Recycle, files.Select(file => file.Path).ToArray());
    }

    async Task<FileOperationResult?> Perform(FileAction action, string[] sources, string? target = null, string? name = null)
    {
        if (busy) return null;
        busy = true;
        lastOperation = null;
        UpdateCommands();
        SetStatus("Windows 正在处理文件…");
        try {
            var candidates = action is FileAction.Rename or FileAction.Move
                ? await Task.Run(() => sources.Distinct(PathComparer).Select(source => MoveUndo.Capture(source,
                    action == FileAction.Move ? Path.Combine(target!, Path.GetFileName(source))
                    : Path.Combine(Path.GetDirectoryName(source)!, name!))).OfType<MoveUndo.Candidate>()
                    .ToDictionary(item => item.Original, PathComparer))
                : null;
            var result = await ShellFileOperations.Run(handle, action, sources, target, name);
            lastOperation = result;
            if (candidates != null) {
                folderUndo = null;
                lastMoves = await Task.Run(() => result.Items
                    .Where(item => item is { Outcome: FileOutcome.Completed, Destination: not null }
                        && candidates.ContainsKey(item.Path))
                    .Select(item => MoveUndo.Confirm(candidates[item.Path], item.Destination!)).OfType<MoveUndo>().ToArray());
            }
            SetStatus(result.Message, warning: result.Incomplete);
            if (action == FileAction.Move) {
                foreach (var item in result.Items.Where(item => item is { Outcome: FileOutcome.Completed, Destination: not null })) {
                    try { FolderColors.Move(item.Path, item.Destination!); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                        SetStatus(result.Message + "未能更新文件夹颜色。");
                        break;
                    }
                }
            }
            return result;
        }
        catch (Exception error) {
            SetStatus(error.Message, warning: true);
            return null;
        }
        finally {
            busy = false;
            if (!closed) {
                if (Notice.Visibility == Visibility.Visible) noticeTimer.Start();
                UpdateCommands();
                ScheduleRefresh();
            }
        }
    }

    void FilesRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        args.Handled = true;
        if (EntryAt(args.OriginalSource) is { } file) {
            if (!ActiveFiles.SelectedItems.Contains(file)) ActiveFiles.SelectedItem = file;
        } else ActiveFiles.SelectedItems.Clear();
        OpenMenu(ActiveFiles, args.GetPosition(ActiveFiles));
    }

    void FilesContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.TryGetPosition(sender, out _)) return;
        args.Handled = true;
        OpenMenu(ActiveFiles);
    }

    void OpenMenu(FrameworkElement anchor, Windows.Foundation.Point? at = null)
    {
        var menu = BuildFileMenu(SelectedFiles());
        if (at is { } position) menu.ShowAt(anchor, new FlyoutShowOptions { Position = position });
        else menu.ShowAt(anchor);
    }

    MenuFlyout BuildFileMenu(FileEntry[] selection, ItemMenuLayout? previewLayout = null, bool preview = false)
    {
        var menu = new MenuFlyout();
        bool writable = preview || available && !busy;
        void MenuAction(string label, Func<Task> action, bool enabled = true, string? shortcut = null)
        {
            AddMenuCommand(menu.Items, label, RunUi, action, enabled, shortcut);
        }

        if (selection.Length == 0) {
            MenuHeading(menu, "AIFile 全局操作");
            var create = new MenuFlyoutSubItem { Text = "新建" };
            AddMenuCommand(create.Items, "文件夹…", RunUi, () => PromptName(), writable, "Ctrl+Shift+N");
            menu.Items.Add(create);
            MenuAction("从剪贴板粘贴", Paste, writable, "Ctrl+V");
            MenuAction("搜索…", ShowSearch, directory != null, "Ctrl+F");
            menu.Items.Add(new MenuFlyoutSeparator());
            var display = new MenuFlyoutSubItem { Text = "查看" };
            foreach (bool isList in new[] { false, true }) {
                var option = new RadioMenuFlyoutItem { Text = isList ? "列表" : "网格", IsChecked = listView == isList, GroupName = "FileView" };
                option.Click += (_, _) => SetView(isList);
                display.Items.Add(option);
            }
            var locations = new ToggleMenuFlyoutItem { Text = "位置栏", IsChecked = placesToggle.IsChecked == true };
            locations.Click += (_, _) => SetPlacesVisible(locations.IsChecked);
            display.Items.Add(locations);
            menu.Items.Add(display);
            var sort = new MenuFlyoutSubItem { Text = "排序" };
            foreach (var field in Enum.GetValues<FileSort>()) {
                string direction = preferences.Sort == field ? (preferences.Descending ? " ↓" : " ↑") : "";
                AddMenuCommand(sort.Items, SortLabel(field) + direction, RunUi, () => {
                    SortBy(field);
                    return Task.CompletedTask;
                });
            }
            menu.Items.Add(sort);
            var manual = new ToggleMenuFlyoutItem { Text = "手动网格（保留空位）", IsChecked = manualGrid?.Enabled == true,
                IsEnabled = available && !loading && query.Length == 0 };
            manual.Click += (_, _) => _ = RunUi(ToggleManualGrid);
            menu.Items.Add(manual);
            if (ManualGridVisible) MenuAction("整理空位", PackManualGrid, writable);
            MenuAction("全选", () => {
                SelectAllFiles();
                return Task.CompletedTask;
            }, entries.Count > 0, "Ctrl+A");
            MenuAction("刷新", RefreshLocation, directory != null, "F5");
            menu.Items.Add(new MenuFlyoutSeparator());
            MenuAction("打开系统回收站", () => {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
                return Task.CompletedTask;
            });
            if (lastOperation != null) MenuAction("上次文件操作详情…", ShowLastOperation);
            if (lastMoves.Length != 0 || folderUndo != null)
                MenuAction(folderUndo?.Recreate == true ? "撤销解散文件夹" : folderUndo != null ? "撤销合并文件夹" : "撤销上次可恢复的移动或重命名",
                    UndoLastMove, !busy, "Ctrl+Z");
            MenuAction("自定义右键菜单…", CustomizeItemMenu);
            MenuAction("设置…", CustomizeAppearance);
            return menu;
        }

        bool single = selection.Length == 1;
        var first = selection[0];
        bool folders = selection.All(item => item.Directory);
        string kind = folders ? "文件夹" : "文件";
        MenuHeading(menu, single ? kind + "操作" : $"已选 {selection.Length} 项");
        var layout = previewLayout ?? MenuLayout(folders);
        AppendMenuLayers(menu, layout.Primary, layout.More, ItemAction.Separator, Command);
        return menu;

        MenuFlyoutItem Entry(ItemAction action, Func<Task> run, bool enabled = true, string? shortcut = null, string? label = null) =>
            MenuCommand(label ?? MenuLabel(action, folders), preview ? _ => Task.CompletedTask : RunUi, run, enabled, shortcut);
        MenuFlyoutItem? Command(ItemAction action) => action switch {
            ItemAction.Open when single => Entry(action, () => OpenFile(first), shortcut: "Enter"),
            ItemAction.Preview when single => Entry(action, () => PreviewFile(first), shortcut: "空格"),
            ItemAction.Copy => Entry(action, () => CopyToClipboard(false), writable, "Ctrl+C",
                single ? null : $"复制所选 {selection.Length} 项"),
            ItemAction.Cut => Entry(action, () => CopyToClipboard(true), writable, "Ctrl+X"),
            ItemAction.Move => Entry(action, () => MoveSelectionTo(selection), writable),
            ItemAction.Reveal when single => Entry(action, () => RevealInExplorer(first)),
            ItemAction.CopyPath => Entry(action, () => CopyPaths(selection)),
            ItemAction.Rename when single => Entry(action, () => PromptName(first), writable, "F2"),
            ItemAction.Color when single && folders => Entry(action, () => ChooseFolderColor(first)),
            ItemAction.Group when !single => Entry(action, () => GroupSelection(selection), writable),
            ItemAction.Dissolve when single && folders && (first.Attributes & System.IO.FileAttributes.ReparsePoint) == 0 =>
                Entry(action, () => DissolveFolder(first), writable),
            ItemAction.Recycle => Entry(action, RecycleSelected, writable, "Delete",
                single ? null : $"将所选 {selection.Length} 项移到系统回收站"),
            _ => null,
        };
    }

    static Task RevealInExplorer(FileEntry file)
    {
        string arguments = file.Directory ? $"\"{file.Path}\"" : $"/select,\"{file.Path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    async Task MoveSelectionTo(FileEntry[] files)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
        var target = await picker.PickSingleFolderAsync();
        if (target != null) await Perform(FileAction.Move, files.Select(file => file.Path).ToArray(), target.Path);
    }

    async Task ShowLastOperation()
    {
        if (lastOperation is not { } result) return;
        var rows = new ListView { MaxHeight = 300, SelectionMode = ListViewSelectionMode.None };
        foreach (var item in result.Items) {
            string outcome = item.Outcome switch {
                FileOutcome.Completed => "已完成", FileOutcome.Skipped => "已跳过或取消",
                FileOutcome.Failed => "失败", _ => "未确认",
            };
            string text = outcome + " · " + item.Path;
            if (item.Destination != null) text += Environment.NewLine + "→ " + item.Destination;
            if (item.Outcome == FileOutcome.Failed && System.Runtime.InteropServices.Marshal.GetExceptionForHR(item.Code) is { } error)
                text += Environment.NewLine + error.Message;
            rows.Items.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "上次文件操作",
            Content = new StackPanel { Spacing = 8, Children = { PanelTheme.Hint(result.Message), rows } },
            CloseButtonText = "关闭",
        };
        await ShowStationDialog(dialog);
    }

    async Task UndoLastMove()
    {
        if (busy || lastMoves.Length == 0 && folderUndo == null) return;
        busy = true;
        try {
            var succeeded = new List<MoveUndo>();
            var rejected = new List<MoveUndo>();
            if (folderUndo is { } organization) lastMoves = await Task.Run(() => PrepareFolderUndo(organization, lastMoves));
            await Task.Run(() => {
                foreach (var item in lastMoves.Reverse()) {
                    try { item.Restore(); succeeded.Add(item); }
                    catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { rejected.Add(item); }
                }
            });
            lastMoves = rejected.ToArray();
            foreach (var item in succeeded) {
                try { FolderColors.Move(item.Current, item.Original); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            if (rejected.Count == 0 && folderUndo is { } completed) {
                if (!completed.Recreate && MoveUndo.Identity(completed.Folder) == completed.Identity) {
                    try { Directory.Delete(completed.Folder, recursive: false); }
                    catch (IOException) {
                        SetStatus("已恢复原位置。文件夹中还有其他项目，已保留文件夹。", warning: true);
                        folderUndo = null;
                        return;
                    }
                }
                folderUndo = null;
                SetStatus("已撤销整组文件夹操作。");
                return;
            }
            SetStatus(rejected.Count == 0 ? $"已撤销 {succeeded.Count} 项。"
                : $"已撤销 {succeeded.Count} 项，{rejected.Count} 项因路径、权限或内容变化保留在原处。", warning: rejected.Count != 0);
        }
        finally {
            busy = false;
            if (Notice.Visibility == Visibility.Visible) noticeTimer.Start();
            ScheduleRefresh();
        }
    }

    static Task CopyPaths(FileEntry[] selection)
    {
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, selection.Select(item => item.Path)));
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return Task.CompletedTask;
    }

    static FileEntry? EntryAt(object? source)
    {
        var element = source as DependencyObject;
        while (element != null) {
            if (element is SelectorItem { Content: FileEntry { EmptySlot: < 0 } entry }) return entry;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    async void StartFileDrag(UIElement sender, DragStartingEventArgs args)
    {
        pendingSelection = null;
        if (EntryAt(sender) is not { } dragged || !available || busy) {
            args.Cancel = true;
            return;
        }
        var selected = SelectedFiles();
        var files = selected.Contains(dragged) ? selected : [dragged];
        var deferral = args.GetDeferral();
        try {
            var storage = await Task.WhenAll(files.Select(async file => file.Directory
                ? (IStorageItem)await StorageFolder.GetFolderFromPathAsync(file.Path)
                : await StorageFile.GetFileFromPathAsync(file.Path)));
            ownDragPaths = files.Select(file => file.Path).ToArray();
            dragOrigin = directory;
            dragAnchor = dragged.Path;
            args.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move;
            args.Data.SetStorageItems(storage, readOnly: false);
            if (PanelTheme.Child<FileGlyph>(sender) is { } glyph) await ApplyDragPreview(args, glyph, files.Length);
        }
        catch (Exception error) {
            ownDragPaths = null;
            args.Cancel = true;
            SetStatus(error.Message);
        }
        finally { deferral.Complete(); }
    }

    void FileDragCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        ClearDragPreview();
        ClearManualDrop();
        ResetDragGrouping();
        CancelDragNavigation();
        ownDragPaths = null;
        dragOrigin = dragAnchor = null;
        hoverPackage = null;
        hoverPaths = null;
        if (args.DropResult != DataPackageOperation.None) ScheduleRefresh();
    }

    string? DropTarget(DragEventArgs args) => EntryAt(args.OriginalSource) is { Directory: true } folder ? folder.Path : directory;

    Task<string[]> IncomingPaths(DataPackageView package)
    {
        if (ownDragPaths != null) return Task.FromResult(ownDragPaths);
        if (ReferenceEquals(hoverPackage, package) && hoverPaths != null) return hoverPaths;
        hoverPackage = package;
        return hoverPaths = ReadPaths();

        async Task<string[]> ReadPaths() => (await package.GetStorageItemsAsync())
            .Select(item => item.Path).Where(Path.IsPathFullyQualified).Distinct(PathComparer).ToArray();
    }

    static DataPackageOperation DropEffect(DragEventArgs args, string[] paths, string target)
    {
        if (paths.Length == 0 || paths.Any(path => IsSameOrBelow(target, path))) return DataPackageOperation.None;
        bool control = args.Modifiers.HasFlag(DragDropModifiers.Control);
        bool shift = args.Modifiers.HasFlag(DragDropModifiers.Shift);
        if (control && shift) return DataPackageOperation.None;
        DataPackageOperation allowed = args.AllowedOperations;
        if (allowed == DataPackageOperation.None) allowed = DataPackageOperation.Copy | DataPackageOperation.Move;
        DataPackageOperation preferred = control ? DataPackageOperation.Copy : shift ? DataPackageOperation.Move : args.DataView.RequestedOperation;
        if (preferred == DataPackageOperation.Link) return DataPackageOperation.None;
        if (preferred is not (DataPackageOperation.Copy or DataPackageOperation.Move)) {
            string volume = ShellFileOperations.VolumeOf(target);
            preferred = paths.All(path => PathComparer.Equals(ShellFileOperations.VolumeOf(path), volume))
                ? DataPackageOperation.Move : DataPackageOperation.Copy;
        }
        if ((allowed & preferred) != 0) return preferred;
        if (control || shift) return DataPackageOperation.None;
        if ((allowed & DataPackageOperation.Copy) != 0) return DataPackageOperation.Copy;
        if ((allowed & DataPackageOperation.Move) != 0) return DataPackageOperation.Move;
        return DataPackageOperation.None;
    }

    async void FilesDragOver(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (available && !busy && TrackDragGrouping(args)) {
            ClearManualDrop();
            CancelDragNavigation();
            args.AcceptedOperation = DataPackageOperation.Move;
            args.DragUIOverride.Caption = "松开以创建文件夹";
            return;
        }
        if (available && !busy && TryGetManualDrop(args, out var manual)) {
            args.AcceptedOperation = manual == null ? DataPackageOperation.None : DataPackageOperation.Move;
            if (manual != null) args.DragUIOverride.Caption = "调整网格位置";
            if (EntryAt(args.OriginalSource) is { Directory: true } folder && !ownDragPaths!.Any(source => IsSameOrBelow(folder.Path, source)))
                ScheduleDragNavigation(folder.Path);
            else CancelDragNavigation();
            return;
        }
        ClearManualDrop();
        if (!available || busy || DropTarget(args) is not { } target || !args.DataView.Contains(StandardDataFormats.StorageItems)) {
            CancelDragNavigation();
            return;
        }
        var deferral = args.GetDeferral();
        try {
            args.AcceptedOperation = DropEffect(args, await IncomingPaths(args.DataView), target);
            if (args.AcceptedOperation != DataPackageOperation.None)
                args.DragUIOverride.Caption = (args.AcceptedOperation == DataPackageOperation.Move ? "移动到 " : "复制到 ") + DirectoryName(target);
            if (args.AcceptedOperation != DataPackageOperation.None && !PathComparer.Equals(target, directory)) ScheduleDragNavigation(target);
            else CancelDragNavigation();
        }
        catch { args.AcceptedOperation = DataPackageOperation.None; }
        finally { deferral.Complete(); }
    }

    async void FilesDrop(object sender, DragEventArgs args)
    {
        CancelDragNavigation();
        args.Handled = true;
        var group = available && !busy && GroupTarget(args) == armedGroupTarget ? armedGroupTarget : null;
        ResetDragGrouping();
        if (group != null && directory is { } parent && entries.FirstOrDefault(file => PathComparer.Equals(file.Path, dragAnchor)) is { } dragged) {
            ClearManualDrop();
            // This is an in-process drag. Complete the OLE exchange before
            // presenting a confirmation or invoking another Shell operation.
            args.AcceptedOperation = DataPackageOperation.Move;
            DispatcherQueue.TryEnqueue(() => _ = RunUi(() => ConfirmDragGroup(parent, dragged, group)));
            return;
        }
        if (available && !busy && TryGetManualDrop(args, out var manual)) {
            if (manual != null) {
                try {
                    PersistGrid(manual);
                    RefreshManualGrid();
                    args.AcceptedOperation = DataPackageOperation.Move;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SetStatus(error.Message, warning: true); }
            }
            ClearManualDrop();
            return;
        }
        ClearManualDrop();
        if (!available || busy || DropTarget(args) is not { } target || !args.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = args.GetDeferral();
        try {
            var paths = await IncomingPaths(args.DataView);
            var effect = DropEffect(args, paths, target);
            if (effect == DataPackageOperation.None) return;
            // The Shell call owns both sides of the transfer. Acknowledging a copy prevents
            // another source from performing delete-on-drop after our Shell move.
            var result = await Perform(effect == DataPackageOperation.Move ? FileAction.Move : FileAction.Copy, paths, target);
            args.AcceptedOperation = result is { Incomplete: false, Errors.Count: 0 }
                ? effect == DataPackageOperation.Move ? DataPackageOperation.None : DataPackageOperation.Copy
                : DataPackageOperation.None;
        }
        catch (Exception error) { SetStatus(error.Message); }
        finally {
            hoverPackage = null;
            hoverPaths = null;
            deferral.Complete();
        }
    }
}
