using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AIFile;

public sealed partial class MainWindow
{
    FileEntry? dragGroupTarget, armedGroupTarget;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? groupTimer;
    readonly Border groupCue = new() { CornerRadius = new(8), IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    void InitializeDragGrouping()
    {
        var label = new TextBlock { Text = "松开以创建文件夹", FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        var cue = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        cue.Children.Add(PanelTheme.Glyph("\uE8B7", 23));
        cue.Children.Add(label);
        groupCue.Child = cue;
        manualDropLayer.Children.Add(groupCue);
        groupTimer = DispatcherQueue.CreateTimer();
        groupTimer.Interval = TimeSpan.FromMilliseconds(300);
        groupTimer.IsRepeating = false;
        groupTimer.Tick += (_, _) => {
            if (dragGroupTarget is not { } item || Cards.ContainerFromItem(item) is not GridViewItem { IsLoaded: true } card) return;
            armedGroupTarget = item;
            DrawGroupCue(card);
        };
    }

    FileEntry? GroupTarget(DragEventArgs args)
    {
        if (listView || query.Length != 0 || SearchPane.Visibility == Visibility.Visible || !IsLocalPlainDrag(args)
            || ownDragPaths is not [var source] ||
            EntryAt(args.OriginalSource) is not { Directory: false } item || PathComparer.Equals(item.Path, source)
            || (item.Attributes & FileAttributes.ReparsePoint) != 0
            || !entries.Any(entry => !entry.Directory && PathComparer.Equals(entry.Path, source)
                && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            || Cards.ContainerFromItem(item) is not GridViewItem card) return null;
        // Match Halo's dwell on an overlapping thumbnail, not the gap between cards.
        var cursor = args.GetPosition(card);
        return new Rect(8, 6, Math.Max(0, card.ActualWidth - 16), Math.Max(0, card.ActualHeight - 12)).Contains(cursor) ? item : null;
    }

    bool TrackDragGrouping(DragEventArgs args)
        => TrackGroupTarget(GroupTarget(args));

    bool TrackGroupTarget(FileEntry? candidate)
    {
        if (candidate != dragGroupTarget) {
            ResetDragGrouping();
            dragGroupTarget = candidate;
            if (candidate != null) groupTimer?.Start();
        }
        return armedGroupTarget != null;
    }

    FileEntry? LocalGridTarget(Point cursor, string source, bool allowFolder)
    {
        if (Cards.ItemsPanelRoot is not { } panel) return null;
        foreach (var card in panel.Children.OfType<GridViewItem>()) {
            if (card.Content is not FileEntry { EmptySlot: < 0 } item || !card.IsLoaded || PathComparer.Equals(source, item.Path)
                || (!allowFolder && item.Directory) || (item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var p = card.TransformToVisual(Cards).TransformPoint(new Point());
            if (new Rect(p.X + 8, p.Y + 6, Math.Max(0, card.ActualWidth - 16), Math.Max(0, card.ActualHeight - 12)).Contains(cursor))
                return item;
        }
        return null;
    }

    void DrawGroupCue(GridViewItem card)
    {
        var corner = card.TransformToVisual(manualDropLayer).TransformPoint(new Point());
        var colors = StationInteractionColors.From(PanelTheme.Appearance.ButtonColor, true, Root.ActualTheme == ElementTheme.Dark);
        colors.ApplyBorder(groupCue, colors.Target, 1);
        Canvas.SetLeft(groupCue, corner.X);
        Canvas.SetTop(groupCue, corner.Y);
        groupCue.Width = card.ActualWidth;
        groupCue.Height = card.ActualHeight;
        groupCue.Visibility = Visibility.Visible;
    }

    void ResetDragGrouping()
    {
        groupTimer?.Stop();
        dragGroupTarget = armedGroupTarget = null;
        groupCue.Visibility = Visibility.Collapsed;
    }

    async Task ConfirmDragGroup(string parent, FileEntry dragged, FileEntry target)
    {
        if (!PathComparer.Equals(parent, directory) || !File.Exists(dragged.Path) || !File.Exists(target.Path)) return;
        await GroupSelection([dragged, target]);
    }
}
