using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Foundation;

namespace AIFile;

public sealed partial class MainWindow
{
    static double CardWidth => PanelTheme.Appearance.IconSize + 24;
    static double CardHeight => PanelTheme.Appearance.IconSize + 46;
    static double CellWidth => CardWidth + PanelTheme.Appearance.HorizontalGap;
    static double CellHeight => CardHeight + PanelTheme.Appearance.VerticalGap;
    ManualGridLayout? manualGrid;
    string? dragOrigin, dragAnchor;
    bool updatingGrid;
    readonly Border manualDropFrame = new() {
        Width = 60, Height = 82, CornerRadius = new(8), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    readonly Canvas manualDropLayer = new() { IsHitTestVisible = false };

    bool ManualGridVisible => manualGrid?.Enabled == true && query.Length == 0 && !listView
        && SearchPane.Visibility != Visibility.Visible;
    int VisibleColumns => Math.Clamp((int)((Cards.ActualWidth - Cards.Padding.Left - Cards.Padding.Right) / CellWidth), 1, ManualGridLayout.MaxColumns);

    void InitializeManualGrid()
    {
        manualDropLayer.Children.Add(manualDropFrame);
        FilesHost.Children.Insert(FilesHost.Children.IndexOf(Notice), manualDropLayer);
        Cards.SizeChanged += (_, _) => {
            if (ManualGridVisible && available && !loading && !updatingGrid && VisibleColumns > (manualGrid?.Columns ?? 0))
                _ = RunUi(() => { RefreshManualGrid(); return Task.CompletedTask; });
        };
        Cards.Loaded += (_, _) => ConfigureGridPanel();
    }

    void PersistGrid(ManualGridLayout layout)
    {
        if (!temporary && directory is { } folder) ManualGridStore.Write(folder, layout);
        manualGrid = layout;
    }

    Task ToggleManualGrid()
    {
        if (directory == null || loading || !available || query.Length != 0) return Task.CompletedTask;
        var value = manualGrid ?? new(false, VisibleColumns, new(StringComparer.OrdinalIgnoreCase));
        PersistGrid((value with { Enabled = !value.Enabled }).Reconcile(entries, VisibleColumns));
        if (listView && manualGrid?.Enabled == true) SetView(false);
        RefreshManualGrid();
        return Task.CompletedTask;
    }

    Task PackManualGrid()
    {
        if (!ManualGridVisible || manualGrid is not { } layout) return Task.CompletedTask;
        var ordered = entries.OrderBy(file => layout.Cells.GetValueOrDefault(file.Name).Row)
            .ThenBy(file => layout.Cells.GetValueOrDefault(file.Name).Column).ToArray();
        PersistGrid((layout with { Cells = new(StringComparer.OrdinalIgnoreCase) }).Reconcile(ordered, VisibleColumns));
        RefreshManualGrid();
        return Task.CompletedTask;
    }

    void ResetManualProjection()
    {
        if (!ReferenceEquals(Cards.ItemsSource, entries)) Cards.ItemsSource = entries;
        ConfigureGridPanel();
    }

    void RefreshManualGrid()
    {
        if (updatingGrid) return;
        updatingGrid = true;
        try {
            if (!ManualGridVisible || manualGrid is not { } layout || entries.Count == 0) {
                ResetManualProjection();
                return;
            }
            var selected = SelectedFiles().Select(file => file.Path).ToHashSet(PathComparer);
            double offset = PanelTheme.Child<ScrollViewer>(Cards)?.VerticalOffset ?? 0;
            var next = layout.Reconcile(entries, VisibleColumns);
            if (next.Columns != layout.Columns || next.Cells.Count != layout.Cells.Count
                || next.Cells.Any(pair => !layout.Cells.TryGetValue(pair.Key, out var cell) || cell != pair.Value)) {
                try { PersistGrid(next); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SetStatus("无法保存手动网格位置。", warning: true); }
            }
            layout = manualGrid = next;
            // Files beyond the sparse grid's supported extent still use the
            // normal file list so the view never drops entries.
            if (layout.Cells.Count != entries.Count) {
                ResetManualProjection();
                SetStatus("该目录超过手动网格可用范围，已显示全部文件。", warning: true);
                return;
            }
            int last = layout.Cells.Values.Max(cell => cell.Row * layout.Columns + cell.Column);
            int length = Math.Min(layout.Columns * ManualGridLayout.MaxRows,
                Math.Max(last + 1, (int)Math.Ceiling(Cards.ActualHeight / CellHeight) * layout.Columns));
            var slots = new FileEntry[length];
            foreach (var entry in entries) {
                var cell = layout.Cells[entry.Name];
                slots[cell.Row * layout.Columns + cell.Column] = entry;
            }
            for (int index = 0; index < slots.Length; index++)
                slots[index] ??= new("", "", false, default, 0, EmptySlot: index);
            if (Cards.ItemsSource is not FileEntry[] current || !current.SequenceEqual(slots)) {
                Cards.ItemsSource = slots;
                foreach (var entry in entries.Where(file => selected.Contains(file.Path))) Cards.SelectedItems.Add(entry);
            }
            ConfigureGridPanel();
            Cards.UpdateLayout();
            PanelTheme.Child<ScrollViewer>(Cards)?.ChangeView(null, offset, null, true);
        }
        finally { updatingGrid = false; }
    }

    void ConfigureGridPanel()
    {
        bool manual = Cards.ItemsSource is FileEntry[] && ManualGridVisible;
        ScrollViewer.SetHorizontalScrollBarVisibility(Cards, manual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(Cards, manual ? ScrollMode.Enabled : ScrollMode.Disabled);
        if (Cards.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        panel.ItemWidth = CellWidth;
        panel.ItemHeight = CellHeight;
        panel.MaximumRowsOrColumns = manual ? manualGrid!.Columns : -1;
        panel.MinWidth = manual ? manualGrid!.Columns * CellWidth : 0;
    }

    bool IsLocalPlainDrag(DragEventArgs args) => ownDragPaths != null && PathComparer.Equals(dragOrigin, directory)
        && !args.Modifiers.HasFlag(DragDropModifiers.Control) && !args.Modifiers.HasFlag(DragDropModifiers.Shift);

    bool TryGetManualDrop(DragEventArgs args, out ManualGridLayout? moved)
    {
        moved = null;
        if (!ManualGridVisible || Cards.ItemsSource is not FileEntry[] || !IsLocalPlainDrag(args)
            || dragAnchor == null) return false;
        moved = PlanManualDrop(args.GetPosition(Cards), dragAnchor, ownDragPaths!);
        return true;
    }

    ManualGridLayout? PlanManualDrop(Point cursor, string anchor, string[] sources)
    {
        if (manualGrid is not { } layout || Cards.ItemsPanelRoot is not { } panel) return null;
        var origin = panel.TransformToVisual(Cards).TransformPoint(new());
        int column = (int)Math.Floor((cursor.X - origin.X) / CellWidth);
        int row = (int)Math.Floor((cursor.Y - origin.Y) / CellHeight);
        var moved = layout.Drop(Path.GetFileName(anchor), sources.Select(Path.GetFileName).OfType<string>().ToArray(), new(row, column));
        if (moved != null) {
            var corner = Cards.TransformToVisual(manualDropLayer).TransformPoint(
                new Point(origin.X + column * CellWidth + PanelTheme.Appearance.HorizontalGap / 2d,
                    origin.Y + row * CellHeight + PanelTheme.Appearance.VerticalGap / 2d));
            manualDropFrame.Width = CardWidth;
            manualDropFrame.Height = CardHeight;
            var colors = StationInteractionColors.From(PanelTheme.Appearance.ButtonColor, true, Root.ActualTheme == ElementTheme.Dark);
            colors.ApplyBorder(manualDropFrame, colors.Target, 1);
            Canvas.SetLeft(manualDropFrame, corner.X);
            Canvas.SetTop(manualDropFrame, corner.Y);
            manualDropFrame.Visibility = Visibility.Visible;
        } else ClearManualDrop();
        return moved;
    }

    void ClearManualDrop() => manualDropFrame.Visibility = Visibility.Collapsed;

    static void ApplyGridMetrics(SelectorItem item)
    {
        if (item is not GridViewItem) return;
        item.Width = CardWidth;
        item.Height = CardHeight;
        item.Margin = new(PanelTheme.Appearance.HorizontalGap / 2d, PanelTheme.Appearance.VerticalGap / 2d,
            PanelTheme.Appearance.HorizontalGap / 2d, PanelTheme.Appearance.VerticalGap / 2d);
        if (PanelTheme.Child<StackPanel>(item) is { } panel) panel.Width = CardWidth;
        if (PanelTheme.Child<FileGlyph>(item) is { } icon) icon.IconSize = PanelTheme.Appearance.IconSize;
    }

    void SelectAllFiles()
    {
        if (ActiveFiles == Cards && Cards.ItemsSource is FileEntry[]) {
            foreach (var file in entries)
                if (!Cards.SelectedItems.Contains(file)) Cards.SelectedItems.Add(file);
        } else ActiveFiles.SelectAll();
    }
}
