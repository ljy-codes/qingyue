using System.Text.RegularExpressions;

namespace Qingyue.Core;

public sealed class LocalWorkspaceScanner
{
    public static IReadOnlySet<string> DefaultIgnoredDirectories { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs" };

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
        if (options.MaximumEntries is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "工作区条目上限必须在 1 到 100000 之间。");

        var ignored = options.IgnoredDirectories is null
            ? DefaultIgnoredDirectories
            : DefaultIgnoredDirectories.Concat(options.IgnoredDirectories)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nameMatcher = Glob(options.FileNamePattern);
        var entries = new List<WorkspaceFileEntry>();
        var warnings = new List<string>();
        var pending = new Stack<string>();
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
                if (entries.Count >= options.MaximumEntries)
                {
                    warnings.Add($"工作区条目达到上限 {options.MaximumEntries:N0}，后续内容已忽略。");
                    return new(fullRoot, entries, warnings);
                }

                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"无法读取“{Relative(fullRoot, child)}”：{ex.Message}");
                    continue;
                }
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                string name = Path.GetFileName(child);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (ignored.Contains(name)) continue;
                    entries.Add(new(child, Relative(fullRoot, child), true, 0, LastWrite(child), null));
                    pending.Push(child);
                    continue;
                }
                if (!nameMatcher(name)) continue;

                TextKind kind = TextServices.DetectKind(child, "");
                if (!MatchesFilter(kind, options.FileFilter)) continue;
                entries.Add(new(child, Relative(fullRoot, child), false, Length(child), LastWrite(child), kind));
            }
        }

        return new(fullRoot, entries
            .OrderBy(entry => entry.IsDirectory ? 0 : 1)
            .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
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
        catch { return 0; }
    }

    private static DateTimeOffset LastWrite(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTimeOffset.MinValue; }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}
