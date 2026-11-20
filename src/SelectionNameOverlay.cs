using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AIFile;

internal sealed class SelectionNameOverlay
{
    readonly TextBlock selectionName = new() {
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 12,
        LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };
    readonly Canvas selectionNameLayer = new() { IsHitTestVisible = false };
    readonly Microsoft.UI.Xaml.Shapes.Path selectionNameExtension = new() { Visibility = Visibility.Collapsed };
    readonly Border selectionNameSurface = new() {
        Visibility = Visibility.Collapsed, Padding = new Thickness(3, 0, 3, 2), CornerRadius = new CornerRadius(4),
    };
    TextBlock? selectedCompactName;
    (Point Label, Point Viewport, double LabelWidth, double ViewWidth, double ViewHeight)? positionedName;

    readonly GridView files;
    readonly Func<bool> suppressed;
    internal SelectionNameOverlay(Grid root, GridView files, FrameworkElement above, Func<bool> suppressed)
    {
        this.files = files;
        this.suppressed = suppressed;
        selectionNameSurface.Child = selectionName;
        selectionNameLayer.Children.Add(selectionNameExtension);
        selectionNameLayer.Children.Add(selectionNameSurface);
        Grid.SetRow(selectionNameLayer, Grid.GetRow(files));
        AutomationProperties.SetAccessibilityView(selectionNameLayer, AccessibilityView.Raw);
        root.Children.Insert(root.Children.IndexOf(above), selectionNameLayer);
        files.SelectionChanged += (_, _) => UpdateSelectionDetails();
        root.LayoutUpdated += (_, _) => PositionSelectionName();
    }

    internal void ApplyColors(StationInteractionColors interactionColors)
    {
        selectionNameExtension.Fill = new SolidColorBrush(interactionColors.Selection);
        selectionName.Foreground = new SolidColorBrush(interactionColors.NameText);
    }


    internal void UpdateSelectionDetails()
    {
        if (selectedCompactName != null) selectedCompactName.Opacity = 1;
        selectedCompactName = null;
        positionedName = null;
        selectionNameSurface.Visibility = Visibility.Collapsed;
        selectionNameExtension.Visibility = Visibility.Collapsed;
        PositionSelectionName();
    }

    void PositionSelectionName()
    {
        GridViewItem? card = null;
        TextBlock? compact = null;
        if (!suppressed() && files.SelectedItems is [FileEntry entry]
            && files.ContainerFromItem(entry) is GridViewItem visible && visible.IsLoaded) {
            card = visible;
            compact = PanelTheme.Child<StackPanel>(visible)?.Children.OfType<TextBlock>().LastOrDefault();
            if (compact != null && selectionName.Text != entry.Name) selectionName.Text = entry.Name;
        }
        if (!ReferenceEquals(compact, selectedCompactName)) {
            if (selectedCompactName != null) selectedCompactName.Opacity = 1;
            selectedCompactName = compact;
            positionedName = null;
            selectionNameSurface.Visibility = Visibility.Collapsed;
            selectionNameExtension.Visibility = Visibility.Collapsed;
        }
        if (card == null || compact is not { IsLoaded: true, ActualWidth: > 0 } label
            || selectionNameLayer.XamlRoot == null || files.ActualHeight <= 0) return;

        var origin = label.TransformToVisual(selectionNameLayer).TransformPoint(new());
        var viewport = files.TransformToVisual(selectionNameLayer).TransformPoint(new());
        var layout = (origin, viewport, label.ActualWidth, files.ActualWidth, files.ActualHeight);
        if (positionedName == layout) return;
        positionedName = layout;
        double bottom = viewport.Y + files.ActualHeight;
        double right = viewport.X + files.ActualWidth;
        if (origin.Y < viewport.Y || origin.Y >= bottom || origin.X + label.ActualWidth <= viewport.X || origin.X >= right) {
            label.Opacity = 1;
            selectionNameSurface.Visibility = Visibility.Collapsed;
            selectionNameExtension.Visibility = Visibility.Collapsed;
            return;
        }

        double availableWidth = right - viewport.X;
        double width = Math.Min(availableWidth, label.ActualWidth + 6);
        selectionName.FontSize = 12;
        selectionName.LineHeight = 15;
        selectionName.Measure(new Size(Math.Max(1, width - 6), double.PositiveInfinity));
        if (selectionName.DesiredSize.Height + 2 > bottom - origin.Y) {
            // Give long names near the last row more horizontal room before raising the label.
            width = Math.Min(availableWidth, Math.Max(width, 240));
            selectionName.Measure(new Size(Math.Max(1, width - 6), double.PositiveInfinity));
        }
        if (selectionName.DesiredSize.Height + 2 > files.ActualHeight) {
            width = availableWidth;
            selectionName.Measure(new Size(Math.Max(1, width - 6), double.PositiveInfinity));
            if (selectionName.DesiredSize.Height + 2 > files.ActualHeight) {
                selectionName.FontSize = 10;
                selectionName.LineHeight = 13;
                selectionName.Measure(new Size(Math.Max(1, width - 6), double.PositiveInfinity));
            }
        }
        double x = Math.Clamp(origin.X + (label.ActualWidth - width) / 2, viewport.X, Math.Max(viewport.X, right - width));
        double y = Math.Clamp(origin.Y, viewport.Y, Math.Max(viewport.Y, bottom - selectionName.DesiredSize.Height - 2));
        if (selectionNameSurface.Width != width) selectionNameSurface.Width = width;
        if (Canvas.GetLeft(selectionNameSurface) != x) Canvas.SetLeft(selectionNameSurface, x);
        if (Canvas.GetTop(selectionNameSurface) != y) Canvas.SetTop(selectionNameSurface, y);
        var cardOrigin = card.TransformToVisual(selectionNameLayer).TransformPoint(new Point());
        double height = selectionName.DesiredSize.Height + 2;
        var all = new Rect(x, y, width, height);
        double left = Math.Max(x, cardOrigin.X), top = Math.Max(y, cardOrigin.Y);
        double insideRight = Math.Min(x + width, cardOrigin.X + card.ActualWidth);
        double insideBottom = Math.Min(y + height, cardOrigin.Y + card.ActualHeight);
        if (left > x || top > y || insideRight < x + width || insideBottom < y + height) {
            var extension = new GeometryGroup { FillRule = FillRule.EvenOdd };
            extension.Children.Add(new RectangleGeometry { Rect = all });
            if (insideRight > left && insideBottom > top)
                extension.Children.Add(new RectangleGeometry { Rect = new Rect(left, top, insideRight - left, insideBottom - top) });
            selectionNameExtension.Data = extension;
            selectionNameExtension.Visibility = Visibility.Visible;
        } else selectionNameExtension.Visibility = Visibility.Collapsed;
        label.Opacity = 0;
        selectionNameSurface.Visibility = Visibility.Visible;
    }

}
