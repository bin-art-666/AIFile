using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFile;

internal enum FileSort { Name, Type, Modified, Size }

public sealed partial class MainWindow
{
    sealed record DirectoryView(string[] Selection, double VerticalOffset);
    readonly Dictionary<(string Path, bool List), DirectoryView> directoryViews = [];

    void SaveDirectoryView()
    {
        if (directory == null || loading || SearchPane.Visibility == Visibility.Visible || query.Length != 0) return;
        if (directoryViews.Count >= 100) directoryViews.Remove(directoryViews.Keys.First());
        directoryViews[(directory.ToUpperInvariant(), listView)] = new(
            SelectedFiles().Select(file => file.Path).ToArray(),
            PanelTheme.Child<ScrollViewer>(ActiveFiles)?.VerticalOffset ?? 0);
    }

    void RestoreDirectoryView(string path)
    {
        if (!directoryViews.TryGetValue((path.ToUpperInvariant(), listView), out var view)) return;
        var selected = view.Selection.ToHashSet(PathComparer);
        preserveSelection = true;
        try {
            foreach (var item in entries.Where(item => selected.Contains(item.Path))) ActiveFiles.SelectedItems.Add(item);
        }
        finally { preserveSelection = false; }
        ActiveFiles.UpdateLayout();
        PanelTheme.Child<ScrollViewer>(ActiveFiles)?.ChangeView(null, view.VerticalOffset, null, disableAnimation: true);
        UpdateCommands();
    }

    FileEntry[] SortEntries(IEnumerable<FileEntry> files)
    {
        var directoriesFirst = files.OrderByDescending(file => file.Directory);
        bool descending = preferences.Descending;
        var sorted = preferences.Sort switch {
            FileSort.Type => Order(file => file.TypeLabel, StringComparer.CurrentCultureIgnoreCase),
            FileSort.Modified => Order(file => file.Modified),
            FileSort.Size => Order(file => file.Length),
            _ => Order(file => file.Name, NaturalNameComparer.Instance),
        };
        return sorted.ThenBy(file => file.Name, NaturalNameComparer.Instance).ToArray();

        IOrderedEnumerable<FileEntry> Order<T>(Func<FileEntry, T> value, IComparer<T>? comparer = null) =>
            descending ? directoriesFirst.ThenByDescending(value, comparer) : directoriesFirst.ThenBy(value, comparer);
    }

    void SortHeaderClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string column } && Enum.TryParse<FileSort>(column, out var field)) SortBy(field);
    }

    void SortBy(FileSort field)
    {
        if (ManualGridVisible && manualGrid is { } layout) PersistGrid(layout with { Enabled = false });
        bool descending = preferences.Sort == field ? !preferences.Descending : field is FileSort.Modified or FileSort.Size;
        preferences = preferences with { Sort = field, Descending = descending };
        UpdateSortHeaders();
        if (!loading) {
            UpdateEntries(SortEntries(entries), progressive: false);
            RefreshManualGrid();
        }
    }

    static string SortLabel(FileSort field) => field switch {
        FileSort.Type => "类型", FileSort.Modified => "修改时间", FileSort.Size => "大小", _ => "名称",
    };

    void UpdateSortHeaders()
    {
        foreach (var button in ListHeader.Children.OfType<Button>()) {
            if (button.Tag is not string column || !Enum.TryParse<FileSort>(column, out var field)) continue;
            string label = SortLabel(field);
            bool active = preferences.Sort == field;
            button.Content = active ? label + (preferences.Descending ? " ↓" : " ↑") : label;
            PanelTheme.Label(button, active ? label + (preferences.Descending ? "，降序" : "，升序") : "按" + label + "排序");
        }
    }
}
