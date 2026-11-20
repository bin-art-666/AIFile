using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIFile;

public sealed partial class MainWindow
{
    internal async Task VerifyAsync(string reportDirectory)
    {
        var checks = new List<string>();
        string? failure = null;
        try {
            if (Root.XamlRoot == null) {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Root.Loaded += (_, _) => loaded.TrySetResult();
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(8));
            }

            reportDirectory = FileLocations.Normalize(reportDirectory);
            string fixture = Directory.CreateDirectory(Path.Combine(reportDirectory, "fixture-" + Guid.NewGuid().ToString("N"))).FullName;
            string source = Directory.CreateDirectory(Path.Combine(fixture, "source")).FullName;
            string target = Directory.CreateDirectory(Path.Combine(fixture, "target")).FullName;
            string sample = Path.Combine(source, "source.txt");
            File.WriteAllText(sample, "AIFile verification");

            await Navigate(source);
            Check(available && entries is [{ Name: "source.txt" }], "真实目录导航");
            await WaitFor(() => backplate.HasTintSurface && glass.IsNoBaseActive);
            Check(CaptionButtons.Children is [Button, ToggleButton, Button]
                && expand.Width == 30 && expand.CornerRadius.TopLeft == 16
                && expand.Style == PanelTheme.Style("ItemAction") && Root.CornerRadius.TopLeft == 20,
                "Halo 三键、按钮样式和整窗背板");
            Check(TitleRow.Children.Contains(NavigationButtons)
                && NavigationButtons.Children is [ToggleButton, Button, Button, Button, Button]
                && Grid.GetColumn(CaptionButtons) == 2,
                "导航与编辑路径按钮在顶栏左侧，窗口按钮在右侧");
            Check(PlacesPanel.Visibility == Visibility.Collapsed && Address.Visibility == Visibility.Collapsed
                && Breadcrumbs.ItemsSource is Crumb[] crumbs && crumbs[^1].Path == source,
                "位置栏默认收起，面包屑指向真实目录");
            SetPlacesVisible(true);
            Check(PlacesPanel.Visibility == Visibility.Visible && placesToggle.IsChecked == true, "位置栏切换");
            SetPlacesVisible(false);
            var menu = BuildFileMenu([]);
            Check(menu.Items.OfType<MenuFlyoutSubItem>().Any(item => item.Text == "新建") &&
                menu.Items.OfType<MenuFlyoutSubItem>().Any(item => item.Text == "查看") &&
                BuildFileMenu([entries[0]]).Items.OfType<MenuFlyoutSubItem>().SelectMany(item => item.Items).OfType<MenuFlyoutItem>()
                    .Any(item => item.KeyboardAcceleratorTextOverride == "Delete" && item.Text == "移到系统回收站"),
                "Halo 风格空白菜单和文件菜单");
            var customLayout = new ItemMenuLayout(
                [ItemAction.Recycle, ItemAction.Open, ItemAction.Copy, ItemAction.Separator],
                [ItemAction.Preview, ItemAction.Cut, ItemAction.Move, ItemAction.Reveal, ItemAction.CopyPath, ItemAction.Rename, ItemAction.Group]);
            Check(customLayout.IsValid(false) && BuildFileMenu([entries[0]], customLayout, preview: true).Items[2]
                is MenuFlyoutItem { Text: "移到系统回收站" }, "自定义菜单可重排一级和更多操作");
            await WaitFor(() => Cards.ContainerFromIndex(0) is GridViewItem gridCard
                && PanelTheme.Child<FileGlyph>(gridCard)?.Entry?.Path == sample);
            var card = (GridViewItem)Cards.ContainerFromIndex(0);
            Check(card.Width == 60 && card.Height == 82 && card.Margin.Left == 6
                && PanelTheme.Child<ListViewItemPresenter>(card)?.SelectedBackground is Microsoft.UI.Xaml.Media.SolidColorBrush brush
                && brush.Color == StationInteractionColors.From(PanelTheme.Appearance.ButtonColor, false,
                    card.ActualTheme == ElementTheme.Dark).Selection, "Halo 卡片尺寸、绑定与选中颜色");
            var rename = PromptName(entries[0]);
            await WaitFor(() => activeContentDialog is { } dialog
                && Descendants<TextBox>(dialog).FirstOrDefault() is { SelectionLength: 6 }
                && Descendants<Button>(dialog).Any(button => button.Name == "PrimaryButton" && button.TabIndex == 10));
            activeContentDialog!.Hide();
            await rename;
            Check(File.Exists(sample) && activeContentDialog == null, "Halo 弹窗按钮顺序、重命名选区和取消");

            var editingMenu = CustomizeItemMenu();
            await WaitFor(() => activeContentDialog is { } dialog && Descendants<ListView>(dialog).Count() >= 2);
            var menuLists = Descendants<ListView>(activeContentDialog!).Where(list => list.ItemsSource is System.Collections.ObjectModel.ObservableCollection<MenuRow>).ToArray();
            Check(menuLists is [var first, var more]
                && RowsForMenu(first).Select(row => row.Action).SequenceEqual(MenuLayout(false).Primary)
                && RowsForMenu(more).Select(row => row.Action).SequenceEqual(MenuLayout(false).More),
                "Halo 两级右键菜单编辑器加载已保存的布局");
            activeContentDialog!.Hide();
            await editingMenu;

            string shortcut = Path.Combine(fixture, "link.url");
            File.WriteAllText(shortcut, "[InternetShortcut]\r\nURL=https://example.invalid/");
            Check(ShortcutIcons.IsShortcut(shortcut) && ShortcutIcons.IsShortcut("sample.LNK")
                && !ShortcutIcons.IsShortcut(sample) && await ShortcutIcons.Load(shortcut) is { PixelWidth: 64 },
                "Halo 快捷方式图标从 Shell 生成，互联网链接使用系统类型图标");
            SetView(true);
            Check(ReferenceEquals(Rows.ItemsSource, Cards.ItemsSource) && ActiveFiles == Rows, "网格和列表共用目录");
            SetView(false);
            ActiveFiles.SelectedItem = entries[0];
            await Navigate(target);
            await GoHistory(-1);
            Check(directory == source && entries is [{ Name: "source.txt" }]
                && ActiveFiles.SelectedItem is FileEntry { Name: "source.txt" }, "目录历史回退恢复选中项");

            string renamed = Path.Combine(source, "renamed.txt");
            File.Move(sample, renamed);
            await WaitFor(() => entries is [{ Name: "renamed.txt" }]);
            Check(entries[0].Path == renamed, "外部改名自动刷新");
            Search.Text = "missing";
            await ApplySearch();
            Check(entries.Count == 0 && available, "按文件名过滤");
            Search.Text = "";
            await ApplySearch();
            Check(entries.Count == 1, "清除搜索恢复目录");

            await ShellAction(FileAction.NewFolder, [], target, "new folder");
            Check(Directory.Exists(Path.Combine(target, "new folder")), "Shell 新建文件夹");
            await ShellAction(FileAction.Copy, [renamed], target);
            string copy = Path.Combine(target, "renamed.txt");
            Check(File.Exists(renamed) && File.ReadAllText(copy) == "AIFile verification", "Shell 复制实际文件");
            string renamedCopy = $"AIFile-verification-{Guid.NewGuid():N}.txt";
            await ShellAction(FileAction.Rename, [copy], name: renamedCopy);
            Check(!File.Exists(copy) && File.Exists(Path.Combine(target, renamedCopy)), "Shell 重命名实际文件");
            await ShellAction(FileAction.Move, [Path.Combine(target, renamedCopy)], source);
            string moved = Path.Combine(source, renamedCopy);
            Check(File.Exists(moved) && !File.Exists(Path.Combine(target, renamedCopy)), "Shell 移动实际文件");
            try {
                await ShellAction(FileAction.Recycle, [moved]);
                Check(!File.Exists(moved), "Shell 将测试文件移到回收站");
            }
            catch (RecycleUnavailableException) {
                Check(File.Exists(moved), "回收站不可访问时保留原文件");
            }

            await caption.Verify(true);
            Check(true, "原生标题拖动区域避开窗口按钮");
            ToggleMaximize();
            await WaitFor(() => presenter.State == OverlappedPresenterState.Maximized);
            Check(Root.CornerRadius.TopLeft == 0, "最大化时背板贴合工作区");
            ToggleMaximize();
            await WaitFor(() => presenter.State == OverlappedPresenterState.Restored);
            Check(AppWindow.IsShownInSwitchers, "窗口最大化、还原并参与任务切换");

            string undoSource = Path.Combine(source, "undo.txt");
            string undoDestination = Path.Combine(target, "undo.txt");
            File.WriteAllText(undoSource, "move");
            await Perform(FileAction.Move, [undoSource], target);
            Check(File.Exists(undoDestination) && lastMoves.Length == 1, "记录可恢复的同盘移动");
            File.WriteAllText(undoSource, "new occupant");
            await UndoLastMove();
            Check(File.ReadAllText(undoSource) == "new occupant" && File.Exists(undoDestination), "撤销拒绝覆盖后来创建的文件");
            File.Delete(undoSource);
            await UndoLastMove();
            Check(File.Exists(undoSource) && !File.Exists(undoDestination), "核对项目和原目录身份后撤销移动");

            string grouping = Directory.CreateDirectory(Path.Combine(fixture, "grouping")).FullName;
            File.WriteAllText(Path.Combine(grouping, "a.txt"), "a");
            File.WriteAllText(Path.Combine(grouping, "b.txt"), "b");
            await Navigate(grouping);
            SelectFile(entries[0], control: false, shift: false, null);
            SelectFile(entries[1], control: true, shift: false, null);
            Check(ActiveFiles.SelectedItems.Count == 2, "点击选择和 Ctrl 多选使用稳定文件项");
            await GroupIntoNewFolder(grouping, "combined", entries.ToArray());
            string combined = Path.Combine(grouping, "combined");
            Check(Directory.GetFiles(combined).Length == 2 && lastOperation?.Completed == 2, "合并到新文件夹并报告批量逐项结果");
            ScheduleDragNavigation(combined);
            await EnterHoveredDirectory();
            Check(directory == combined && Directory.GetFiles(combined).Length == 2, "拖拽悬停只切换目录");
            await Navigate(grouping);
            await UndoLastMove();
            Check(!Directory.Exists(combined) && Directory.GetFiles(grouping).Length == 2, "整组撤销合并并清理空目录");
            await RefreshLocation();
            await GroupIntoNewFolder(grouping, "combined", entries.ToArray());
            await MoveOutAndRemoveEmptyFolder(combined, grouping);
            Check(!Directory.Exists(combined) && Directory.GetFiles(grouping).Length == 2, "解散文件夹并保留全部文件");
            await UndoLastMove();
            Check(Directory.GetFiles(combined).Length == 2 && Directory.GetFiles(grouping).Length == 0,
                "整组撤销解散并重建目录");
            await MoveOutAndRemoveEmptyFolder(combined, grouping);
            Directory.CreateDirectory(combined);
            File.WriteAllText(Path.Combine(combined, "external.txt"), "preserve");
            await UndoLastMove().ContinueWith(task => {
                if (task.Exception?.GetBaseException() is not IOException) throw new InvalidOperationException("未拒绝占用原目录的撤销");
            });
            Check(File.Exists(Path.Combine(combined, "external.txt")) && Directory.GetFiles(grouping).Length == 2,
                "撤销解散拒绝覆盖后来创建的文件夹");

            await RefreshLocation();
            await ToggleManualGrid();
            var layout = manualGrid ?? throw new InvalidOperationException("未启用手动网格");
            Check(Cards.ItemsSource is FileEntry[] slots && slots.Any(item => item.EmptySlot >= 0), "手动网格保留可见空位");
            var a = layout.Cells["a.txt"];
            var b = layout.Cells["b.txt"];
            var exchanged = layout.Drop("a.txt", ["a.txt"], b);
            Check(exchanged?.Cells["b.txt"] == a && exchanged.Cells["a.txt"] == b, "单项落在占用格时交换");
            Check(layout.Drop("a.txt", ["a.txt", "b.txt"], new(0, layout.Columns - 1)) == null, "多选越界保持原布局");
            var sparse = layout.Drop("a.txt", ["a.txt"], new(2, 0))!;
            PersistGrid(sparse);
            RefreshManualGrid();
            Check(((FileEntry[])Cards.ItemsSource)[2 * sparse.Columns].Name == "a.txt"
                && File.ReadAllText(Path.Combine(grouping, "a.txt")) == "a", "调格只改变显示位置");
            var expandedLayout = sparse.Reconcile(entries, sparse.Columns + 1);
            var narrowed = expandedLayout.Reconcile(entries, 1);
            Check(narrowed.Columns == expandedLayout.Columns && narrowed.Cells["a.txt"] == new GridCell(2, 0), "窗口变宽增加列，变窄保留坐标");
            SortBy(FileSort.Modified);
            Check(ReferenceEquals(Cards.ItemsSource, entries), "自动排序暂时切出手动网格");
            await ToggleManualGrid();
            Check(manualGrid?.Cells["a.txt"] == new GridCell(2, 0), "切回手动恢复原坐标");
            await PackManualGrid();
            Check(manualGrid!.Cells["a.txt"].Row == 0, "整理空位收拢卡片");
            await ToggleManualGrid();

            string nested = Directory.CreateDirectory(Path.Combine(target, "nested")).FullName;
            string nestedFile = Path.Combine(nested, "needle.txt");
            File.WriteAllText(nestedFile, "search verification");
            await Navigate(fixture);
            SearchSubfolders.IsChecked = true;
            await ShowSearch();
            Search.Text = "needle";
            await ApplySearch();
            await WaitFor(() => SearchResults.Items.Count == 1);
            var hit = ((ListViewItem)SearchResults.Items[0]).Tag as DirectorySearchHit;
            Check(hit?.Entry.Path == nestedFile && hit.RelativeDirectory == Path.Combine("target", "nested"), "多级搜索返回相对路径");
            await LocateSearchResult(hit!.Entry);
            Check(directory == nested && ActiveFiles.SelectedItem is FileEntry { Path: var selected } && selected == nestedFile,
                "从搜索结果定位并选中同一个文件");
            SearchSubfolders.IsChecked = false;
            await VerifyImageSearch(nested, Check);
            if (preferences.Sort == FileSort.Modified) SortBy(FileSort.Name);
            SortBy(FileSort.Modified);
            Check(preferences.Sort == FileSort.Modified && preferences.Descending, "列表表头应用排序方向");
            SortBy(FileSort.Name);

            string large = Directory.CreateDirectory(Path.Combine(fixture, "large")).FullName;
            await Task.Run(() => {
                for (int index = 0; index < 10005; index++)
                    using (File.Create(Path.Combine(large, $"file-{index}.txt"))) { }
            });
            await Navigate(large);
            Check(available && entries.Count == 10005 && entries[2].Name == "file-2.txt"
                && Cards.ItemsPanelRoot is ItemsWrapGrid && Cards.ContainerFromIndex(10004) == null,
                "上万项分批加载、自然排序及虚拟化");
        }
        catch (Exception error) { failure = error.ToString(); }
        finally {
            File.WriteAllText(Path.Combine(reportDirectory, "result.json"),
                JsonSerializer.Serialize(new { Passed = failure == null, Checks = checks, Failure = failure },
                    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Close();
        }

        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label);
        }

