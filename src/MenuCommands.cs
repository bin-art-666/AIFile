using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace AIFile;

public sealed partial class MainWindow
{
    internal static void AddMenuCommand(IList<MenuFlyoutItemBase> entries, string text, Func<Func<Task>, Task> run,
        Func<Task> action, bool enabled = true, string? shortcut = null) => entries.Add(MenuCommand(text, run, action, enabled, shortcut));

    static MenuFlyoutItem MenuCommand(string text, Func<Func<Task>, Task> run, Func<Task> action, bool enabled, string? shortcut = null)
    {
        var command = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (shortcut != null) command.KeyboardAcceleratorTextOverride = shortcut;
        command.Click += async (_, _) => await run(action);
        return command;
    }
    internal static void MenuHeading(MenuFlyout menu, string text)
    {
        menu.Items.Add(new MenuFlyoutItem { Text = text, IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
    }
    internal static void AppendMenuLayers<T>(MenuFlyout menu, T[] primary, T[] overflow, T separatorMarker,
        Func<T, MenuFlyoutItem?> command, MenuFlyoutItem? extra = null) where T : struct, Enum
    {
        var more = new MenuFlyoutSubItem { Text = "…" };
        AutomationProperties.SetName(more, "更多操作");
        AppendGroupRows(menu.Items, primary);
        AppendGroupRows(more.Items, overflow);
        if (extra != null) more.Items.Add(extra);
        if (more.Items.Count > 0) menu.Items.Add(more);

        void AppendGroupRows(IList<MenuFlyoutItemBase> target, T[] rows)
        {
            bool hasCommand = false, separator = false;
            foreach (var row in rows) {
                if (EqualityComparer<T>.Default.Equals(row, separatorMarker)) { separator = hasCommand; continue; }
                if (command(row) is not { } item) continue;
                if (separator) target.Add(new MenuFlyoutSeparator());
                target.Add(item);
                hasCommand = true;
                separator = false;
            }
        }
    }
}
