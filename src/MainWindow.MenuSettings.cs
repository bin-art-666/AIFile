using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using Windows.Foundation;
using Windows.System;

namespace AIFile;

internal enum ItemAction { Separator, Open, Preview, Copy, Cut, Move, Reveal, CopyPath, Rename, Color, Recycle, Group, Dissolve }

internal sealed record ItemMenuLayout(ItemAction[] Primary, ItemAction[] More)
{
    internal static ItemMenuLayout Default(bool folder) => new(
        [ItemAction.Open, ItemAction.Preview, ItemAction.Copy, ItemAction.Cut, ItemAction.Separator, ItemAction.Rename],
        folder ? [ItemAction.Reveal, ItemAction.CopyPath, ItemAction.Color, ItemAction.Move, ItemAction.Group, ItemAction.Dissolve,
                ItemAction.Separator, ItemAction.Recycle]
            : [ItemAction.Reveal, ItemAction.CopyPath, ItemAction.Move, ItemAction.Group, ItemAction.Separator, ItemAction.Recycle]);

    internal bool IsValid(bool folder)
    {
        if (Primary == null || More == null || Primary.Length > 20 || More.Length > 20) return false;
        var commands = Primary.Concat(More).Where(action => action != ItemAction.Separator).Order();
        return commands.SequenceEqual(Enum.GetValues<ItemAction>()
            .Where(action => action != ItemAction.Separator && (folder || action is not (ItemAction.Color or ItemAction.Dissolve))));
    }
}

public sealed partial class MainWindow
{
    ItemMenuLayout MenuLayout(bool folder)
    {
        var layout = folder ? preferences.FolderMenu : preferences.FileMenu;
        return layout?.IsValid(folder) == true ? layout : ItemMenuLayout.Default(folder);
    }

    static string MenuLabel(ItemAction action, bool folder) => action switch {
        ItemAction.Open => folder ? "打开文件夹" : "打开文件",
        ItemAction.Preview => "快速预览", ItemAction.Copy => folder ? "复制文件夹" : "复制文件",
        ItemAction.Cut => "剪切", ItemAction.Move => "移动到…",
        ItemAction.Reveal => folder ? "在资源管理器中打开" : "在文件夹中显示",
        ItemAction.CopyPath => "复制路径", ItemAction.Rename => "重命名…",
        ItemAction.Color => "设置文件夹颜色…", ItemAction.Recycle => "移到系统回收站",
        ItemAction.Group => "放入新文件夹…", ItemAction.Dissolve => "解散文件夹…",
        _ => "分隔线",
    };

    sealed class MenuRow(ItemAction action, string label)
    {
        internal ItemAction Action { get; } = action;
        public string Label { get; } = label;
    }

    static ObservableCollection<MenuRow> RowsForMenu(ListView list) => (ObservableCollection<MenuRow>)list.ItemsSource;

