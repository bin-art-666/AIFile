using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace AIFile;

public sealed partial class MainWindow : Window
{
    readonly ObservableCollection<FileEntry> entries = [];
    readonly List<string> history = [];
    readonly DispatcherQueueTimer refreshTimer, searchTimer, noticeTimer;
    readonly OverlappedPresenter presenter;
    readonly PanelCaption caption;
    readonly GridMarqueeSelection marquee;
    readonly SelectionNameOverlay selectionName;
    readonly nint handle;
    readonly bool temporary;
    WindowPreferences preferences;
    readonly Button back, forward, up, expand;
    readonly ToggleButton placesToggle = new();
    readonly BackplateSurface backplate = new() { IsHitTestVisible = false };
    readonly GlassBackdrop glass = PanelTheme.DesktopBackdrop();
    CancellationTokenSource? readRequest;
    FileSystemWatcher? watcher;
    string? directory;
    string query = "";
    int historyIndex = -1;
    bool loading, available, busy, listView, closed, preserveSelection;
    bool lastMaximized, dimensionsReady;
    ListViewBase ActiveFiles => listView ? Rows : Cards;

    internal MainWindow(bool temporary = false, string? initialPath = null)
    {
        InitializeComponent();
        this.temporary = temporary;
        preferences = temporary ? new() : WindowPreferences.Read();
        if (preferences.Appearance is { IsValid: true } appearance) PanelTheme.UseAppearance(appearance);
        if (!Enum.IsDefined(preferences.Sort)) preferences = preferences with { Sort = FileSort.Name, Descending = false };
        UpdateSortHeaders();
        handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "AIFile";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AIFile.ico"));
        AppWindow.IsShownInSwitchers = true;
        presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = true;
        presenter.PreferredMinimumWidth = 580;
        presenter.PreferredMinimumHeight = 380;
        Root.Children.Insert(0, backplate);
        Grid.SetRowSpan(backplate, Root.RowDefinitions.Count);
        SystemBackdrop = glass;
        Root.Loaded += (_, _) => RefreshBackplate();
        Root.ActualThemeChanged += (_, _) => RefreshBackplate();
        Root.SizeChanged += (_, _) => PanelTheme.ClipDesktopBackdrop(Root);
        caption = new(this, TitleRow, CaptionButtons, () => !closed, leading: NavigationButtons, nativeResizeRegions: false);
        marquee = new(FilesHost, Cards, Notice,
            () => available && !busy && !listView && SearchPane.Visibility != Visibility.Visible,
            () => Cards.SelectedItems.Clear(), UpdateCommands);
        selectionName = new(FilesHost, Cards, Notice, () => closed || loading || busy || listView
            || marquee.IsActive || ownDragPaths != null || SearchPane.Visibility == Visibility.Visible);
        InitializeCardSelection();
        InitializeManualGrid();
        InitializeDragGrouping();

        expand = AddButton(CaptionButtons, "\uE740", "最大化", ToggleMaximize);
        var pin = new ToggleButton { IsChecked = preferences.Pinned };
        presenter.IsAlwaysOnTop = preferences.Pinned;
        PanelTheme.FramedIconStyle(pin, "置顶");
        WindowControls.SetIcon(pin, "\uE718");
        pin.Click += (_, _) => {
            presenter.IsAlwaysOnTop = pin.IsChecked == true;
            preferences = preferences with { Pinned = presenter.IsAlwaysOnTop };
            PanelTheme.Label(pin, presenter.IsAlwaysOnTop ? "取消置顶" : "置顶");
        };
        CaptionButtons.Children.Add(pin);
        AddButton(CaptionButtons, "\uE8BB", "关闭", () => { if (!busy) Close(); });
        AppWindow.Changed += (_, _) => WindowChanged();
        AppWindow.Closing += (_, args) => { if (busy) args.Cancel = true; };

        PanelTheme.FramedIconStyle(placesToggle, "显示位置栏");
        WindowControls.SetIcon(placesToggle, "\uE8B7");
        placesToggle.Click += (_, _) => SetPlacesVisible(placesToggle.IsChecked == true);
        NavigationButtons.Children.Add(placesToggle);
        back = AddAsyncButton(NavigationButtons, "\uE72B", "后退（Alt+左）", () => GoHistory(-1));
        forward = AddAsyncButton(NavigationButtons, "\uE72A", "前进（Alt+右）", () => GoHistory(1));
        up = AddAsyncButton(NavigationButtons, "\uE74A", "上一级（Alt+上）", GoUp);
        AddButton(NavigationButtons, "\uE70F", "编辑路径（Ctrl+L）", ShowAddress);
        InitializeDragNavigation();

        refreshTimer = DispatcherQueue.CreateTimer();
        refreshTimer.Interval = TimeSpan.FromMilliseconds(260);
        refreshTimer.IsRepeating = false;
        refreshTimer.Tick += async (_, _) => {
            if (!loading && !busy) await RunUi(RefreshLocation);
            else if (!closed) refreshTimer.Start();
        };
        searchTimer = DispatcherQueue.CreateTimer();
        searchTimer.Interval = TimeSpan.FromMilliseconds(240);
        searchTimer.IsRepeating = false;
        searchTimer.Tick += async (_, _) => await RunUi(ApplySearch);
        noticeTimer = DispatcherQueue.CreateTimer();
        noticeTimer.Interval = TimeSpan.FromSeconds(4);
        noticeTimer.IsRepeating = false;
        noticeTimer.Tick += (_, _) => Notice.Visibility = Visibility.Collapsed;

        Cards.ItemsSource = Rows.ItemsSource = entries;
        SetView(preferences.List);
        InstallShortcuts();
        Places.ItemsSource = FileLocations.Places();
        Root.Loaded += FirstLoad;
        Activated += (_, args) => {
            if (!closed && args.WindowActivationState != WindowActivationState.Deactivated && watcher == null && directory != null)
                ScheduleRefresh();
        };
        Closed += (_, _) => {
            closed = true;
            refreshTimer.Stop();
            searchTimer.Stop();
            noticeTimer.Stop();
            readRequest?.Cancel();
            advancedSearch?.Cancel();
            StopWatching();
            caption.Dispose();
            CancelDragNavigation();
            ResetDragGrouping();
            ClearDragPreview();
            backplate.Dispose();
            if (!temporary) preferences.Save();
        };
        UpdateCommands();

        void FirstLoad(object sender, RoutedEventArgs args)
        {
            Root.Loaded -= FirstLoad;
            DispatcherQueue.TryEnqueue(ApplyInitialSize);
            if (temporary) return;
            string first = initialPath ?? ((IReadOnlyList<FilePlace>)Places.ItemsSource).FirstOrDefault()?.Path
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _ = RunUi(() => Navigate(first));
        }
    }

