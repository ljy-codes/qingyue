namespace Qingyue.Core;

public sealed class GlobalSearchEngine
{
    internal const int MaximumPreviewLength = 1024;
    private readonly LocalWorkspaceScanner scanner;

    public GlobalSearchEngine(LocalWorkspaceScanner? scanner = null) =>
        this.scanner = scanner ?? new LocalWorkspaceScanner();

    public async Task<GlobalSearchResult> SearchAsync(
        string root, string query, GlobalSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        options ??= new GlobalSearchOptions();
        if (query.Length == 0 || options.MaximumMatches <= 0 ||
            options.MaximumFileBytes <= 0 || options.MaximumTotalBytes <= 0)
            return new([], [], [], 0);
        int contextLines = Math.Clamp(options.ContextLines, 0, 10);
        string replacement = options.BuildReplacementPreview ? options.Replacement : "";
        var searchOptions = options.Search ?? new SearchOptions();
        // Validate before scanning so malformed patterns fail even for an empty workspace.
        _ = SearchEngine.Find("", query, replacement, searchOptions);
        int maximumMatches = Math.Min(10_000, options.MaximumMatches);

        var scan = await scanner.ScanAsync(root, new WorkspaceScanOptions(
            FileNamePattern: options.FileNamePattern,
            FileFilter: WorkspaceFileFilter.All,
            MaximumEntries: 10_000), cancellationToken);
        var matches = new List<GlobalSearchMatch>();
        var files = new List<GlobalSearchFileResult>();
        var warnings = scan.Warnings.ToList();
        int scanned = 0;
        long scannedBytes = 0;
        long fileByteLimit = Math.Min(DocumentFile.MaximumSize, options.MaximumFileBytes);

        foreach (var entry in scan.Entries.Where(item => !item.IsDirectory &&
                     (string.IsNullOrEmpty(options.FileNameContains) ||
                      item.RelativePath.Contains(options.FileNameContains, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length < 0 || entry.Length > fileByteLimit ||
                entry.Length > options.MaximumTotalBytes - scannedBytes)
                continue;
            scanned++;
            scannedBytes += entry.Length;
            DocumentFile document;
            try { document = DocumentFile.Open(entry.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                warnings.Add($"跳过“{entry.RelativePath}”：{ex.Message}");
                continue;
            }
            string text = document.Text;
            if (text.Length > SearchEngine.MaximumTextLength)
            {
                warnings.Add($"跳过“{entry.RelativePath}”：文本超过搜索上限 {SearchEngine.MaximumTextLength:N0} 个 UTF-16 单元。");
                continue;
            }

            IReadOnlyList<SearchMatch> found;
            try
            {
                found = SearchEngine.Find(text, query, replacement, searchOptions);
            }
            catch (ArgumentException) { throw; }
            catch (InvalidOperationException) { throw; }
            if (found.Count == 0) continue;
            if (options.BuildReplacementPreview &&
                matches.Count + found.Count > maximumMatches)
                throw new InvalidOperationException($"全局搜索匹配数量超过 {maximumMatches:N0}，请缩小范围。");
            int accepted = Math.Min(found.Count, maximumMatches - matches.Count);
            if (accepted <= 0)
            {
                warnings.Add($"搜索匹配达到上限 {maximumMatches:N0}，后续结果已截断。");
                break;
            }

            var lines = SplitLines(text);
            foreach (var match in found.Take(accepted))
            {
                var location = Locate(lines, match.Start);
                int firstLine = Math.Max(0, location.LineIndex - contextLines);
                int lastLine = Math.Min(lines.Count - 1, location.LineIndex + contextLines);
                string before = string.Join("\n", lines
                    .Skip(firstLine)
                    .Take(location.LineIndex - firstLine)
                    .Select(item => item.Text));
                string after = string.Join("\n", lines
                    .Skip(location.LineIndex + 1)
                    .Take(contextLines)
                    .Select(item => item.Text));
                matches.Add(new(entry.Path, location.LineIndex + 1, location.Column + 1,
                    lines[location.LineIndex].Text, before, after,
                    text.Substring(match.Start, match.Length), match.Replacement,
                    match.Start, match.Length, BuildPreview(text, lines, firstLine, lastLine)));
            }

            string? preview = null;
            GlobalSearchReplacementPreview? replacementPreview = null;
            if (options.BuildReplacementPreview)
            {
                preview = SearchEngine.ReplaceAll(text, found);
                if (!string.Equals(preview, text, StringComparison.Ordinal))
                    replacementPreview = new GlobalSearchReplacementPreview(document, found.Count, preview);
            }
            files.Add(new(entry.Path, entry.RelativePath, accepted, preview, replacementPreview));
            if (accepted < found.Count || matches.Count >= maximumMatches)
            {
                warnings.Add($"搜索匹配达到上限 {maximumMatches:N0}，后续结果已截断。");
                break;
            }
        }

        return new(matches.AsReadOnly(), files.AsReadOnly(), warnings.AsReadOnly(), scanned);
    }

    private sealed record SourceLine(string Text, int Start);

    private static List<SourceLine> SplitLines(string text)
    {
        var lines = new List<SourceLine>();
        int start = 0;
        while (start <= text.Length)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline;
            if (end > start && text[end - 1] == '\r') end--;
            lines.Add(new(text[start..end], start));
            if (newline < 0) break;
            start = newline + 1;
        }
        return lines;
    }

    private static (int LineIndex, int Column) Locate(IReadOnlyList<SourceLine> lines, int offset)
    {
        int low = 0, high = lines.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (lines[middle].Start <= offset) low = middle + 1;
            else high = middle - 1;
        }
        int index = Math.Max(0, high);
        return (index, Math.Max(0, offset - lines[index].Start));
    }

    private static string BuildPreview(
        string text, IReadOnlyList<SourceLine> lines, int firstLine, int lastLine)
    {
        int start = lines[firstLine].Start;
        int end = lastLine + 1 < lines.Count ? lines[lastLine + 1].Start : text.Length;
        int previewEnd = Math.Min(end, start + MaximumPreviewLength);
        if (previewEnd > start && previewEnd < text.Length &&
            char.IsHighSurrogate(text[previewEnd - 1]) &&
            char.IsLowSurrogate(text[previewEnd]))
            previewEnd--;
        string preview = text[start..previewEnd].Trim();
        return preview + (previewEnd < end ? "…" : "");
    }
}
