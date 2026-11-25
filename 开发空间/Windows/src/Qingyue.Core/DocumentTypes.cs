namespace Qingyue.Core;

public enum TextCodec { Utf8, Utf8Bom, Utf16LE, Utf16BE, Gb18030 }
public enum LineEnding { Lf, CrLf, Cr, Mixed }
// Values are persisted in drafts. Append new kinds; never renumber existing ones.
public enum TextKind
{
    Plain = 0, Markdown = 1, Json = 2, Jsonc = 3, Yaml = 4, Ini = 5, Xml = 6,
    JavaScript = 7, TypeScript = 8, Python = 9, Java = 10, CSharp = 11, Sql = 12,
    Html = 13, Css = 14, Go = 15, Rust = 16, Shell = 17, PowerShell = 18, Toml = 19, Csv = 20
}

public sealed class FileConflictException(string message = "磁盘文件已被其他程序修改，请另存为或明确选择覆盖。") : IOException(message);
public sealed class MixedLineEndingsException() : IOException("文件含有混合换行，请明确选择 LF 或 CRLF 后保存。");
public sealed class WorkspaceScanLimitException(string message) : IOException(message);

public sealed record DocumentSnapshot(
    int Version, Guid Id, string? Path, string? SuggestedName, string Text,
    TextCodec Codec, LineEnding LineEnding, string BaselineText,
    TextCodec BaselineCodec, LineEnding BaselineLineEnding, string? BaselineDigest,
    bool HadBom, bool WasSaved, string? ResolvedPath = null);

public sealed record WorkspaceEntry(
    Guid Id, string? Path, bool IsReading, int SourceAnchor, int SelectionStart,
    int SelectionLength, bool RecoverDraft = true, bool AutomaticKind = true,
    TextKind Kind = TextKind.Plain)
{
    // Missing in older sessions: fall back to SourceAnchor.
    public int? ViewportAnchor { get; init; }
    public double ViewportDelta { get; init; }
}

public sealed record WorkspaceSession(int Version, List<WorkspaceEntry> Entries, Guid? ActiveId);
public sealed record RestoredDocument(WorkspaceEntry Entry, DocumentFile File);
public sealed record WorkspaceLoadResult(List<RestoredDocument> Documents, Guid? ActiveId, List<string> Warnings);
public sealed record WorkspaceSaveResult(DateTimeOffset SavedAt, List<string> Errors);
public sealed record DocumentBookmark(
    Guid Id, Guid DocumentId, string? Path, int SourceAnchor, string Label,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record BookmarkSaveResult(DateTimeOffset SavedAt, List<string> Errors);

public sealed record SearchOptions(bool CaseSensitive = false, bool Regex = false, bool WholeWord = false);
public sealed record SearchMatch(int Start, int Length, string Replacement);
public enum WorkspaceFileFilter { All, Text, Markdown, Code, Data, Configuration }
public sealed record WorkspaceScanOptions(
    string FileNamePattern = "*",
    WorkspaceFileFilter FileFilter = WorkspaceFileFilter.All,
    IReadOnlySet<string>? IgnoredDirectories = null,
    int MaximumEntries = 10_000,
    string ExtensionFilter = "",
    bool IncludeHidden = false,
    long MaximumBytes = 512L * 1024 * 1024);
public sealed record WorkspaceFileEntry(
    string Path, string RelativePath, bool IsDirectory, long Length, DateTimeOffset LastWriteTimeUtc,
    TextKind? Kind);
public sealed record WorkspaceScanResult(
    string Root, IReadOnlyList<WorkspaceFileEntry> Entries, IReadOnlyList<string> Warnings);
public sealed record GlobalSearchOptions(
    string FileNamePattern = "*",
    string FileNameContains = "",
    SearchOptions? Search = null,
    string Replacement = "",
    int ContextLines = 0,
    int MaximumMatches = 10_000,
    bool BuildReplacementPreview = true,
    long MaximumFileBytes = 10L * 1024 * 1024,
    long MaximumTotalBytes = 128L * 1024 * 1024);
public sealed record GlobalSearchMatch(
    string Path, int LineNumber, int ColumnNumber, string LineText,
    string BeforeContext, string AfterContext, string Value, string Replacement,
    int SourceStart = -1, int SourceLength = 0, string Preview = "");
public sealed class GlobalSearchReplacementPreview
{
    internal GlobalSearchReplacementPreview(
        DocumentFile document,
        int matchCount,
        string replacementText)
    {
        Document = document;
        MatchCount = matchCount;
        OriginalText = document.Text;
        ReplacementText = replacementText;
    }

    public DocumentFile Document { get; }
    public string OriginalText { get; }
    public string ReplacementText { get; }
    public int MatchCount { get; }
    public bool Applied { get; private set; }

    public void Apply()
    {
        if (Applied) throw new InvalidOperationException("该替换预览已经应用，不能重复应用。");
        if (Document.DiskChanged()) throw new FileConflictException();
        Document.Text = ReplacementText;
        Document.Save();
        Applied = true;
    }
}
public sealed record GlobalSearchFileResult(
    string Path,
    string RelativePath,
    int MatchCount,
    string? ReplacementText,
    GlobalSearchReplacementPreview? Preview = null);
public sealed record GlobalSearchResult(
    IReadOnlyList<GlobalSearchMatch> Matches,
    IReadOnlyList<GlobalSearchFileResult> Files,
    IReadOnlyList<string> Warnings,
    int FilesScanned);
public sealed record ReadingListEntry(
    Guid Id, string Path, bool IsPinned, bool IsRead, int ProgressPercent, int SourceAnchor,
    DateTimeOffset LastOpenedAt, DateTimeOffset UpdatedAt);
public sealed record ReadingListSaveResult(DateTimeOffset SavedAt, List<string> Errors);
public enum ReadingBlockKind { Paragraph, Metadata, Heading, Code, Quote, ListItem, Separator, Table }
public sealed record ReadingBlock(
    ReadingBlockKind Kind, string Text, int SourceStart, int SourceLength,
    int DisplayStart, int Level = 0)
{
    public string? CodeLanguage { get; init; }
    // Paragraph/code blocks owned by the list item at the zero-based Level.
    public bool ListContinuation { get; init; }
}
public sealed record ReadingHeading(string Title, int SourceStart, int DisplayStart, int Level = 1);
