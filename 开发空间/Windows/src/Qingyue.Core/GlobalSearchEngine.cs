namespace Qingyue.Core;

public sealed class GlobalSearchEngine
{
    private readonly LocalWorkspaceScanner scanner;

    public GlobalSearchEngine(LocalWorkspaceScanner? scanner = null) =>
        this.scanner = scanner ?? new LocalWorkspaceScanner();

    public async Task<GlobalSearchResult> SearchAsync(
        string root, string query, GlobalSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0) return new([], [], [], 0);
        options ??= new GlobalSearchOptions();
        if (options.ContextLines is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(options), "上下文行数必须在 0 到 10 之间。");
        if (options.MaximumFiles is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(options), "搜索文件上限必须在 1 到 10000 之间。");
        if (options.MaximumMatches is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "搜索匹配上限必须在 1 到 100000 之间。");

        var scan = await scanner.ScanAsync(root, new WorkspaceScanOptions(
            FileNamePattern: options.FileNamePattern,
            FileFilter: WorkspaceFileFilter.All,
            MaximumEntries: Math.Max(options.MaximumFiles * 4, 1000)), cancellationToken);
        var matches = new List<GlobalSearchMatch>();
        var files = new List<GlobalSearchFileResult>();
        var warnings = scan.Warnings.ToList();
        int scanned = 0;

        foreach (var entry in scan.Entries.Where(item => !item.IsDirectory)
                     .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scanned >= options.MaximumFiles)
            {
                warnings.Add($"搜索文件达到上限 {options.MaximumFiles:N0}，后续文件已忽略。");
                break;
            }
            scanned++;
            string text;
            try { text = DocumentFile.Open(entry.Path).Text; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                warnings.Add($"跳过“{entry.RelativePath}”：{ex.Message}");
                continue;
            }
            if (text.Length > SearchEngine.MaximumTextLength)
            {
                warnings.Add($"跳过“{entry.RelativePath}”：文本超过搜索上限 {SearchEngine.MaximumTextLength:N0} 个 UTF-16 单元。");
                continue;
            }

            IReadOnlyList<SearchMatch> found;
            try
            {
                found = SearchEngine.Find(text, query, options.Replacement, options.Search ?? new SearchOptions());
            }
            catch (ArgumentException) { throw; }
            catch (InvalidOperationException) { throw; }
            if (found.Count == 0) continue;
            if (matches.Count + found.Count > options.MaximumMatches)
                throw new InvalidOperationException($"全局搜索匹配数量超过 {options.MaximumMatches:N0}，请缩小范围。");

            var lines = SplitLines(text);
            foreach (var match in found)
            {
                var location = Locate(lines, match.Start);
                string before = string.Join("\n", lines
                    .Skip(Math.Max(0, location.LineIndex - options.ContextLines))
                    .Take(location.LineIndex - Math.Max(0, location.LineIndex - options.ContextLines))
                    .Select(item => item.Text));
                string after = string.Join("\n", lines
                    .Skip(location.LineIndex + 1)
                    .Take(options.ContextLines)
                    .Select(item => item.Text));
                matches.Add(new(entry.Path, location.LineIndex + 1, location.Column + 1,
                    lines[location.LineIndex].Text, before, after,
                    text.Substring(match.Start, match.Length), match.Replacement));
            }

            string? preview = null;
            if (options.BuildReplacementPreview)
                preview = SearchEngine.ReplaceAll(text, found);
            files.Add(new(entry.Path, found.Count, preview));
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
}
