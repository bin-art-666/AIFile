namespace AIFile;

internal sealed record DirectorySearchHit(FileEntry Entry, string RelativeDirectory, string? ImageMatch);
internal sealed record DirectorySearchResult(int Count, bool Limited, bool Skipped, string? OcrWarning);

internal sealed class DirectorySearch
{
    internal const int Limit = 200;
    readonly ImageTextSearch imageText = new();

    internal async Task<DirectorySearchResult> Find(string root, string query, bool recursive, bool ocr,
        Action<DirectorySearchHit> receive, CancellationToken token)
    {
        var engines = Array.Empty<Windows.Media.Ocr.OcrEngine>();
        string? ocrWarning = null;
        if (ocr) {
            try {
                engines = ImageTextSearch.Engines();
                if (engines.Length == 0) ocrWarning = "Windows 没有可用的 OCR 语言。";
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException) {
                ocrWarning = "本机图片文字识别暂不可用。";
            }
        }
        string languages = string.Join(',', engines.Select(engine => engine.RecognizerLanguage.LanguageTag));
        var folders = new Stack<string>();
        folders.Push(root);
        bool skipped = false;
        int count = 0;
        while (folders.TryPop(out string? path)) {
            token.ThrowIfCancellationRequested();
            try {
                foreach (var file in FileLocations.Enumerate(path)) {
                    token.ThrowIfCancellationRequested();
                    bool link = (file.Attributes & FileAttributes.ReparsePoint) != 0;
                    if (recursive && file.Directory && !link) folders.Push(file.Path);
                    bool nameMatches = file.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                    string? match = null;
                    if (!nameMatches && !file.Directory && !link && engines.Length > 0 && ImageTextSearch.IsImage(file.Path)) {
                        try {
                            string recognized = await imageText.Read(file.Path, engines, languages, token);
                            match = ImageTextSearch.Match(recognized, query);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) {
                            ocrWarning = "部分图片无法识别。";
                        }
                    }
                    if (!nameMatches && match == null) continue;
                    if (count == Limit) return new(count, true, skipped, ocrWarning);
                    receive(new(file, Path.GetRelativePath(root, path), match));
                    count++;
                }
            }
            catch (Exception error) when (path != root && error is (IOException or UnauthorizedAccessException)) { skipped = true; }
        }
        return new(count, false, skipped, ocrWarning);
    }
}
