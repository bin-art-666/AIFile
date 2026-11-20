using System.Text.Json;

namespace AIFile;

internal static class FolderColors
{
    static readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIFile", "folder-colors.json");
    static readonly Dictionary<string, string> colors = Read();
    internal static event Action<string, string>? Changed;

    static Dictionary<string, string> Read()
    {
        try {
            return new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(settings))?
                .Where(item => MaterialOptions.IsColor(item.Value)) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static string? GetOverride(string path) => colors.GetValueOrDefault(path);
    internal static string Get(string path) => GetOverride(path) ?? HaloAppearance.FolderColor;
    internal static void Preview(string path, string color) => Changed?.Invoke(path, color);

    internal static void Save(string path, string? color)
    {
        var updated = new Dictionary<string, string>(colors, StringComparer.OrdinalIgnoreCase);
        if (color == null) updated.Remove(path);
        else updated[path] = color;
        Persist(updated);
    }

    internal static void Move(string from, string to)
    {
        var updated = new Dictionary<string, string>(colors, StringComparer.OrdinalIgnoreCase);
        string prefix = from + Path.DirectorySeparatorChar;
        var moved = colors.Where(pair => pair.Key.Equals(from, StringComparison.OrdinalIgnoreCase)
            || pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (moved.Length == 0) return;
        foreach (var color in moved) updated.Remove(color.Key);
        foreach (var color in moved) updated[to + color.Key[from.Length..]] = color.Value;
        Persist(updated);
    }

    static void Persist(Dictionary<string, string> updated)
    {
        string temp = settings + "." + Guid.NewGuid().ToString("N");
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(updated));
            File.Move(temp, settings, overwrite: true);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        colors.Clear();
        foreach (var item in updated) colors.Add(item.Key, item.Value);
    }
}