    static Border BuildMenuLayer(ListView list, string title, string menu)
    {
        AutomationProperties.SetName(list, $"{menu} {title}");
        list.SelectionMode = ListViewSelectionMode.Single;
        list.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        list.ItemsSource = new ObservableCollection<MenuRow>();
        list.DisplayMemberPath = nameof(MenuRow.Label);
        list.Height = 204;
        var itemStyle = new Style(typeof(ListViewItem));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34d));
        list.ItemContainerStyle = itemStyle;
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Disabled);
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(PanelTheme.Hint(title));
        var empty = new TextBlock {
            Text = "暂无操作", Opacity = 0.72, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        var surface = new Grid();
        surface.Children.Add(list);
        surface.Children.Add(empty);
        RowsForMenu(list).CollectionChanged += (_, _) => empty.Visibility = RowsForMenu(list).Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        content.Children.Add(surface);
        return new Border { Child = content, Style = PanelTheme.Style("Card") };
    }

    void BuildMenuEditor(StackPanel body, ListView primary, ListView more, ItemAction separatorMarker, Action edited, string menu)
    {
        body.Children.Add(AdaptivePair(BuildMenuLayer(primary, "一级菜单", menu), BuildMenuLayer(more, "更多菜单（…）", menu), 410));
        ListView active = primary;

        Button Tool(string label, Action use) {
            var button = new Button { Content = label };
            AutomationProperties.SetName(button, $"{menu} {label}");
            button.Click += (_, _) => use();
            PanelTheme.StyleTextAction(button);
            return button;
        }
        var up = Tool("上移", () => MoveMenuRow(active, -1, edited));
        var down = Tool("下移", () => MoveMenuRow(active, 1, edited));
        var transfer = Tool("移到「…」", () => {
            var list = active;
            var other = list == primary ? more : primary;
            if (list.SelectedItem is not MenuRow row || row.Action == separatorMarker || RowsForMenu(other).Count >= 20) return;
            RowsForMenu(list).RemoveAt(list.SelectedIndex);
            RowsForMenu(other).Add(row);
            SelectMenuRow(other, row);
            edited();
        });
        var separator = Tool("添加分隔线", () => {
            var list = active;
            int index = list.SelectedIndex;
            if (list.SelectedItem is not MenuRow row) return;
            if (row.Action == separatorMarker) {
                RowsForMenu(list).RemoveAt(index);
                list.SelectedIndex = Math.Min(index, RowsForMenu(list).Count - 1);
            } else {
                var line = new MenuRow(separatorMarker, "── 分隔线 ──");
                RowsForMenu(list).Insert(index + 1, line);
                SelectMenuRow(list, line);
            }
            edited();
        });
        body.Children.Add(AdaptivePair(ActionRow(up, down), ActionRow(transfer, separator), 362, equalColumns: false));
        BindSelection(primary, more);
        BindSelection(more, primary);
        Refresh();

        void BindSelection(ListView list, ListView other)
        {
            list.SelectionChanged += (_, _) => {
                if (list.SelectedIndex >= 0) {
                    active = list;
                    if (other.SelectedIndex >= 0) other.SelectedIndex = -1;
                }
                Refresh();
            };
            BindMenuMouseDrag(list, edited);
        }

        void Refresh()
        {
            int index = active.SelectedIndex;
            bool selected = index >= 0;
            bool isSeparator = active.SelectedItem is MenuRow row && row.Action == separatorMarker;
            up.IsEnabled = index > 0;
            down.IsEnabled = selected && index < active.Items.Count - 1;
            transfer.IsEnabled = selected && !isSeparator && RowsForMenu(active == primary ? more : primary).Count < 20;
            separator.IsEnabled = selected && (isSeparator || RowsForMenu(active).Count < 20 && RowsForMenu(active).Count(item => item.Action == separatorMarker) < 9);
            UpdateLabel(transfer, active == more ? "移到一级菜单" : "移到「…」");
            UpdateLabel(separator, isSeparator ? "删除分隔线" : "添加分隔线");
        }

        void UpdateLabel(Button button, string label)
        {
            if (Equals(button.Content, label)) return;
            button.Content = label;
            AutomationProperties.SetName(button, $"{menu} {label}");
        }
    }

    static StackPanel ActionRow(params UIElement[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    static Grid AdaptivePair(FrameworkElement first, FrameworkElement second, double horizontalWidth, bool equalColumns = true)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        var firstColumn = new ColumnDefinition();
        grid.ColumnDefinitions.Add(firstColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(first);
        grid.Children.Add(second);
        Arrange(false);
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width >= horizontalWidth);
        return grid;

        void Arrange(bool horizontal)
        {
            grid.RowSpacing = horizontal ? 0 : 8;
            firstColumn.Width = horizontal && !equalColumns ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            Grid.SetColumnSpan(first, horizontal ? 1 : 2);
            Grid.SetColumnSpan(second, horizontal ? 1 : 2);
            Grid.SetColumn(second, horizontal ? 1 : 0);
            Grid.SetRow(second, horizontal ? 0 : 1);
        }
    }

    static void SelectMenuRow(ListView list, MenuRow row)
    {
        list.SelectedItem = row;
        if (list.IsLoaded) list.ScrollIntoView(row);
    }

    static void MoveMenuRow(ListView list, int offset, Action edited)
    {
        int index = list.SelectedIndex, destination = index + offset;
        if (index < 0 || destination < 0 || destination >= list.Items.Count) return;
        var row = RowsForMenu(list)[index];
        RowsForMenu(list).Move(index, destination);
        SelectMenuRow(list, row);
        edited();
    }

    // A menu edit stays inside its popup, so reuse the file cards' pointer capture.
    static void BindMenuMouseDrag(ListView list, Action edited)
    {
        (Pointer Pointer, ListViewItem Container, MenuRow Row, Point Start)? press = null;
        list.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) => {
            var point = args.GetCurrentPoint(list);
            if (press != null || point.PointerDeviceType != PointerDeviceType.Mouse
                || point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
                || MenuRowAt(list, point.Position) is not { Content: MenuRow row } container
                || !container.CapturePointer(args.Pointer)) return;
            press = (args.Pointer, container, row, point.Position);
            list.SelectedItem = row;
            container.Focus(FocusState.Pointer);
            args.Handled = true;
        }), true);
        list.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) => {
            if (FinishDrag(args)) args.Handled = true;
        }), true);
        list.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, args) => {
            if (press?.Pointer.PointerId == args.Pointer.PointerId && !FinishDrag(args)) press = null;
        }), true);
        list.PointerCanceled += (_, _) => StopDrag();
        list.PreviewKeyDown += (_, args) => {
            if (args.Key != VirtualKey.Escape || press == null) return;
            StopDrag();
            args.Handled = true;
        };

        void StopDrag()
        {
            if (press is not { } start) return;
            press = null;
            start.Container.ReleasePointerCapture(start.Pointer);
        }

        bool FinishDrag(PointerRoutedEventArgs args)
        {
            if (press is not { } start || start.Pointer.PointerId != args.Pointer.PointerId) return false;
            var point = args.GetCurrentPoint(list);
            if (point.Properties.IsLeftButtonPressed) return false;
            StopDrag();
            if (Math.Abs(point.Position.Y - start.Start.Y) < 4
                || MenuRowAt(list, point.Position)?.Content is not MenuRow row || row == start.Row) return true;
            var rows = RowsForMenu(list);
            int from = rows.IndexOf(start.Row), to = rows.IndexOf(row);
            if (from < 0 || to < 0) return true;
            list.SelectedItem = start.Row;
            MoveMenuRow(list, to - from, edited);
            return true;
        }
    }

    static ListViewItem? MenuRowAt(ListView list, Point point)
    {
        if (!new Rect(0, 0, list.ActualWidth, list.ActualHeight).Contains(point)) return null;
        foreach (var row in RowsForMenu(list)) {
            if (list.ContainerFromItem(row) is not ListViewItem item) continue;
            var bounds = item.TransformToVisual(list).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            if (bounds.Contains(point)) return item;
        }
        return null;
    }

    async Task CustomizeItemMenu()
    {
        var drafts = new[] { MenuLayout(false), MenuLayout(true) };
        var target = new ComboBox { Header = "菜单类型", ItemsSource = new[] { "文件", "文件夹" }, SelectedIndex = 0 };
        var primary = new ListView();
        var more = new ListView();
        var preview = new Button { Content = "预览菜单" };
        var restore = new Button { Content = "恢复默认菜单" };
        PanelTheme.StyleTextAction(preview);
        PanelTheme.StyleTextAction(restore);
        var body = new StackPanel { Spacing = 8, Children = { target } };
        var dialog = new ContentDialog {
            Title = "自定义右键菜单", PrimaryButtonText = "保存", CloseButtonText = "取消",
            Content = new ScrollViewer { Content = body, MaxHeight = 530, HorizontalScrollMode = ScrollMode.Disabled },
        };
        dialog.Resources["ContentDialogMinWidth"] = Math.Clamp(Root.ActualWidth - 48, 320, 520);

        int current = 0;
        MenuFlyout? previewMenu = null;
        BuildMenuEditor(body, primary, more, ItemAction.Separator, Preview, "右键菜单");
        body.Children.Add(ActionRow(preview, restore));

        ItemMenuLayout Draft() => new(RowsForMenu(primary).Select(row => row.Action).ToArray(),
            RowsForMenu(more).Select(row => row.Action).ToArray());

        void Preview()
        {
            bool folder = current == 1;
            var draft = Draft();
            previewMenu = BuildFileMenu([new(folder ? "示例文件夹" : "示例.txt", "", folder, default, 0)], draft, preview: true);
            preview.Flyout = previewMenu;
            dialog.IsPrimaryButtonEnabled = draft.IsValid(folder);
        }

        void Load(ItemMenuLayout layout)
        {
            LoadMenuLayer(primary, layout.Primary);
            LoadMenuLayer(more, layout.More);
            Preview();
        }

        void LoadMenuLayer(ListView list, ItemAction[] actions)
        {
            var rows = RowsForMenu(list);
            rows.Clear();
            foreach (var action in actions)
                rows.Add(new(action, action == ItemAction.Separator ? "── 分隔线 ──" : MenuLabel(action, current == 1)));
        }

        target.SelectionChanged += (_, _) => {
            if (target.SelectedIndex < 0) return;
            drafts[current] = Draft();
            current = target.SelectedIndex;
            Load(drafts[current]);
        };
        restore.Click += (_, _) => Load(ItemMenuLayout.Default(current == 1));
        Load(drafts[0]);
        if (await ShowStationDialog(dialog, childFlyoutOpen: () => previewMenu?.IsOpen == true) != ContentDialogResult.Primary) return;
        drafts[current] = Draft();
        if (!drafts[0].IsValid(false) || !drafts[1].IsValid(true)) return;
        preferences = preferences with { FileMenu = drafts[0], FolderMenu = drafts[1] };
        if (!temporary) preferences.Save();
    }
}
