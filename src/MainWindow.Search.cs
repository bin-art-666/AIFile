using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace AIFile;

public sealed partial class MainWindow
{
    readonly DirectorySearch directorySearch = new();
    CancellationTokenSource? advancedSearch;

    void SearchScopeChanged(object sender, RoutedEventArgs args)
    {
        if (Search.Text.Length == 0 || closed) return;
        StopWatching();
        _ = RunUi(ApplySearch);
    }

    void UpdateFileViewVisibility()
    {
        bool searching = SearchPane.Visibility == Visibility.Visible;
        Cards.Visibility = !searching && !listView ? Visibility.Visible : Visibility.Collapsed;
        Rows.Visibility = !searching && listView ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Visibility = !searching && entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void CancelAdvancedSearch()
    {
        advancedSearch?.Cancel();
        advancedSearch = null;
        if (SearchPane.Visibility != Visibility.Visible) return;
        if (watcher?.IncludeSubdirectories == true) StopWatching();
        SearchPane.Visibility = Visibility.Collapsed;
        SearchResults.Items.Clear();
        UpdateFileViewVisibility();
    }

    async Task RunAdvancedSearch(string value)
    {
        if (directory is not { } path || value.Length == 0) return;
        advancedSearch?.Cancel();
        using var request = new CancellationTokenSource();
        advancedSearch = request;
        bool recursive = SearchSubfolders.IsChecked == true;
        bool ocr = SearchImageText.IsChecked == true;
        SearchPane.Visibility = Visibility.Visible;
        SearchResults.Items.Clear();
        UpdateFileViewVisibility();
        SearchStatus.Text = ocr ? "正在搜索文件和识别图片文字…" : "正在搜索文件夹…";
        if (watcher != null && watcher.IncludeSubdirectories != recursive) StopWatching();
        StartWatching(path);
        try {
            var result = await Task.Run(() => directorySearch.Find(path, value, recursive, ocr,
                hit => DispatcherQueue.TryEnqueue(() => {
                    if (advancedSearch == request && !request.IsCancellationRequested && !closed) AddSearchResult(hit);
                }), request.Token), request.Token);
            if (request.IsCancellationRequested || closed) return;
            SearchStatus.Text = result.Limited ? $"仅显示前 {DirectorySearch.Limit} 项，请缩小搜索范围。"
                : result.Count == 0 ? "没有匹配的项目。" : $"找到 {result.Count} 项。";
            if (result.Skipped) SearchStatus.Text += "部分目录无法访问。";
            if (result.OcrWarning != null) SearchStatus.Text += result.OcrWarning;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            if (!request.IsCancellationRequested) SearchStatus.Text = "无法搜索此目录：" + error.Message;
        }
        finally {
            if (advancedSearch == request) advancedSearch = null;
        }
    }

    void AddSearchResult(DirectorySearchHit hit)
    {
        var file = hit.Entry;
        var icon = new FileGlyph { Entry = file, IconSize = 22 };
        var label = new StackPanel { Spacing = 2 };
        label.Children.Add(new TextBlock { Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        label.Children.Add(new TextBlock { Text = hit.RelativeDirectory == "." ? "当前目录" : hit.RelativeDirectory,
            FontSize = 12, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis });
        if (hit.ImageMatch is { } match) label.Children.Add(new TextBlock {
            Text = "图片文字 · " + match, FontSize = 12, Opacity = .8, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var locate = new Button { Content = "定位", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(locate, $"定位 {file.Name}，{hit.RelativeDirectory}");
        locate.Click += (_, _) => _ = RunUi(() => LocateSearchResult(file));
        var row = new Grid { ColumnSpacing = 10, Padding = new(4) };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(label, 1);
        Grid.SetColumn(locate, 2);
        row.Children.Add(icon);
        row.Children.Add(label);
        row.Children.Add(locate);
        SearchResults.Items.Add(new ListViewItem { Content = row, Tag = hit, HorizontalContentAlignment = HorizontalAlignment.Stretch });
    }

    async Task LocateSearchResult(FileEntry file)
    {
        if (Path.GetDirectoryName(file.Path) is not { } parent) return;
        CancelAdvancedSearch();
        Search.Text = "";
        SearchBar.Visibility = Visibility.Collapsed;
        searchTimer.Stop();
        query = "";
        await Navigate(parent);
        if (!PathComparer.Equals(parent, directory)) return;
        var found = entries.FirstOrDefault(entry => PathComparer.Equals(entry.Path, file.Path));
        if (found == null) {
            SetStatus("项目已移动、删除或暂不可访问。");
            return;
        }
        ActiveFiles.SelectedItem = found;
        ActiveFiles.ScrollIntoView(found);
        ActiveFiles.Focus(FocusState.Keyboard);
    }
}
