using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace AIFile;

internal sealed class GridMarqueeSelection
{
    const double DragThreshold = 5;
    const double EdgeBand = 28;
    readonly Grid host;
    readonly GridView grid;
    readonly Canvas layer = new() { IsHitTestVisible = false };
    readonly Border frame = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly DispatcherQueueTimer scrollTimer;
    readonly Func<bool> canStart;

    readonly Action clearSelection;

    readonly Action refreshLabels;
    Pointer? pointer;
    HashSet<FileEntry> prior = [];
    readonly Dictionary<FileEntry, Rect> knownFrames = [];
    ScrollViewer? scroll;
    bool active;
    Point origin, startOffset, position;
    bool additive;

    internal bool IsActive => active;

    internal GridMarqueeSelection(Grid host, GridView grid, FrameworkElement notice, Func<bool> canStart,
        Action clearSelection, Action refreshLabels)
    {
        this.host = host;
        this.grid = grid;
        this.canStart = canStart;

        this.clearSelection = clearSelection;

        this.refreshLabels = refreshLabels;
        layer.Children.Add(frame);
        Grid.SetRow(layer, Grid.GetRow(grid));
        host.Children.Insert(host.Children.IndexOf(notice), layer);
        scrollTimer = host.DispatcherQueue.CreateTimer();
        scrollTimer.Interval = TimeSpan.FromMilliseconds(30);
        scrollTimer.IsRepeating = true;
        scrollTimer.Tick += (_, _) => ScrollAtEdge();
        host.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Pressed), true);
        host.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Moved), true);
        host.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Released), true);
        host.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Lost), true);
        host.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((sender, e) => {
            if (IsActive && ReferenceEquals(e.OriginalSource, host)) Lost(sender, e);
        }), true);
        host.PointerExited += (_, e) => {
            var point = e.GetCurrentPoint(host).Position;
            if (!IsActive && pointer?.PointerId == e.Pointer.PointerId &&
                (point.X < 0 || point.Y < 0 || point.X >= host.ActualWidth || point.Y >= host.ActualHeight)) Reset();
        };
        host.PreviewKeyDown += (_, e) => {
            if (!IsActive || e.Key != VirtualKey.Escape) return;
            e.Handled = true;
            Cancel();
        };
        host.Unloaded += (_, _) => Cancel();
    }

    internal void ApplyColors(StationInteractionColors colors)
    {
        var fill = colors.Selection;
        frame.Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Min((int)fill.A, 84), fill.R, fill.G, fill.B));
        frame.BorderBrush = new SolidColorBrush(colors.Outline);
        frame.BorderThickness = new Thickness(1);
    }

    void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (pointer != null || !canStart() || !BlankTarget(e.OriginalSource as DependencyObject)) return;
        var point = e.GetCurrentPoint(grid);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (point.PointerDeviceType != PointerDeviceType.Mouse) {
            if (Viewport().Contains(point.Position)) clearSelection();
            return;
        }
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || !Viewport().Contains(point.Position)) return;
        pointer = e.Pointer;
        RememberSelection(point.Position, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control));
        e.Handled = true;
    }

    HashSet<FileEntry> SelectedIds() => grid.SelectedItems.Cast<FileEntry>().ToHashSet();

    void RememberSelection(Point point, bool extend)
    {
        origin = position = point;
        startOffset = Offset();
        additive = extend;
        prior = SelectedIds();
    }

    void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (pointer?.PointerId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(grid);
        if (!point.Properties.IsLeftButtonPressed) { Cancel(); return; }
        position = point.Position;
        if (!IsActive) {
            if (!canStart()) { Reset(); return; }
            if (Math.Max(Math.Abs(position.X - origin.X), Math.Abs(position.Y - origin.Y)) < DragThreshold) return;
            if (!host.CapturePointer(e.Pointer)) { Reset(); return; }
            Start();
        }
        e.Handled = true;
        additive = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        Render();
    }

    void Released(object sender, PointerRoutedEventArgs e)
    {
        if (pointer?.PointerId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(grid);
        if (point.Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (!IsActive) {
            if (!additive && canStart()) clearSelection();
            Reset();
            return;
        }
        position = point.Position;
        additive = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        Render();
        Reset();
        grid.Focus(FocusState.Programmatic);
    }

    void Lost(object sender, PointerRoutedEventArgs e)
    {
        if (pointer?.PointerId == e.Pointer.PointerId) Cancel();
    }

    void Start()
    {
        active = true;
        scroll = PanelTheme.Child<ScrollViewer>(grid);
        if (scroll != null) scroll.ViewChanged += ViewChanged;
        if (!ReferenceEquals(FocusManager.GetFocusedElement(host.XamlRoot), grid))
            grid.Focus(FocusState.Programmatic);
        refreshLabels();
        scrollTimer.Start();
    }

    void ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => Render();

    void Render()
    {
        var viewport = Viewport();
        var offset = Offset();
        var start = new Point(origin.X + startOffset.X - offset.X, origin.Y + startOffset.Y - offset.Y);
        var end = new Point(Math.Clamp(position.X, viewport.Left, viewport.Right), Math.Clamp(position.Y, viewport.Top, viewport.Bottom));
        var bounds = new Rect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        var shown = Intersect(bounds, viewport);
        var translated = grid.TransformToVisual(layer).TransformPoint(new(shown.X, shown.Y));
        Canvas.SetLeft(frame, translated.X);
        Canvas.SetTop(frame, translated.Y);
        frame.Width = shown.Width;
        frame.Height = shown.Height;
        frame.Visibility = shown.Width > 0 && shown.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
        var contentBounds = new Rect(bounds.X + offset.X, bounds.Y + offset.Y, bounds.Width, bounds.Height);
        if (grid.ItemsPanelRoot is { } panel) {
            foreach (var card in panel.Children.OfType<GridViewItem>()) {
                if (card.Content is not FileEntry { EmptySlot: < 0 } item || !card.IsLoaded) continue;
                var p = card.TransformToVisual(grid).TransformPoint(new());
                var frame = new Rect(p.X, p.Y, card.ActualWidth, card.ActualHeight);
                if (Intersects(viewport, frame)) knownFrames[item] = new Rect(p.X + offset.X, p.Y + offset.Y, frame.Width, frame.Height);
            }
        }
        var next = additive ? prior.ToHashSet() : new HashSet<FileEntry>();
        foreach (var (item, frame) in knownFrames)
            if (Intersects(contentBounds, frame)) next.Add(item);
        Select(next);
    }

    bool BlankTarget(DependencyObject? node)
    {
        for (; node != null && node != host; node = VisualTreeHelper.GetParent(node)) {
            if (node is GridViewItem or Button or Microsoft.UI.Xaml.Controls.Primitives.ScrollBar) return false;
        }
        return node == host;
    }

    static Rect Intersect(Rect left, Rect right)
    {
        double x = Math.Max(left.Left, right.Left), y = Math.Max(left.Top, right.Top);
        return new(x, y, Math.Max(0, Math.Min(left.Right, right.Right) - x), Math.Max(0, Math.Min(left.Bottom, right.Bottom) - y));
    }

    static bool Intersects(Rect box, Rect card)
    {
        var intersection = Intersect(box, card);
        return intersection.Width > 0 && intersection.Height > 0;
    }

    Rect Viewport()
    {
        var viewer = scroll ?? PanelTheme.Child<ScrollViewer>(grid);
        if (viewer != null && PanelTheme.Child<ScrollContentPresenter>(viewer) is { ActualWidth: > 0, ActualHeight: > 0 } viewport) {
            var top = viewport.TransformToVisual(grid).TransformPoint(new());
            return Intersect(new(top.X, top.Y, viewport.ActualWidth, viewport.ActualHeight),
                new(0, 0, grid.ActualWidth, grid.ActualHeight));
        }
        return new(0, 0, grid.ActualWidth, grid.ActualHeight);
    }

    Point Offset()
    {
        var viewer = scroll ?? PanelTheme.Child<ScrollViewer>(grid);
        return viewer == null ? new() : new(viewer.HorizontalOffset, viewer.VerticalOffset);
    }

    void ScrollAtEdge()
    {
        if (scroll == null) return;
        var viewport = Viewport();
        double x = Math.Clamp(scroll.HorizontalOffset + ScrollStep(position.X, viewport.Left, viewport.Right), 0, scroll.ScrollableWidth);
        double y = Math.Clamp(scroll.VerticalOffset + ScrollStep(position.Y, viewport.Top, viewport.Bottom), 0, scroll.ScrollableHeight);
        if (x != scroll.HorizontalOffset || y != scroll.VerticalOffset) scroll.ChangeView(x, y, null, true);
    }

    static double ScrollStep(double point, double start, double end)
    {
        double band = Math.Min(EdgeBand, (end - start) / 3);
        if (point < start + band) return -Math.Clamp(start + band - point, 2, 24);
        if (point > end - band) return Math.Clamp(point - end + band, 2, 24);
        return 0;
    }

    void Select(HashSet<FileEntry> items)
    {
        foreach (var entry in grid.SelectedItems.Cast<FileEntry>().ToArray())
            if (!items.Contains(entry)) grid.SelectedItems.Remove(entry);
        foreach (var entry in items)
            if (!grid.SelectedItems.Contains(entry) && grid.Items.Contains(entry)) grid.SelectedItems.Add(entry);
    }

    internal void Cancel()
    {
        if (IsActive) Select(prior);
        Reset();
    }

    void Reset()
    {
        var captured = pointer;
        pointer = null;
        prior.Clear();
        knownFrames.Clear();
        frame.Visibility = Visibility.Collapsed;
        scrollTimer.Stop();
        if (scroll != null) scroll.ViewChanged -= ViewChanged;
        scroll = null;
        if (!active) return;
        active = false;
        if (captured != null) host.ReleasePointerCapture(captured);
        refreshLabels();
    }
}
