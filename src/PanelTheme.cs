using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AIFile;

internal static partial class PanelTheme
{
    internal static HaloAppearance Appearance { get; private set; } = new();

    internal static void UseAppearance(HaloAppearance appearance)
    {
        Appearance = appearance;
        RefreshThemedButtons();
    }

    internal static void ApplyDefaults()
    {
        var resources = Application.Current.Resources;
        resources["HaloActionSize"] = 30d;
        resources["HaloControlRadius"] = new CornerRadius(16);
        resources["HaloIconSize"] = 16.5d;
    }

    internal static T Value<T>(string key) => (T)Application.Current.Resources["Halo" + key];
    internal static Style Style(string name) => Value<Style>(name + "Style");

    internal static T Track<T>(T element, Action<T> apply) where T : FrameworkElement
    {
        apply(element);
        return element;
    }

    internal static void Label(FrameworkElement element, string name)
    {
        AutomationProperties.SetName(element, name);
        ToolTipService.SetToolTip(element, name);
    }

    internal static T? Child<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (Child<T>(child) is { } nested) return nested;
        }
        return null;
    }

    static MaterialOptions Material() => new(TintOpacity: (float)Value<double>("PanelTintOpacity"), LuminosityOpacity: (float)Value<double>("PanelLuminosityOpacity"));
    internal static GlassBackdrop DesktopBackdrop() => new(new MaterialOptions(TintOpacity: 0, LuminosityOpacity: 0, NoBase: true)) { TransparentCanvas = true };
    internal static void ApplyDesktopBackdrop(Grid root, BackplateSurface surface, GlassBackdrop glass, bool maximized)
    {
        var appearance = Appearance;
        root.CornerRadius = surface.CornerRadius = new CornerRadius(maximized ? 0 : appearance.BackplateRadius);
        // The window blurs the desktop. The XAML surface supplies the tint.
        surface.UpdateAppearance(appearance with { BackplateBlur = 0 }, surface.ActualTheme == ElementTheme.Dark);
        glass.UpdateBlur(appearance.BackplateBlur);
        glass.ReapplyTransparentFrame();
        ClipDesktopBackdrop(root);
    }
    internal static void ClipDesktopBackdrop(Grid root)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(root);
        var current = visual.Clip as Microsoft.UI.Composition.RectangleClip;
        var clip = current ?? visual.Compositor.CreateRectangleClip();
        var radius = new System.Numerics.Vector2((float)root.CornerRadius.TopLeft);
        clip.TopLeftRadius = clip.TopRightRadius = clip.BottomLeftRadius = clip.BottomRightRadius = radius;
        clip.Right = (float)root.ActualWidth; clip.Bottom = (float)root.ActualHeight;
        if (current == null) visual.Clip = clip;
    }
    internal static Windows.UI.Color BackplateColor(HaloAppearance appearance, bool dark)
    {
        var color = Material().FlatColor(dark);
        double baselineOpacity = (100 - HaloAppearance.TransparencyReference) / 100d;
        byte alpha = appearance.BackplateTransparency == 0 ? (byte)255
            : (byte)Math.Clamp(Math.Round(color.Alpha * (100 - appearance.BackplateTransparency) / 100d / baselineOpacity), 0, 255);
        return Windows.UI.Color.FromArgb(alpha, (byte)(color.Rgb >> 16), (byte)(color.Rgb >> 8), (byte)color.Rgb);
    }
    internal static FontIcon Glyph(string glyph, double? size = null)
    {
        var icon = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = size ?? Value<double>("IconSize") };
        return size != null ? icon : Track(icon, static value => value.FontSize = Value<double>("IconSize"));
    }
    internal static void IconStyle(ButtonBase button, string name)
    {
        button.Style = Style(button is ToggleButton ? "IconToggle" : "IconButton");
        Track(button, static value => { value.Width = value.Height = Value<double>("ActionSize"); value.CornerRadius = Value<CornerRadius>("ControlRadius"); });
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name);
    }
    internal static Button Icon(string glyph, string name, double? size = null)
    {
        var button = new Button { Content = Glyph(glyph, size) }; IconStyle(button, name); return button;
    }
    // Framed actions share native keyboard, pointer and checked states.
    internal static void FramedIconStyle(ButtonBase button, string name)
    {
        IconStyle(button,name);
        button.Style=Style(button is ToggleButton ? "FramedToggle" : "ItemAction");
        RegisterThemedButton(button);
        Track(button, static value => { value.CornerRadius=Value<CornerRadius>("ControlRadius"); ApplyButtonColors(value); });
        button.ActualThemeChanged += (_, _) => ApplyButtonColors(button);
    }
    internal static TextBlock Title(string text) => new() { Text = text, FontSize = Value<double>("TitleSize"),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis };

    internal static TextBlock Hint(string text) => new() { Text = text, FontSize = Value<double>("HintSize"),
        Opacity = .7, TextWrapping = TextWrapping.Wrap };

    internal static void CompactDialog(ContentDialog dialog)
    {
        dialog.Resources["ContentDialogPadding"] = new Thickness(16, 12, 16, 12);
        dialog.Resources["ContentDialogTitleMargin"] = new Thickness(0, 0, 0, 8);
        dialog.Resources["ContentDialogMinHeight"] = 0d;
    }

    internal static void StyleTextAction(ButtonBase button, bool useButtonColor = false)
    {
        button.CornerRadius = Value<CornerRadius>("ControlRadius");
        button.UseSystemFocusVisuals = true;
        if (!useButtonColor) return;
        ApplyButtonColors(button);
        button.ActualThemeChanged += (_, _) => ApplyButtonColors(button);
        RegisterThemedButton(button);
    }

    internal static Windows.UI.Color SolidContentSurface(bool dark) =>
        dark ? PaletteColor(0xFF26292D) : PaletteColor(0xFFF6F9FA);

    internal static void StyleDialog(ContentDialog dialog)
    {
        dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        void Apply()
        {
            dialog.CornerRadius = new CornerRadius(Appearance.BackplateRadius);
            if (HighContrastPalette != null) dialog.ClearValue(Control.BackgroundProperty);
            else dialog.Background = new SolidColorBrush(SolidContentSurface(dialog.ActualTheme == ElementTheme.Dark));
        }
        Apply();
        dialog.ActualThemeChanged += (_, _) => Apply();
    }

}