        async Task ShellAction(FileAction action, string[] paths, string? destination = null, string? name = null)
        {
            var result = await ShellFileOperations.Run(handle, action, paths, destination, name)
                .WaitAsync(TimeSpan.FromSeconds(15));
            if (result.Incomplete || result.Errors.Count > 0 || result.Completed != Math.Max(1, paths.Length))
                throw new IOException($"{action}: {result.Message} {string.Join(", ", result.Items.Select(item => item.Path + " " + item.Outcome))}");
        }

        static async Task WaitFor(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition()) await Task.Delay(40, timeout.Token);
        }
    }

    async Task VerifyImageSearch(string folder, Action<bool, string> check)
    {
        var engines = ImageTextSearch.Engines();
        if (engines.Length == 0) return;
        bool chinese = engines.Any(engine => engine.RecognizerLanguage.LanguageTag.StartsWith("zh-", StringComparison.OrdinalIgnoreCase));
        string phrase = chinese ? "连接失败" : "HALO SCREENSHOT";
        string path = Path.Combine(folder, "sample.png");
        using (var bitmap = new Microsoft.Graphics.Canvas.CanvasRenderTarget(
            Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(), 1000, 240, 96)) {
            using (var drawing = bitmap.CreateDrawingSession()) {
                drawing.Clear(Microsoft.UI.Colors.White);
                using var format = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat {
                    FontFamily = chinese ? "Microsoft YaHei UI" : "Segoe UI", FontSize = 80,
                };
                drawing.DrawText(phrase, 35, 55, Microsoft.UI.Colors.Black, format);
            }
            await using var stream = File.Create(path);
            using var output = stream.AsRandomAccessStream();
            await bitmap.SaveAsync(output, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
        }
        var hits = new List<DirectorySearchHit>();
        var unrecognized = await Task.Run(() => directorySearch.Find(folder, phrase, false, false, hits.Add, default));
        check(unrecognized.Count == 0, "图片文字识别默认关闭");
        var recognized = await Task.Run(() => directorySearch.Find(folder, phrase, false, true, hits.Add, default));
        check(recognized.Count == 1 && hits[0].Entry.Path == path && hits[0].ImageMatch != null, "从真实 PNG 识别并搜索图片文字");
    }
}
