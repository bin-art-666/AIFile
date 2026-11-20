using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace AIFile;

public sealed partial class MainWindow
{
    ContentDialog? activeContentDialog;

    async Task<ContentDialogResult> ShowStationDialog(ContentDialog dialog,
        Action? focusContent = null, Flyout? childFlyout = null, Action? refreshPreview = null,
        Func<bool>? childFlyoutOpen = null)
    {
        if (activeContentDialog != null) return ContentDialogResult.None;
        var grid = ActiveFiles;
        dialog.XamlRoot = Root.XamlRoot;
        dialog.RequestedTheme = Root.ActualTheme;
        PanelTheme.StyleDialog(dialog);
        var existingPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(dialog.XamlRoot).ToHashSet();
        bool shadeReady = false, footerReady = false;
        void FinishLayout(object? sender, object e)
        {
            if (activeContentDialog != dialog) return;
            if (!shadeReady) {
                // Popup placement moves the native shade into a different popup.
                var shade = VisualTreeHelper.GetOpenPopupsForXamlRoot(dialog.XamlRoot)
                    .Where(popup => !existingPopups.Contains(popup)).Select(popup => popup.Child)
                    .OfType<Rectangle>().FirstOrDefault(element => element.Name == "SmokeLayerBackground");
                if (shade != null) {
                    shadeReady = true;
                    RegisterDialogBlankDismiss(shade, dialog, grid,
                        () => childFlyout?.IsOpen == true || childFlyoutOpen?.Invoke() == true);
                }
            }
            if (!footerReady) SetupFooter();
            if (shadeReady && footerReady) Root.LayoutUpdated -= FinishLayout;
        }
        void SetupFooter()
        {
            var closeButton = Descendants<Button>(dialog).FirstOrDefault(button => button.Name == "CloseButton");
            var actionButton = Descendants<Button>(dialog).FirstOrDefault(button => button.Name == "PrimaryButton");
            bool hasAction = !string.IsNullOrEmpty(dialog.PrimaryButtonText);
            if (closeButton == null || hasAction && actionButton == null) return;
            footerReady = true;
            closeButton.TabIndex = 9;
            closeButton.IsTabStop = true;
            PanelTheme.StyleTextAction(closeButton);
            if (hasAction && actionButton != null) {
                int left = Grid.GetColumn(actionButton);
                Grid.SetColumn(actionButton, Grid.GetColumn(closeButton));
                Grid.SetColumn(closeButton, left);
                actionButton.TabIndex = 10;
                actionButton.IsTabStop = true;
                PanelTheme.StyleTextAction(actionButton, useButtonColor: true);
            }
            if (focusContent != null) focusContent();
            else closeButton.Focus(FocusState.Programmatic);
        }
        dialog.Opened += (_, e) => FinishLayout(dialog, e);

        TypedEventHandler<FrameworkElement, object> themeChanged = (_, _) => {
            dialog.RequestedTheme = Root.ActualTheme;
            if (refreshPreview != null)
                DispatcherQueue.TryEnqueue(() => { if (activeContentDialog == dialog) refreshPreview(); });
        };
        Root.ActualThemeChanged += themeChanged;
        activeContentDialog = dialog;
        Root.LayoutUpdated += FinishLayout;
        try {
            var showing = dialog.ShowAsync();
            DispatcherQueue.TryEnqueue(() => FinishLayout(dialog, EventArgs.Empty));
            return await showing;
        }
        finally {
            Root.LayoutUpdated -= FinishLayout;
            Root.ActualThemeChanged -= themeChanged;
            activeContentDialog = null;
            if (grid.IsLoaded && grid.SelectedItem is FileEntry item && grid.ContainerFromItem(item) is Control selected)
                selected.Focus(FocusState.Programmatic);
        }
    }

    static void RegisterDialogBlankDismiss(Rectangle shade, ContentDialog dialog, ListViewBase grid, Func<bool> childFlyoutOpen)
    {
        const double clickSlop = 4;
        (uint Id, Point Position)? press = null;
        // Match both ends of the click so a release from a child flyout cannot dismiss the dialog.
        shade.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => {
            var point = e.GetCurrentPoint(null);
            press = !childFlyoutOpen() && point.Properties.IsLeftButtonPressed && DialogGridBlankAt(grid, point.Position)
                ? (point.PointerId, point.Position) : null;
            e.Handled = true;
        }), true);
        shade.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => {
            var point = e.GetCurrentPoint(null);
            bool dismiss = press is { } start && start.Id == point.PointerId && !childFlyoutOpen()
                && Math.Abs(start.Position.X - point.Position.X) <= clickSlop
                && Math.Abs(start.Position.Y - point.Position.Y) <= clickSlop
                && DialogGridBlankAt(grid, point.Position);
            press = null;
            e.Handled = true;
            if (dismiss) dialog.Hide();
        }), true);
        shade.PointerExited += (_, _) => press = null;
        shade.PointerCanceled += (_, _) => press = null;
    }

    static bool DialogGridBlankAt(ListViewBase grid, Point point)
    {
        var target = VisualTreeHelper.FindElementsInHostCoordinates(point, grid).FirstOrDefault();
        return ContentBlankTarget(target, grid);
    }

    static void StyleStationDialogInput(TextBox input)
    {
        var border = new SolidColorBrush();
        var focus = new SolidColorBrush();
        input.UseSystemFocusVisuals = false;
        input.CornerRadius = PanelTheme.Value<CornerRadius>("ControlRadius");
        input.BorderBrush = border;
        input.BorderThickness = new Thickness(1);
        input.Resources["TextControlBorderBrushPointerOver"] = border;
        input.Resources["TextControlBorderBrushFocused"] = focus;
        input.Resources["TextControlBorderThemeThicknessFocused"] = input.BorderThickness;

        void Update()
        {
            bool dark = input.ActualTheme == ElementTheme.Dark;
            if (PanelTheme.HighContrastPalette is { } system) {
                border.Color = system.UIElementColor(Windows.UI.ViewManagement.UIElementType.WindowText);
                focus.Color = system.UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight);
            } else {
                border.Color = PanelTheme.PaletteColor(dark ? 0xFF677683 : 0xFFA1AEB5);
                focus.Color = Application.Current.Resources.TryGetValue(dark ? "SystemAccentColorLight2" : "SystemAccentColorDark1", out var value)
                    && value is Windows.UI.Color accent ? accent : PanelTheme.PaletteColor(dark ? 0xFF6CBFFF : 0xFF0067C0);
            }
        }
        Update();
        input.ActualThemeChanged += (_, _) => Update();
    }
    static bool ContentBlankTarget(DependencyObject? target, DependencyObject boundary) {
        while (target != null) {
            if (target is SelectorItem or ButtonBase or Thumb or ScrollBar or TextBox or PasswordBox) return false;
            if (ReferenceEquals(target, boundary)) return true;
            target = VisualTreeHelper.GetParent(target);
        }
        return false;
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
