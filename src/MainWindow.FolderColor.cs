using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AIFile;

public sealed partial class MainWindow
{
    internal async Task ChooseFolderColor(FileEntry item)
    {
        void Refresh(string color) => FolderColors.Preview(item.Path, color);
        string defaultColor = HaloAppearance.FolderColor;
        string? original = FolderColors.GetOverride(item.Path);
        var sample = new Image { Width = 54, Height = 54, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(sample, "文件夹颜色预览");
        var useDefault = new CheckBox { Content = "使用默认颜色", IsChecked = original == null, TabIndex = 0, UseSystemFocusVisuals = true };
        var hex = new TextBox {
            MaxLength = 7, Text = original ?? defaultColor, PlaceholderText = "#RRGGBB", TabIndex = 7,
        };
        AutomationProperties.SetName(hex, "自定义颜色色值");
        StyleStationDialogInput(hex);
        var palette = new Grid { ColumnSpacing = 6 };
        var swatches = new List<(string Color, ToggleButton Button, FontIcon Mark)>();
        foreach (var (label, color) in new[] {
            ("蓝色", HaloAppearance.FolderColor), ("青色", "#05A6B5"), ("绿色", "#35A271"),
            ("紫色", "#845AD6"), ("橙色", "#E79936"), ("珊瑚红", "#E37068"),
        }) {
            var fill = PanelTheme.PaletteColor(HaloAppearance.ColorValue(color, 0));
            var mark = PanelTheme.Glyph("\uE73E", 12);
            mark.Foreground = new SolidColorBrush(PanelTheme.ReadableText(fill, dark: false));
            var chip = new Border {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(fill), Child = mark,
            };
            var button = new ToggleButton {
                Content = chip, MinWidth = 0, MinHeight = 36, Padding = new Thickness(3), TabIndex = swatches.Count + 1,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            PanelTheme.StyleTextAction(button);
            if (PanelTheme.HighContrastPalette == null) {
                button.Resources["ToggleButtonBackgroundChecked"] = new SolidColorBrush(Windows.UI.Color.FromArgb(48, fill.R, fill.G, fill.B));
                button.Resources["ToggleButtonBackgroundCheckedPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(64, fill.R, fill.G, fill.B));
                button.Resources["ToggleButtonBackgroundCheckedPressed"] = new SolidColorBrush(Windows.UI.Color.FromArgb(80, fill.R, fill.G, fill.B));
            }
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, $"{label} {color}");
            palette.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(button, swatches.Count);
            palette.Children.Add(button);
            swatches.Add((color, button, mark));
        }
        var picker = new ColorPicker {
            IsAlphaEnabled = false, IsColorChannelTextInputVisible = false, IsHexInputVisible = false,
        };
        var pickerFlyout = new Flyout { Content = picker };
        var choose = new Button { Content = "选取颜色", Flyout = pickerFlyout, TabIndex = 8 };
        PanelTheme.StyleTextAction(choose);
        var custom = new Grid { ColumnSpacing = 8, Children = { hex, choose } };
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(choose, 1);
        var error = new TextBlock { Text = "请输入 #RRGGBB 颜色值", Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel {
            Spacing = 8, Children = { sample, useDefault, PanelTheme.Hint("常用色"), palette, PanelTheme.Hint("自定义颜色"), custom, error },
        };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "设置文件夹颜色",
            Content = new ScrollViewer { Content = body, MaxHeight = 320, IsTabStop = false }, PrimaryButtonText = "保存",
            CloseButtonText = "取消" };
        bool syncing = false;
        void Update()
        {
            bool inherited = useDefault.IsChecked == true;
            string color = inherited ? defaultColor : hex.Text.ToUpperInvariant();
            bool valid = MaterialOptions.IsColor(color);
            dialog.IsPrimaryButtonEnabled = valid;
            error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            syncing = true;
            foreach (var (preset, button, mark) in swatches) {
                bool selected = !inherited && valid && preset == color;
                button.IsChecked = selected;
                mark.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }
            if (valid) picker.Color = PanelTheme.PaletteColor(HaloAppearance.ColorValue(color, 0));
            syncing = false;
            if (!valid) return;
            _ = FolderIcon.Apply(sample, color);
            Refresh(color);
        }
        void SelectColor(string color)
        {
            syncing = true;
            useDefault.IsChecked = false;
            hex.Text = color;
            syncing = false;
            Update();
        }
        useDefault.Checked += (_, _) => { if (!syncing) Update(); };
        useDefault.Unchecked += (_, _) => { if (!syncing) Update(); };
        hex.TextChanged += (_, _) => { if (!syncing) SelectColor(hex.Text); };
        foreach (var (color, button, _) in swatches) {
            button.Checked += (_, _) => { if (!syncing) SelectColor(color); };
            button.Unchecked += (_, _) => { if (!syncing) Update(); };
        }
        picker.ColorChanged += (_, e) => {
            if (!syncing) SelectColor($"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}");
        };
        Update();
        try {
            if (await ShowStationDialog(dialog, () => useDefault.Focus(FocusState.Programmatic), pickerFlyout, Update)
                == ContentDialogResult.Primary)
                FolderColors.Save(item.Path, useDefault.IsChecked == true ? null : hex.Text.ToUpperInvariant());
        } finally {
            Refresh(FolderColors.GetOverride(item.Path) ?? HaloAppearance.FolderColor);
        }
    }
}
