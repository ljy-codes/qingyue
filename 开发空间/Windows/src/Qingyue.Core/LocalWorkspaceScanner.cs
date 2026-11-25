using System.Text.RegularExpressions;

namespace Qingyue.Core;

public sealed class LocalWorkspaceScanner
{
    public static IReadOnlySet<string> DefaultIgnoredDirectories { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", "bin", "obj", "node_modules", ".build"
        };

    public static IReadOnlySet<string> DefaultExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "txt", "md", "markdown", "json", "jsonc", "yaml", "yml", "ini", "conf",
            "config", "cfg", "toml", "log", "xml", "env", "js", "ts", "py", "java",
            "cs", "sql", "html", "css", "go", "rs", "sh", "ps1", "csv"
        };

    public Task<WorkspaceScanResult> ScanAsync(
        string root, WorkspaceScanOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(root, options ?? new WorkspaceScanOptions(), cancellationToken), cancellationToken);

    private static WorkspaceScanResult Scan(
        string root, WorkspaceScanOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("工作区目录不能为空。", nameof(root));
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) throw new DirectoryNotFoundException("工作区目录不存在：" + fullRoot);
        if (new DirectoryInfo(fullRoot).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("工作区目录不能是重解析点。");
        if (options.MaximumEntries < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "工作区文件上限不能小于 0。");
        if (options.MaximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "工作区容量上限不能小于 0。");

        var ignored = options.IgnoredDirectories is null
            ? DefaultIgnoredDirectories
            : DefaultIgnoredDirectories.Concat(options.IgnoredDirectories)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nameMatcher = Glob(options.FileNamePattern);
        var extensions = (string.IsNullOrWhiteSpace(options.ExtensionFilter)
                ? DefaultExtensions
                : options.ExtensionFilter
                    .Split([',', ';', ' ', '\t', '\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => value.Trim('.'))
                    .Where(value => value.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
        var entries = new List<WorkspaceFileEntry>();
        var warnings = new List<string>();
        var pending = new Stack<string>();
        long totalBytes = 0;
        pending.Push(fullRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"无法读取目录“{Relative(fullRoot, directory)}”：{ex.Message}");
                continue;
            }

            foreach (string child in children.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"无法读取“{Relative(fullRoot, child)}”：{ex.Message}");
                    continue;
                }
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                string name = Path.GetFileName(child);
                bool hidden = name.StartsWith('.') || attributes.HasFlag(FileAttributes.Hidden);
                if (!options.IncludeHidden && hidden) continue;
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!ignored.Contains(name)) pending.Push(child);
                    continue;
                }

                if (!nameMatcher(name)) continue;
                string extension = Path.GetExtension(name).TrimStart('.');
                if (!extensions.Contains(extension)) continue;
                TextKind kind = TextServices.DetectKind(child, "");
                if (!MatchesFilter(kind, options.FileFilter)) continue;

                long length = Length(child);
                if (length < 0) continue;
                if (entries.Count >= options.MaximumEntries)
                    throw new WorkspaceScanLimitException("工作区文件数量超过上限。");
                if (length > options.MaximumBytes - totalBytes)
                    throw new WorkspaceScanLimitException("工作区扫描容量超过上限。");
                totalBytes += length;
                entries.Add(new(child, Relative(fullRoot, child), false, length, LastWrite(child), kind));
            }
        }

        return new(fullRoot, entries
            .OrderBy(entry => entry.RelativePath, NaturalOrder.Instance)
            .ToArray(), warnings);
    }

    private static bool MatchesFilter(TextKind kind, WorkspaceFileFilter filter) => filter switch
    {
        WorkspaceFileFilter.All => true,
        WorkspaceFileFilter.Text => kind is TextKind.Plain or TextKind.Markdown,
        WorkspaceFileFilter.Markdown => kind == TextKind.Markdown,
        WorkspaceFileFilter.Code => kind is TextKind.JavaScript or TextKind.TypeScript or TextKind.Python
            or TextKind.Java or TextKind.CSharp or TextKind.Sql or TextKind.Html or TextKind.Css
            or TextKind.Go or TextKind.Rust or TextKind.Shell or TextKind.PowerShell,
        WorkspaceFileFilter.Data => kind is TextKind.Json or TextKind.Jsonc or TextKind.Yaml
            or TextKind.Xml or TextKind.Toml or TextKind.Csv,
        WorkspaceFileFilter.Configuration => kind == TextKind.Ini,
        _ => false
    };

    private static Func<string, bool> Glob(string? pattern)
    {
        string[] patterns = (pattern ?? "*").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns.Length == 0) patterns = ["*"];
        var regex = new Regex(
            "^(?:" + string.Join("|", patterns.Select(value =>
                Regex.Escape(value).Replace(@"\*", ".*").Replace(@"\?", "."))) + ")$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(350));
        return value => regex.IsMatch(value);
    }

    private static long Length(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return -1; }
    }

    private static DateTimeOffset LastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTimeOffset.MinValue; }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}
