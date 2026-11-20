using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIFile;

internal readonly record struct GridCell(int Row, int Column);

internal sealed record ManualGridLayout(bool Enabled, int Columns, Dictionary<string, GridCell> Cells)
{
    internal const int MaxColumns = 64, MaxRows = 4096;

    internal ManualGridLayout Reconcile(IEnumerable<FileEntry> entries, int visibleColumns)
    {
        int columns = Math.Clamp(Math.Max(Columns, visibleColumns), 1, MaxColumns);
        var positions = new Dictionary<string, GridCell>(StringComparer.OrdinalIgnoreCase);
        var occupied = new HashSet<GridCell>();
        var missing = new List<string>();
        foreach (var entry in entries) {
            if (Cells.TryGetValue(entry.Name, out var cell) && Valid(cell, columns) && occupied.Add(cell))
                positions[entry.Name] = cell;
            else missing.Add(entry.Name);
        }
        int next = 0;
        foreach (string name in missing) {
            while (next < columns * MaxRows && !occupied.Add(new(next / columns, next % columns))) next++;
            if (next == columns * MaxRows) break;
            positions[name] = new(next / columns, next % columns);
            next++;
        }
        return new(Enabled, columns, positions);
    }

    internal ManualGridLayout? Drop(string anchor, string[] files, GridCell target)
    {
        if (!Valid(target, Columns) || !Cells.TryGetValue(anchor, out var original) || !files.Contains(anchor, StringComparer.OrdinalIgnoreCase))
            return null;
        var selected = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var positions = new Dictionary<string, GridCell>(Cells, StringComparer.OrdinalIgnoreCase);
        var blocked = Cells.Where(item => !selected.Contains(item.Key)).ToDictionary(item => item.Value, item => item.Key);
        foreach (string file in selected) {
            if (!Cells.TryGetValue(file, out var from)) return null;
            var destination = new GridCell(from.Row + target.Row - original.Row, from.Column + target.Column - original.Column);
            if (!Valid(destination, Columns)) return null;
            if (blocked.TryGetValue(destination, out string? existing)) {
                if (selected.Count != 1) return null;
                positions[existing] = original;
            }
            positions[file] = destination;
        }
        return this with { Cells = positions };
    }

    static bool Valid(GridCell cell, int columns) => cell.Row is >= 0 and < MaxRows && cell.Column >= 0 && cell.Column < columns;
}

internal static class ManualGridStore
{
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIFile", "grid-layouts");
    static string FilePath(string folder) => Path.Combine(Root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant()))) + ".json");

    internal static ManualGridLayout? Read(string folder)
    {
        try {
            var metadata = new FileInfo(FilePath(folder));
            if (!metadata.Exists || metadata.Length > 48 * 1024 * 1024) return null;
            var value = JsonSerializer.Deserialize<ManualGridLayout>(File.ReadAllText(metadata.FullName));
            return value is { Columns: >= 1 and <= ManualGridLayout.MaxColumns, Cells: not null }
                ? value with { Cells = new(value.Cells, StringComparer.OrdinalIgnoreCase) } : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) {
            return null;
        }
    }

    internal static void Write(string folder, ManualGridLayout layout)
    {
        Directory.CreateDirectory(Root);
        string path = FilePath(folder), temporary = path + "." + Guid.NewGuid().ToString("N");
        try {
            File.WriteAllText(temporary, JsonSerializer.Serialize(layout));
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
