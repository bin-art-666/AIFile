using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace AIFile;

public sealed partial class MainWindow
{
    SoftwareBitmap? outgoingPreview;

    async Task ApplyDragPreview(DragStartingEventArgs args, FileGlyph source, int count)
    {
        const double size = 36, padding = 6;
        var thumbnail = new Grid { Width = size, Height = size };
        thumbnail.Children.Add(source.CloneIcon(size));
        if (count > 1) thumbnail.Children.Add(new Border {
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 33, 87, 134)), CornerRadius = new(8),
            Padding = new(5, 1, 5, 1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = count.ToString(), FontSize = 11, Foreground = new SolidColorBrush(Colors.White) },
        });
        var appearance = PanelTheme.Appearance;
        var colors = StationInteractionColors.From(appearance.ButtonColor, appearance.ShowInteractionBorders, Root.ActualTheme == ElementTheme.Dark);
        var frame = new Border { Child = thumbnail, CornerRadius = new(8) };
        colors.ApplyBorder(frame, colors.Drag, 1);
        frame.Padding = new(padding - frame.BorderThickness.Left);
        var grip = args.GetPosition(source);
        var grab = new Point(Math.Clamp((grip.X + padding) / (size + padding * 2), 0, 1),
            Math.Clamp((grip.Y + padding) / (size + padding * 2), 0, 1));
        var placement = new DragPreviewPlacement(appearance.DragPreview, appearance.DragDistance);
        var (anchor, offset) = placement.Layout(size + padding * 2, grab);
        var point = args.GetPosition(FilesHost);
        var visual = new Border { Child = frame, Padding = offset, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new(Math.Max(0, point.X - anchor.X), Math.Max(0, point.Y - anchor.Y), 0, 0) };
        FilesHost.Children.Add(visual);
        try {
            FilesHost.UpdateLayout();
            var rendered = new RenderTargetBitmap();
            await rendered.RenderAsync(visual);
            if (rendered.PixelWidth == 0 || rendered.PixelHeight == 0) return;
            var buffer = await rendered.GetPixelsAsync();
            if (closed) return;
            ClearDragPreview();
            outgoingPreview = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8,
                rendered.PixelWidth, rendered.PixelHeight, BitmapAlphaMode.Premultiplied);
            var scale = Root.XamlRoot.RasterizationScale;
            args.DragUI.SetContentFromSoftwareBitmap(outgoingPreview, new(
                Math.Min(anchor.X * scale, rendered.PixelWidth - 1), Math.Min(anchor.Y * scale, rendered.PixelHeight - 1)));
        }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
        finally { FilesHost.Children.Remove(visual); }
    }

    void ClearDragPreview()
    {
        outgoingPreview?.Dispose();
        outgoingPreview = null;
    }
}
