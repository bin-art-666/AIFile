using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.UI.Xaml;

namespace AIFile;

public sealed record FilePlace(string Name, string Path, string Glyph)
{
    public override string ToString() => Name;
}

public sealed record FileEntry(string Name, string Path, bool Directory, DateTimeOffset Modified, long Length,
    FileAttributes Attributes = 0, int EmptySlot = -1)
{
    public override string ToString() => Name;
    public Visibility FileVisibility => Directory ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FolderVisibility => Directory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GridItemVisibility => EmptySlot < 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ModifiedLabel => Modified.LocalDateTime.ToString("yyyy/MM/dd HH:mm");
    public string TypeLabel => Directory ? "文件夹" : System.IO.Path.GetExtension(Name) is { Length: > 1 } extension
        ? extension[1..].ToUpperInvariant() : "文件";
    public string SizeLabel => Directory ? "" : Length switch {
        < 1024 => $"{Length} B",
        < 1024 * 1024 => $"{Length / 1024d:0.#} KB",
        < 1024 * 1024 * 1024 => $"{Length / (1024d * 1024):0.#} MB",
        _ => $"{Length / (1024d * 1024 * 1024):0.#} GB",
    };
    public string Glyph => System.IO.Path.GetExtension(Name).ToLowerInvariant() switch {
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" => "\uEB9F",
        ".mp4" or ".mkv" or ".mov" or ".avi" => "\uE714",
        ".mp3" or ".flac" or ".wav" or ".m4a" => "\uE189",
        ".zip" or ".7z" or ".rar" => "\uF012",
        _ => "\uE8A5",
    };
}

internal static class FileLocations
{
    internal static IReadOnlyList<FilePlace> Places()
    {
        var places = new List<FilePlace>();
        void Add(string name, string path, string icon)
        {
            if (!string.IsNullOrWhiteSpace(path)) places.Add(new(name, path, icon));
        }

        Add("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "\uE7F4");
        Add("下载", KnownFolder(new("374DE290-123F-4565-9164-39C4925E467B")), "\uE896");
        Add("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "\uE8A5");
        Add("图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "\uEB9F");
        Add("音乐", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "\uE189");
        Add("视频", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "\uE714");
        foreach (var drive in DriveInfo.GetDrives())
            Add(drive.Name.TrimEnd('\\'), drive.RootDirectory.FullName, "\uEDA2");
        return places;
    }

    static string KnownFolder(Guid id)
    {
        int result = SHGetKnownFolderPath(in id, 0x4000, 0, out nint path);
        try { return result >= 0 ? Marshal.PtrToStringUni(path) ?? "" : ""; }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);

    internal static string Normalize(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(expanded)) throw new IOException("请输入完整目录路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    internal static IEnumerable<FileEntry> Enumerate(string path, string query = "")
    {
        var entries = new FileSystemEnumerable<FileEntry>(path,
            (ref FileSystemEntry entry) => new(entry.FileName.ToString(), entry.ToFullPath(),
                entry.IsDirectory, entry.LastWriteTimeUtc, entry.Length, entry.Attributes),
            new EnumerationOptions {
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            });
        if (query.Length != 0)
            entries.ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                entry.FileName.Contains(query, StringComparison.OrdinalIgnoreCase);
        return entries;
    }

    internal static async Task ReadBatches(
        string path, string query, Func<FileEntry[], Task> receive, CancellationToken token)
    {
        var batches = Channel.CreateBounded<FileEntry[]>(2);
        var read = Task.Run(async () => {
            try {
                var batch = new List<FileEntry>(144);
                foreach (var entry in Enumerate(path, query)) {
                    token.ThrowIfCancellationRequested();
                    batch.Add(entry);
                    if (batch.Count < 144) continue;
                    await batches.Writer.WriteAsync(batch.ToArray(), token);
                    batch.Clear();
                }
                if (batch.Count > 0) await batches.Writer.WriteAsync(batch.ToArray(), token);
                batches.Writer.TryComplete();
            }
            catch (Exception error) { batches.Writer.TryComplete(error); }
        }, CancellationToken.None);

        await foreach (var batch in batches.Reader.ReadAllAsync(token)) {
            token.ThrowIfCancellationRequested();
            await receive(batch);
        }
        await read;
    }
}
