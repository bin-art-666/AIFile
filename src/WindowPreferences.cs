using System.Text.Json;

namespace AIFile;

internal sealed record WindowPreferences(int Width = 1060, int Height = 720, bool List = false, int LayoutVersion = 0,
    int? X = null, int? Y = null, FileSort Sort = FileSort.Name, bool Descending = false,
    ItemMenuLayout? FileMenu = null, ItemMenuLayout? FolderMenu = null, HaloAppearance? Appearance = null,
    bool RememberPosition = true, bool Pinned = false)
{
    static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIFile", "window.json");

    internal static WindowPreferences Read()
    {
        try {
            var saved = JsonSerializer.Deserialize<WindowPreferences>(File.ReadAllText(SettingsPath)) ?? new();
            return saved.LayoutVersion == 0 ? saved with { LayoutVersion = 1, List = false } : saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) {
            return new();
        }
    }

    internal void Save()
    {
        string? temporary = null;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            temporary = SettingsPath + "." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            System.Diagnostics.Trace.WriteLine(error);
        }
        finally {
            if (temporary != null) {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
