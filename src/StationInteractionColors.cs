using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AIFile;

internal readonly record struct StationInteractionColors(Color Selection, Color Drag, Color Target,
    Color Outline, Color NameText, bool ShowBorders)
{
    internal static StationInteractionColors From(string buttonColor, bool showBorders, bool dark)
    {
        if (PanelTheme.HighContrastPalette is { } system) {
            var fill = system.UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight);
            var text = system.UIElementColor(Windows.UI.ViewManagement.UIElementType.HighlightText);
            return new(fill, fill, fill, text, text, showBorders);
        }

        var color = PanelTheme.PaletteColor(HaloAppearance.ColorValue(buttonColor,
            HaloAppearance.ColorValue(HaloAppearance.DefaultButtonColor, 0)));
        int alpha = Math.Clamp((int)color.A, dark ? 48 : 40, dark ? 100 : 84);
        Color Tint(int opacity) => Color.FromArgb((byte)opacity, color.R, color.G, color.B);
        var selection = Tint(alpha);
        return new(selection, Tint(Math.Max(132, color.A * 4 / 5)), Tint(alpha + 52),
            Tint(Math.Max(166, (int)color.A)), PanelTheme.ReadableText(selection, dark), showBorders);
    }

    internal void ApplySelection(SelectorItem card)
    {
        if (PanelTheme.Child<ListViewItemPresenter>(card) is not { } presenter) return;
        // The expanded name continues this fill outside the card while the pointer moves.
        presenter.SelectedBackground = presenter.SelectedPointerOverBackground =
            presenter.SelectedPressedBackground = new SolidColorBrush(Selection);
        presenter.SelectedBorderBrush = presenter.SelectedPointerOverBorderBrush =
            presenter.SelectedPressedBorderBrush = new SolidColorBrush(Outline);
        presenter.SelectedBorderThickness = new Thickness(ShowBorders ? 1 : 0);
    }

    internal void ApplyBorder(Border surface, Color fill, double thickness)
    {
        surface.Background = new SolidColorBrush(fill);
        surface.BorderBrush = new SolidColorBrush(Outline);
        surface.BorderThickness = new Thickness(ShowBorders ? thickness : 0);
    }
}
