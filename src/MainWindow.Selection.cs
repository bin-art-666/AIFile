using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace AIFile;

public sealed partial class MainWindow
{
    sealed record SelectionPress(Pointer Pointer, FrameworkElement Grip, FileEntry File, Point Origin, bool? ApplyOnRelease, bool Captured);
    SelectionPress? pendingSelection;
    FileEntry? selectionAnchor;
    PointerEventHandler? fileGripHandler;
    FileEntry[]? localDragSelection;

    void InitializeCardSelection()
    {
        FilesHost.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ReleaseFileSelection), true);
        FilesHost.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(DragFileSelection), true);
        var cancel = new PointerEventHandler((_, args) => {
            if (pendingSelection is { } press && press.Pointer.PointerId == args.Pointer.PointerId) {
                pendingSelection = null;
                press.Grip.CanDrag = true;
                ResetLocalGridDrag();
            }
        });
        FilesHost.AddHandler(UIElement.PointerCanceledEvent, cancel, true);
        FilesHost.AddHandler(UIElement.PointerCaptureLostEvent, cancel, true);
        FilesHost.PreviewKeyDown += (_, args) => {
            if (args.Key != VirtualKey.Escape || localDragSelection == null || pendingSelection is not { } press) return;
            pendingSelection = null;
            ResetLocalGridDrag();
            press.Grip.CanDrag = true;
            if (press.Captured) press.Grip.ReleasePointerCapture(press.Pointer);
            args.Handled = true;
        };
    }

    void FileGripLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not UIElement grip) return;
        var handler = fileGripHandler ??= new PointerEventHandler(PressFileSelection);
        grip.RemoveHandler(UIElement.PointerPressedEvent, handler);
        grip.AddHandler(UIElement.PointerPressedEvent, handler, true);
    }

    void PressFileSelection(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not FrameworkElement grip) return;
        var point = args.GetCurrentPoint(grip);
        if (point.PointerDeviceType != PointerDeviceType.Mouse || point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || EntryAt(sender) is not { } file) return;
        var view = ActiveFiles;
        bool control = args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        bool shift = args.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        bool selected = view.SelectedItems.Contains(file);
        var anchor = selectionAnchor ?? view.SelectedItem as FileEntry;
        if (view.ContainerFromItem(file) is Control card) card.Focus(FocusState.Pointer);
        args.Handled = true;
        bool? applyOnRelease = selected && !shift && (control || view.SelectedItems.Count > 1) ? control : null;
        ResetLocalGridDrag();
        if (applyOnRelease == null) SelectFile(file, control, shift, anchor);
        // Match Halo: claim selection at the inner drag surface and only start a
        // native drag after movement has separated it from an ordinary click.
        grip.CanDrag = false;
        bool captured = grip.CapturePointer(args.Pointer);
        if (!captured) grip.CanDrag = true;
        pendingSelection = new(args.Pointer, grip, file, args.GetCurrentPoint(FilesHost).Position, applyOnRelease, captured);
    }

    void ReleaseFileSelection(object sender, PointerRoutedEventArgs args)
    {
        if (pendingSelection is not { } press || press.Pointer.PointerId != args.Pointer.PointerId ||
            args.GetCurrentPoint(FilesHost).Properties.IsLeftButtonPressed) return;
        pendingSelection = null;
        if (localDragSelection is { } locallyDragged) {
            var cursor = args.GetCurrentPoint(Cards).Position;
            var group = armedGroupTarget == LocalGridTarget(cursor, press.File.Path, allowFolder: false) ? armedGroupTarget : null;
            var slot = group == null && ManualGridVisible
                ? PlanManualDrop(cursor, press.File.Path, locallyDragged.Select(item => item.Path).ToArray()) : null;
            var folder = !ManualGridVisible && group == null
                ? LocalGridTarget(cursor, press.File.Path, allowFolder: true) : null;
            string? parent = directory;
            ResetLocalGridDrag();
            press.Grip.CanDrag = true;
            if (press.Captured) press.Grip.ReleasePointerCapture(press.Pointer);
            args.Handled = true;
            if (group != null && parent != null) _ = RunUi(() => ConfirmDragGroup(parent, press.File, group));
            else if (slot != null) _ = RunUi(() => {
                PersistGrid(slot);
                RefreshManualGrid();
                return Task.CompletedTask;
            });
            else if (folder is { Directory: true } && locallyDragged.All(item => !IsSameOrBelow(folder.Path, item.Path)))
                _ = RunUi(() => Perform(FileAction.Move, locallyDragged.Select(item => item.Path).ToArray(), folder.Path));
            return;
        }
        press.Grip.CanDrag = true;
        if (press.Captured) press.Grip.ReleasePointerCapture(press.Pointer);
        args.Handled = true;
        var position = args.GetCurrentPoint(press.Grip).Position;
        if (press.ApplyOnRelease is bool toggle && position.X >= 0 && position.Y >= 0
            && position.X < press.Grip.ActualWidth && position.Y < press.Grip.ActualHeight)
            SelectFile(press.File, toggle, false, null);
    }

    async void DragFileSelection(object sender, PointerRoutedEventArgs args)
    {
        if (pendingSelection is not { Captured: true } press || press.Pointer.PointerId != args.Pointer.PointerId) return;
        var point = args.GetCurrentPoint(FilesHost);
        if (!point.Properties.IsLeftButtonPressed || busy || !available) return;
        if (Math.Max(Math.Abs(point.Position.X - press.Origin.X), Math.Abs(point.Position.Y - press.Origin.Y)) < 4) return;
        var selected = localDragSelection ?? SelectedFiles();
        if (!selected.Contains(press.File)) selected = [press.File];
        bool plain = !args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) && !args.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        bool canGroup = selected.Length == 1 && !press.File.Directory && (press.File.Attributes & FileAttributes.ReparsePoint) == 0;
        if (!listView && query.Length == 0 && plain && (ManualGridVisible || canGroup)
            && new Rect(0, 0, FilesHost.ActualWidth, FilesHost.ActualHeight).Contains(point.Position)) {
            localDragSelection = selected;
            var cursor = args.GetCurrentPoint(Cards).Position;
            if (canGroup && TrackGroupTarget(LocalGridTarget(cursor, press.File.Path, allowFolder: false))) ClearManualDrop();
            else if (ManualGridVisible) PlanManualDrop(cursor, press.File.Path, selected.Select(item => item.Path).ToArray());
            args.Handled = true;
            return;
        }
        ResetLocalGridDrag();
        var nativePoint = args.GetCurrentPoint(press.Grip);
        pendingSelection = null;
        args.Handled = true;
        press.Grip.ReleasePointerCapture(press.Pointer);
        press.Grip.CanDrag = true;
        try { await press.Grip.StartDragAsync(nativePoint); }
        catch (Exception error) { if (!closed) SetStatus(error.Message, warning: true); }
        finally { ownDragPaths = null; ClearDragPreview(); CancelDragNavigation(); }
    }

    void ResetLocalGridDrag()
    {
        localDragSelection = null;
        ClearManualDrop();
        ResetDragGrouping();
    }

    void SelectFile(FileEntry file, bool control, bool shift, FileEntry? anchor)
    {
        var selected = ActiveFiles.SelectedItems;
        if (shift) {
            var ordered = ManualGridVisible && Cards.ItemsSource is FileEntry[] slots
                ? slots.Where(item => item.EmptySlot < 0).ToArray() : entries.ToArray();
            int current = Array.IndexOf(ordered, file);
            int start = anchor == null ? current : Array.IndexOf(ordered, anchor);
            if (current < 0) return;
            if (start < 0) start = current;
            if (!control) selected.Clear();
            for (int index = Math.Min(start, current); index <= Math.Max(start, current); index++)
                if (!selected.Contains(ordered[index])) selected.Add(ordered[index]);
            selectionAnchor ??= ordered[start];
            return;
        }
        if (control) {
            if (!selected.Remove(file)) selected.Add(file);
        } else if (selected.Count != 1 || !selected.Contains(file)) {
            selected.Clear();
            selected.Add(file);
        }
        selectionAnchor = file;
    }
}
