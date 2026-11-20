using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace AIFile;

// The preview host from Halo's file card, realized only for visible containers.
public sealed class FileGlyph : Grid
{
    static readonly SemaphoreSlim thumbnails = new(4);
    readonly Image image = new() { Stretch = Stretch.Uniform };
    readonly FontIcon fallback = PanelTheme.Glyph("\uE8A5", 36 * .72);
    CancellationTokenSource? request;

    public static readonly DependencyProperty EntryProperty = DependencyProperty.Register(
        nameof(Entry), typeof(FileEntry), typeof(FileGlyph), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(FileGlyph), new PropertyMetadata(36d, Changed));

    public FileEntry? Entry { get => (FileEntry?)GetValue(EntryProperty); set => SetValue(EntryProperty, value); }
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    public FileGlyph()
    {
        Children.Add(fallback);
        Children.Add(image);
        Loaded += (_, _) => {
            FolderColors.Changed += RefreshFolder;
            Update();
        };
        Unloaded += (_, _) => {
            FolderColors.Changed -= RefreshFolder;
            request?.Cancel();
        };
    }

    static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((FileGlyph)sender).Update();

    void Update()
    {
        request?.Cancel();
        Width = Height = image.Width = image.Height = IconSize;
        image.Source = null;
        image.Tag = null;
        fallback.Visibility = Entry is { EmptySlot: >= 0 } ? Visibility.Collapsed : Visibility.Visible;
        fallback.FontSize = IconSize * .72;
        if (!IsLoaded || Entry is not { EmptySlot: < 0 } entry) return;
        bool shortcut = ShortcutIcons.IsShortcut(entry.Name);
        fallback.Glyph = entry.Directory ? "\uE8B7" : shortcut ? "\uE71B" : "\uE8A5";
        if (entry.Directory) {
            _ = FolderIcon.Apply(image, FolderColors.Get(entry.Path), fallback);
        } else {
            request = new();
            _ = LoadThumbnail(entry, shortcut, request);
        }
    }

    void RefreshFolder(string path, string color)
    {
        if (Entry is { Directory: true } entry && string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
            _ = FolderIcon.Apply(image, color, fallback);
    }

    internal UIElement CloneIcon(double size) => image.Source is { } bitmap
        ? new Image { Width = size, Height = size, Source = bitmap, Stretch = Stretch.Uniform }
        : PanelTheme.Glyph(fallback.Glyph, size * .72);

    async Task LoadThumbnail(FileEntry entry, bool shortcut, CancellationTokenSource source)
    {
        using (source) {
            var token = source.Token;
            bool held = false;
            try {
                await thumbnails.WaitAsync(token);
                held = true;
                BitmapImage? bitmap;
                if (shortcut) {
                    bitmap = await ShortcutIcons.Load(entry.Path);
                } else {
                    var file = await StorageFile.GetFileFromPathAsync(entry.Path).AsTask(token);
                    using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 128, ThumbnailOptions.ResizeThumbnail).AsTask(token);
                    if (thumb == null) return;
                    bitmap = new BitmapImage { DecodePixelWidth = 128 };
                    await bitmap.SetSourceAsync(thumb);
                }
                if (bitmap == null) return;
                token.ThrowIfCancellationRequested();
                image.Source = bitmap;
                fallback.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
            finally {
                if (held) thumbnails.Release();
                if (request == source) request = null;
            }
        }
    }
}
