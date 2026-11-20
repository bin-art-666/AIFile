using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AIFile;

public sealed partial class MainWindow
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? dragNavigation;
    string? hoverDirectory, hoverOrigin;

    void InitializeDragNavigation()
    {
        dragNavigation = DispatcherQueue.CreateTimer();
        dragNavigation.Interval = TimeSpan.FromMilliseconds(650);
        dragNavigation.IsRepeating = false;
        dragNavigation.Tick += (_, _) => _ = RunUi(EnterHoveredDirectory);
        AddDragTarget(up, () => directory == null ? null : Path.GetDirectoryName(directory));
        AddDragTarget(back, () => historyIndex > 0 ? history[historyIndex - 1] : null);
    }

    void AddDragTarget(Button button, Func<string?> destination)
    {
        button.AllowDrop = true;
        button.DragOver += async (_, args) => {
            args.Handled = true;
            if (!available || busy || destination() is not { } target || !args.DataView.Contains(StandardDataFormats.StorageItems)) {
                CancelDragNavigation();
                return;
            }
            var deferral = args.GetDeferral();
            try {
                var effect = DropEffect(args, await IncomingPaths(args.DataView), target);
                args.AcceptedOperation = effect;
                if (effect == DataPackageOperation.None) CancelDragNavigation();
                else {
                    args.DragUIOverride.Caption = "停留以进入 " + DirectoryName(target);
                    ScheduleDragNavigation(target);
                }
            }
            catch { CancelDragNavigation(); }
            finally { deferral.Complete(); }
        };
        button.DragLeave += (_, _) => CancelDragNavigation();
        button.Drop += (_, args) => {
            CancelDragNavigation();
            args.AcceptedOperation = DataPackageOperation.None;
            args.Handled = true;
        };
    }

    void ScheduleDragNavigation(string target)
    {
        if (PathComparer.Equals(hoverDirectory, target) && PathComparer.Equals(hoverOrigin, directory)) return;
        hoverDirectory = target;
        hoverOrigin = directory;
        dragNavigation?.Stop();
        dragNavigation?.Start();
    }

    void CancelDragNavigation()
    {
        hoverDirectory = hoverOrigin = null;
        dragNavigation?.Stop();
    }

    async Task EnterHoveredDirectory()
    {
        string? target = hoverDirectory, origin = hoverOrigin;
        CancelDragNavigation();
        if (target == null || !PathComparer.Equals(origin, directory) || !available || busy || closed) return;
        // A hover only changes the view. The actual file transfer still starts at Drop.
        if (historyIndex > 0 && PathComparer.Equals(target, history[historyIndex - 1])) await GoHistory(-1);
        else await Navigate(target);
    }

    void FilesDragLeave(object sender, DragEventArgs args)
    {
        CancelDragNavigation();
        ClearManualDrop();
        ResetDragGrouping();
    }

    static bool IsSameOrBelow(string path, string root)
    {
        if (PathComparer.Equals(path, root)) return true;
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
