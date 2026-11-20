using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIFile;

public sealed partial class MainWindow
{
    async Task CustomizeAppearance()
    {
        var original = PanelTheme.Appearance;
        var appearance = new StackPanel { Spacing = 8 };
        var drag = new StackPanel { Spacing = 8 };
        var notices = new StackPanel { Spacing = 8 };
        var window = new StackPanel { Spacing = 8 };
        var actions = new StackPanel { Spacing = 8 };
        NumberBox Number(StackPanel page, string label, int value, int minimum, int maximum)
        {
            var field = new NumberBox { Header = label, Value = value, Minimum = minimum, Maximum = maximum,
                SmallChange = 1, LargeChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            };
            page.Children.Add(field);
            return field;
        }
        var radius = Number(appearance, "窗口圆角", original.BackplateRadius, 12, 40);
        var transparency = Number(appearance, "背板透明度（%）", original.BackplateTransparency, 0, 80);
        var blur = Number(appearance, "背板模糊（%）", original.BackplateBlur, 0, 100);
        var iconSize = Number(appearance, "网格图标大小", original.IconSize, 32, 96);
        var horizontal = Number(appearance, "网格横向间距", original.HorizontalGap, 4, 32);
        var vertical = Number(appearance, "网格纵向间距", original.VerticalGap, 4, 32);
        var normal = Number(notices, "普通提示显示时间（秒）", original.NoticeSeconds, 1, 30);
        var warning = Number(notices, "失败提示显示时间（秒）", original.ErrorSeconds, 1, 30);
        var placement = new ComboBox { Header = "拖拽预览位置", SelectedIndex = (int)original.DragPreview,
            ItemsSource = new[] { "跟随抓取点", "指针下方", "指针右下方" } };
        placement.SelectedIndex = (int)original.DragPreview;
        drag.Children.Add(placement);
        var distance = Number(drag, "拖拽预览间距", original.DragDistance, 0, DragPreviewPlacement.MaximumDistance);
        var accent = new TextBox { Header = "按钮及选择颜色", Text = original.ButtonColor, MaxLength = 9, PlaceholderText = "#AARRGGBB" };
        var checkedColor = new TextBox { Header = "置顶等选中按钮颜色", Text = original.SelectedButtonColor, MaxLength = 9, PlaceholderText = "#RRGGBB" };
        var outline = new CheckBox { Content = "显示交互描边", IsChecked = original.ShowInteractionBorders };
        var hint = PanelTheme.Hint("");
        appearance.Children.Add(accent);
        appearance.Children.Add(checkedColor);
        appearance.Children.Add(outline);
        var remember = new CheckBox { Content = "启动时恢复窗口位置", IsChecked = preferences.RememberPosition };
        var pin = new CheckBox { Content = "置顶窗口", IsChecked = preferences.Pinned };
        window.Children.Add(remember);
        window.Children.Add(pin);
        var editMenus = new Button { Content = "编辑文件与文件夹右键菜单…" };
        actions.Children.Add(editMenus);
        actions.Children.Add(PanelTheme.Title("快捷键"));
        foreach (string shortcut in new[] { "Ctrl+L · 编辑目录路径", "Ctrl+F · 搜索文件", "空格 · 快速预览", "F2 · 重命名",
            "Ctrl+C / X / V · 复制、剪切、粘贴", "Ctrl+Z · 撤销", "Delete · 移到系统回收站" })
            actions.Children.Add(PanelTheme.Hint(shortcut));
        var navigation = new NavigationView {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsSettingsVisible = false, IsPaneToggleButtonVisible = false,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left, OpenPaneLength = 106, IsPaneOpen = true, Height = 330,
        };
        foreach (var (name, page) in new[] { ("外观", appearance), ("拖放", drag), ("提示", notices), ("窗口", window), ("操作", actions) })
            navigation.MenuItems.Add(new NavigationViewItem { Content = name,
                Tag = new ScrollViewer { Content = page, Padding = new(8, 0, 8, 0), HorizontalScrollMode = ScrollMode.Disabled } });
        navigation.SelectionChanged += (_, args) => {
            if (args.SelectedItem is NavigationViewItem { Tag: ScrollViewer page }) navigation.Content = page;
        };
        navigation.SelectedItem = navigation.MenuItems[0];
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "AIFile 设置",
            Content = new StackPanel { Spacing = 6, Children = { navigation, hint } },
            PrimaryButtonText = "保存", CloseButtonText = "取消",
        };
        bool openMenuSettings = false;
        editMenus.Click += (_, _) => { openMenuSettings = true; dialog.Hide(); };
        HaloAppearance Draft() => new((int)radius.Value, (int)transparency.Value, (int)blur.Value,
            accent.Text.ToUpperInvariant(), checkedColor.Text.ToUpperInvariant(), outline.IsChecked == true,
            (int)normal.Value, (int)warning.Value, (DragPreviewMode)placement.SelectedIndex, (int)distance.Value,
            (int)iconSize.Value, (int)horizontal.Value, (int)vertical.Value);
        void Preview()
        {
            var draft = Draft();
            dialog.IsPrimaryButtonEnabled = draft.IsValid;
            hint.Text = draft.IsValid ? "" : "请输入范围内的数字及 #RRGGBB 或 #AARRGGBB 颜色。";
            if (draft.IsValid) ApplyAppearance(draft);
        }
        foreach (var field in new[] { radius, transparency, blur, normal, warning, distance, iconSize, horizontal, vertical })
            field.ValueChanged += (_, _) => Preview();
        placement.SelectionChanged += (_, _) => Preview();
        accent.TextChanged += (_, _) => Preview();
        checkedColor.TextChanged += (_, _) => Preview();
        outline.Checked += (_, _) => Preview();
        outline.Unchecked += (_, _) => Preview();
        HaloAppearance saved = original;
        try {
            if (await ShowStationDialog(dialog) == ContentDialogResult.Primary && Draft() is { IsValid: true } draft) {
                preferences = preferences with { Appearance = draft, RememberPosition = remember.IsChecked == true, Pinned = pin.IsChecked == true };
                presenter.IsAlwaysOnTop = preferences.Pinned;
                if (CaptionButtons.Children.OfType<ToggleButton>().FirstOrDefault() is { } toggle) {
                    toggle.IsChecked = preferences.Pinned;
                    PanelTheme.Label(toggle, preferences.Pinned ? "取消置顶" : "置顶");
                }
                if (!temporary) preferences.Save();
                saved = draft;
            }
        }
        finally { ApplyAppearance(saved); RefreshManualGrid(); }
        if (openMenuSettings) await CustomizeItemMenu();
    }

    void ApplyAppearance(HaloAppearance appearance)
    {
        PanelTheme.UseAppearance(appearance);
        RefreshBackplate();
        ConfigureGridPanel();
        if (ActiveFiles.ItemsPanelRoot is { } panel) {
            foreach (var item in panel.Children.OfType<SelectorItem>()) {
                ApplyGridMetrics(item);
                ApplySelectionColors(item);
            }
        }
    }
}
