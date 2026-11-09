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

public sealed record DocumentSnapshot(
    int Version, Guid Id, string? Path, string? SuggestedName, string Text,
    TextCodec Codec, LineEnding LineEnding, string BaselineText,
    TextCodec BaselineCodec, LineEnding BaselineLineEnding, string? BaselineDigest,
    bool HadBom, bool WasSaved);

public sealed record WorkspaceEntry(
    Guid Id, string? Path, bool IsReading, int SourceAnchor, int SelectionStart,
    int SelectionLength, bool RecoverDraft = true, bool AutomaticKind = true,
    TextKind Kind = TextKind.Plain);

public sealed record WorkspaceSession(int Version, List<WorkspaceEntry> Entries, Guid? ActiveId);
public sealed record RestoredDocument(WorkspaceEntry Entry, DocumentFile File);
public sealed record WorkspaceLoadResult(List<RestoredDocument> Documents, Guid? ActiveId, List<string> Warnings);
public sealed record WorkspaceSaveResult(DateTimeOffset SavedAt, List<string> Errors);

public sealed record SearchOptions(bool CaseSensitive = false, bool Regex = false, bool WholeWord = false);
public sealed record SearchMatch(int Start, int Length, string Replacement);
public enum ReadingBlockKind { Paragraph, Heading, Code, Quote, ListItem, Separator, Table }
public sealed record ReadingBlock(
    ReadingBlockKind Kind, string Text, int SourceStart, int SourceLength,
    int DisplayStart, int Level = 0);
public sealed record ReadingHeading(string Title, int SourceStart, int DisplayStart, int Level = 1);