    void ApplyInitialSize()
    {
        if (closed || Root.XamlRoot is not { } xamlRoot) return;
        var display = preferences is { RememberPosition: true, X: int x, Y: int y }
            ? DisplayArea.GetFromPoint(new(x, y), DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        work.X += display.OuterBounds.X;
        work.Y += display.OuterBounds.Y;
        var scale = xamlRoot.RasterizationScale;
        int width = Math.Min(work.Width, (int)Math.Round(Math.Clamp(preferences.Width, 580, 1600) * scale));
        int height = Math.Min(work.Height, (int)Math.Round(Math.Clamp(preferences.Height, 380, 1100) * scale));
        int left = Math.Clamp((preferences.RememberPosition ? preferences.X : null) ?? work.X + (work.Width - width) / 2,
            work.X, work.X + work.Width - width);
        int top = Math.Clamp((preferences.RememberPosition ? preferences.Y : null) ?? work.Y + (work.Height - height) / 2,
            work.Y, work.Y + work.Height - height);
        AppWindow.MoveAndResize(new(left, top, width, height));
        dimensionsReady = true;
    }

    Button AddButton(Panel parent, string glyph, string name, Action action)
    {
        var button = WindowControls.IconButton(glyph, name);
        button.Click += (_, _) => action();
        parent.Children.Add(button);
        return button;
    }

    Button AddAsyncButton(Panel parent, string glyph, string name, Func<Task> action) =>
        AddButton(parent, glyph, name, () => _ = RunUi(action));

    internal async Task RunUi(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed) SetStatus(error.Message, warning: true); }
    }

    void WindowChanged()
    {
        if (expand is null) return;
        if (dimensionsReady && presenter.State == OverlappedPresenterState.Restored && Root.XamlRoot is { } xamlRoot) {
            var size = AppWindow.Size;
            var position = AppWindow.Position;
            preferences = preferences with {
                Width = (int)Math.Round(size.Width / xamlRoot.RasterizationScale),
                Height = (int)Math.Round(size.Height / xamlRoot.RasterizationScale),
                X = position.X,
                Y = position.Y,
            };
        }
        bool maximized = presenter.State == OverlappedPresenterState.Maximized;
        if (lastMaximized == maximized) return;
        lastMaximized = maximized;
        RefreshBackplate();
        WindowControls.SetIcon(expand, maximized ? "\uE73F" : "\uE740");
        PanelTheme.Label(expand, maximized ? "还原" : "最大化");
    }

    void RefreshBackplate()
    {
        if (closed || Root.XamlRoot == null) return;
        PanelTheme.ApplyDesktopBackdrop(Root, backplate, glass,
            presenter.State == OverlappedPresenterState.Maximized);
        PanelTheme.ApplyNoticeColors(Notice, Status);
        var colors = StationInteractionColors.From(PanelTheme.Appearance.ButtonColor,
            PanelTheme.Appearance.ShowInteractionBorders, Root.ActualTheme == ElementTheme.Dark);
        marquee.ApplyColors(colors);
        selectionName.ApplyColors(colors);
    }

    void ToggleMaximize()
    {
        if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
        else presenter.Maximize();
    }

    async Task Navigate(string path, bool recordHistory = true)
    {
        path = FileLocations.Normalize(path);
        if (recordHistory && historyIndex >= 0 && history[historyIndex].Equals(path, StringComparison.OrdinalIgnoreCase)) {
            Address.Text = path;
            await RefreshLocation();
            return;
        }
        if (recordHistory) {
            if (historyIndex + 1 < history.Count) history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            history.Add(path);
            historyIndex = history.Count - 1;
        }
        SaveDirectoryView();
        selectionAnchor = null;
        pendingSelection = null;
        ResetLocalGridDrag();
        marquee.Cancel();
        StopWatching();
        CancelAdvancedSearch();
        directory = path;
        manualGrid = temporary ? null : ManualGridStore.Read(path);
        Address.Text = path;
        query = "";
        Search.Text = "";
        searchTimer.Stop();
        var places = (IReadOnlyList<FilePlace>)Places.ItemsSource;
        Places.SelectedItem = places.FirstOrDefault(place => place.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        UpdateBreadcrumbs(path);
        await ShowDirectory(progressive: true);
        if (available && PathComparer.Equals(path, directory)) RestoreDirectoryView(path);
    }

    static string DirectoryName(string path) => Path.GetFileName(path) is { Length: > 0 } name ? name : path;

    Task GoUp() => directory != null && Path.GetDirectoryName(directory) is { } parent ? Navigate(parent) : Task.CompletedTask;

    Task GoHistory(int direction)
    {
        int next = historyIndex + direction;
        if (next < 0 || next >= history.Count) return Task.CompletedTask;
        historyIndex = next;
        return Navigate(history[next], recordHistory: false);
    }

    void PlaceClicked(object sender, ItemClickEventArgs args) =>
        _ = RunUi(() => Navigate(((FilePlace)args.ClickedItem).Path));

    sealed record Crumb(string Name, string Path)
    {
        public override string ToString() => Name;
    }

    void UpdateBreadcrumbs(string path)
    {
        var crumbs = new Stack<Crumb>();
        var places = (IReadOnlyList<FilePlace>)Places.ItemsSource;
        for (string? current = path; current != null; current = Path.GetDirectoryName(current)) {
            var place = places.FirstOrDefault(item => PathComparer.Equals(item.Path, current));
            crumbs.Push(new(place?.Name ?? DirectoryName(current), current));
        }
        Breadcrumbs.ItemsSource = crumbs.ToArray();
    }

    void CrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is Crumb crumb && !PathComparer.Equals(crumb.Path, directory))
            _ = RunUi(() => Navigate(crumb.Path));
    }

    void SetPlacesVisible(bool visible)
    {
        PlacesPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        placesToggle.IsChecked = visible;
        PanelTheme.Label(placesToggle, visible ? "隐藏位置栏" : "显示位置栏");
    }

    void ShowAddress()
    {
        Address.Text = directory ?? "";
        Breadcrumbs.Visibility = Visibility.Collapsed;
        Address.Visibility = Visibility.Visible;
        Address.Focus(FocusState.Keyboard);
        Address.SelectAll();
    }

    void AddressUnfocused(object sender, RoutedEventArgs args) => HideAddress();

    void HideAddress()
    {
        if (Address.Visibility == Visibility.Collapsed) return;
        Address.Visibility = Visibility.Collapsed;
        Breadcrumbs.Visibility = Visibility.Visible;
    }
    void AddressKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter) {
            args.Handled = true;
            _ = RunUi(async () => {
                string path = FileLocations.Normalize(Address.Text);
                HideAddress();
                ActiveFiles.Focus(FocusState.Keyboard);
                await Navigate(path);
            });
        } else if (args.Key == VirtualKey.Escape) {
            HideAddress();
            ActiveFiles.Focus(FocusState.Keyboard);
            args.Handled = true;
        }
    }

    void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || closed) return;
        searchTimer.Stop();
        searchTimer.Start();
    }

    void SearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => _ = RunUi(ApplySearch);

    Task ShowSearch()
    {
        SearchBar.Visibility = Visibility.Visible;
        Search.Focus(FocusState.Keyboard);
        return Task.CompletedTask;
    }

    async Task HideSearch()
    {
        if (SearchBar.Visibility == Visibility.Collapsed) return;
        Search.Text = "";
        SearchBar.Visibility = Visibility.Collapsed;
        ActiveFiles.Focus(FocusState.Keyboard);
        await ApplySearch();
    }

    Task ApplySearch()
    {
        searchTimer.Stop();
        string value = Search.Text.Trim();
        if (value.Length > 0 && (SearchSubfolders.IsChecked == true || SearchImageText.IsChecked == true))
            return RunAdvancedSearch(value);
        CancelAdvancedSearch();
        if (value == query && watcher != null) return Task.CompletedTask;
        query = value;
        return ShowDirectory(progressive: true);
    }

    async Task RefreshLocation()
    {
        Places.ItemsSource = FileLocations.Places();
        if (directory != null)
            Places.SelectedItem = ((IReadOnlyList<FilePlace>)Places.ItemsSource)
                .FirstOrDefault(place => place.Path.Equals(directory, StringComparison.OrdinalIgnoreCase));
        if (SearchPane.Visibility == Visibility.Visible) await RunAdvancedSearch(Search.Text.Trim());
        else await ShowDirectory(progressive: false);
    }

    async Task ShowDirectory(bool progressive)
    {
        if (directory is not { } path || closed) return;
        readRequest?.Cancel();
        using var request = new CancellationTokenSource();
        readRequest = request;
        loading = true;
        if (progressive) {
            available = false;
            ResetManualProjection();
            entries.Clear();
            EmptyMessage.Text = "正在读取目录…";
            EmptyMessage.Visibility = Visibility.Visible;
        }
        UpdateCommands();
        var found = new List<FileEntry>();
        try {
            // Subscribe before enumeration so changes during the initial read trigger a follow-up scan.
            StartWatching(path);
            await FileLocations.ReadBatches(path, query, async batch => {
                found.AddRange(batch);
                if (!progressive) return;
                foreach (var entry in batch) entries.Add(entry);
                EmptyMessage.Visibility = Visibility.Collapsed;
                await Task.Yield();
            }, request.Token);
            request.Token.ThrowIfCancellationRequested();
            var ordered = SortEntries(found);
            UpdateEntries(ordered, progressive);
            available = true;
            RefreshManualGrid();
            EmptyMessage.Text = query.Length > 0 ? "没有匹配的文件" : "此文件夹为空";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) {
            if (!request.IsCancellationRequested) {
                available = false;
                ResetManualProjection();
                entries.Clear();
                EmptyMessage.Text = error is UnauthorizedAccessException ? "没有权限读取此目录" : "目录不可用";
                SetStatus(error.Message, warning: true);
                StopWatching();
            }
        }
        finally {
            if (readRequest == request && !closed) {
                readRequest = null;
                loading = false;
                UpdateFileViewVisibility();
                UpdateCommands();
            }
        }
    }

    void UpdateEntries(FileEntry[] ordered, bool progressive)
    {
        if (entries.SequenceEqual(ordered)) return;
        var selected = ActiveFiles.SelectedItems.Cast<FileEntry>().Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        preserveSelection = true;
        try {
            if (!progressive && entries.Count == ordered.Length && entries.Select(file => file.Path).SequenceEqual(ordered.Select(file => file.Path))) {
                for (int index = 0; index < ordered.Length; index++)
                    if (entries[index] != ordered[index]) entries[index] = ordered[index];
            } else {
                entries.Clear();
                foreach (var entry in ordered) entries.Add(entry);
            }
            foreach (var file in ordered.Where(file => selected.Contains(file.Path)))
                if (!ActiveFiles.SelectedItems.Contains(file)) ActiveFiles.SelectedItems.Add(file);
        }
        finally { preserveSelection = false; }
    }

    void StartWatching(string path)
    {
        if (watcher != null) return;
        FileSystemWatcher? candidate = null;
        try {
            candidate = new(path) {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 32 * 1024,
                IncludeSubdirectories = SearchPane.Visibility == Visibility.Visible && SearchSubfolders.IsChecked == true,
            };
            candidate.Changed += OnDiskChanged;
            candidate.Created += OnDiskChanged;
            candidate.Deleted += OnDiskChanged;
            candidate.Renamed += OnDiskChanged;
            candidate.Error += (sender, _) => DispatcherQueue.TryEnqueue(() => {
                if (!ReferenceEquals(sender, watcher)) return;
                StopWatching();
                ScheduleRefresh();
            });
            watcher = candidate;
            candidate.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) {
            candidate?.Dispose();
            watcher = null;
        }
    }

    void OnDiskChanged(object sender, FileSystemEventArgs args) => DispatcherQueue.TryEnqueue(() => {
        if (ReferenceEquals(sender, watcher)) ScheduleRefresh();
    });

    void StopWatching()
    {
        var previous = watcher;
        watcher = null;
        previous?.Dispose();
    }

    void ScheduleRefresh()
    {
        refreshTimer.Stop();
        if (!closed) refreshTimer.Start();
    }

    void SetView(bool list)
    {
        var selected = SelectedFiles();
        ActiveFiles.SelectedItems.Clear();
        listView = list;
        UpdateFileViewVisibility();
        RefreshManualGrid();
        foreach (var entry in selected) ActiveFiles.SelectedItems.Add(entry);
        preferences = preferences with { List = list, LayoutVersion = 1 };
    }

    void FileSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!preserveSelection) UpdateCommands();
    }

    void UpdateCommands()
    {
        if (back is null || up is null) return;
        back.IsEnabled = historyIndex > 0;
        forward.IsEnabled = historyIndex >= 0 && historyIndex + 1 < history.Count;
        up.IsEnabled = directory != null && Path.GetDirectoryName(directory) != null;
        int selected = SelectedFiles().Length;
        Count.Text = selected > 0 ? $"已选 {selected} 项 · 共 {entries.Count} 项" : $"{entries.Count} 项";
        selectionName?.UpdateSelectionDetails();
    }

    void SetStatus(string message, bool warning = false)
    {
        Status.Text = message;
        Notice.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        noticeTimer.Stop();
        noticeTimer.Interval = TimeSpan.FromSeconds(warning ? PanelTheme.Appearance.ErrorSeconds : PanelTheme.Appearance.NoticeSeconds);
        if (!busy) noticeTimer.Start();
    }

    void FileContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer is not SelectorItem card) return;
        bool normal = args.Item is not FileEntry { EmptySlot: >= 0 };
        card.IsEnabled = card.IsHitTestVisible = card.IsTabStop = normal;
        card.Opacity = normal ? 1 : 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(card, normal
            ? Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content : Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        ApplyGridMetrics(card);
        if (!normal) return;
        card.ActualThemeChanged -= FileContainerThemeChanged;
        card.ActualThemeChanged += FileContainerThemeChanged;
        // The native presenter is created when a virtualized card enters the visual tree.
        if (PanelTheme.Child<ListViewItemPresenter>(card) == null) {
            card.Loaded -= FileContainerLoaded;
            card.Loaded += FileContainerLoaded;
        } else ApplySelectionColors(card);
    }

    void FileContainerLoaded(object sender, RoutedEventArgs args)
    {
        var item = (SelectorItem)sender;
        ApplyGridMetrics(item);
        ApplySelectionColors(item);
    }
    void FileContainerThemeChanged(FrameworkElement sender, object args) => ApplySelectionColors((SelectorItem)sender);

    static void ApplySelectionColors(SelectorItem card) => StationInteractionColors
        .From(PanelTheme.Appearance.ButtonColor, PanelTheme.Appearance.ShowInteractionBorders, card.ActualTheme == ElementTheme.Dark)
        .ApplySelection(card);

    sealed class NaturalNameComparer : IComparer<string>
    {
        internal static readonly NaturalNameComparer Instance = new();
        public int Compare(string? left, string? right) =>
            left == null || right == null ? StringComparer.OrdinalIgnoreCase.Compare(left, right) : StrCmpLogicalW(left, right);

        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        static extern int StrCmpLogicalW(string left, string right);
    }
}
