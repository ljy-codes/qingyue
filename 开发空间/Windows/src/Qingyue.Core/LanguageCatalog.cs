namespace Qingyue.Core;

/// <summary>Formatting and validation capabilities are deliberately independent.</summary>
public sealed record LanguageDefinition(
    TextKind Kind, string DisplayName, IReadOnlyList<string> Extensions,
    bool SupportsFormatting, bool SupportsValidation,
    string FormattingDescription, string ValidationDescription);

public static class LanguageCatalog
{
    private const string WhitespaceOnly = "仅可显式整理行尾空白和换行，不提供完整代码格式化；字符串、缩进和空行可能具有语义，请预览后确认。";
    private const string NoValidation = "未提供完整语法校验器。";

    public static IReadOnlyList<LanguageDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        Define(TextKind.Plain, "TXT 文本", [".txt", ".log"], true, false,
            "整理行尾空白，连续空行最多保留一行；行首 Tab 按四列制表位展开，不合并段落、不改行内空格。"),
        Define(TextKind.Markdown, "Markdown", [".md", ".markdown", ".mdown", ".mkd"], true, false,
            "温和整理明确的 ATX 标题、独立顶层列表间距和普通段落间多余空行；保护围栏、缩进代码、表格、frontmatter、链接和硬换行，存疑则保留。"),
        Define(TextKind.Json, "JSON", [".json"], true, true,
            "两空格缩进；保留重复键、键顺序、数字原文和字符串转义，直接输入的中文保持直显。",
            "严格 JSON 语法校验，不接受注释或尾随逗号；不检查重复键或业务规则。"),
        Define(TextKind.Jsonc, "JSONC", [".jsonc"], true, true,
            "两空格缩进；保留注释内容、注释顺序、尾随逗号和原始值，注释可能移到独立行。",
            "支持 //、/* */ 注释和尾随逗号的 JSON；不是 JSON5。"),
        Define(TextKind.Yaml, "YAML", [".yaml", ".yml"], false, false,
            validation: "仅检查缩进中的 Tab，不进行完整 YAML 语法校验。"),
        Define(TextKind.Ini, "INI / 环境配置", [".ini", ".cfg", ".conf", ".properties", ".env"]),
        Define(TextKind.Xml, "XML", [".xml", ".xaml", ".svg", ".plist", ".csproj"], true, true,
            "两空格缩进元素结构；保留混合文本、CDATA 和 xml:space 子树；不保证实体写法、属性引号等词法形式。",
            "检查 XML 良构性，禁止 DTD 和外部实体；不进行 XSD 校验。"),
        Define(TextKind.JavaScript, "JavaScript", [".js", ".mjs", ".cjs", ".jsx"]),
        Define(TextKind.TypeScript, "TypeScript", [".ts", ".mts", ".cts", ".tsx"]),
        Define(TextKind.Python, "Python", [".py", ".pyw", ".pyi"]),
        Define(TextKind.Java, "Java", [".java"]),
        Define(TextKind.CSharp, "C#", [".cs", ".csx"]),
        Define(TextKind.Sql, "SQL", [".sql"]),
        Define(TextKind.Html, "HTML", [".html", ".htm"]),
        Define(TextKind.Css, "CSS", [".css"]),
        Define(TextKind.Go, "Go", [".go"]),
        Define(TextKind.Rust, "Rust", [".rs"]),
        Define(TextKind.Shell, "Shell", [".sh", ".bash", ".zsh", ".ksh"]),
        Define(TextKind.PowerShell, "PowerShell", [".ps1", ".psm1", ".psd1"]),
        Define(TextKind.Toml, "TOML", [".toml"]),
        Define(TextKind.Csv, "CSV / TSV", [".csv", ".tsv"])
    });

    private static readonly IReadOnlyDictionary<TextKind, LanguageDefinition> ByKind =
        All.ToDictionary(item => item.Kind);
    private static readonly IReadOnlyDictionary<string, TextKind> ByExtension =
        All.SelectMany(item => item.Extensions.Select(extension => (extension, item.Kind)))
            .ToDictionary(item => item.extension, item => item.Kind, StringComparer.OrdinalIgnoreCase);

    public static LanguageDefinition Get(TextKind kind) => ByKind.TryGetValue(kind, out var item)
        ? item : throw new ArgumentOutOfRangeException(nameof(kind), "未知文本类型。");
    public static string DisplayName(TextKind kind) => Get(kind).DisplayName;
    public static string DefaultExtension(TextKind kind) => Get(kind).Extensions[0];
    public static bool CanFormat(TextKind kind) => Get(kind).SupportsFormatting;
    public static string FormatActionLabel(TextKind kind) => CanFormat(kind) ? "格式化" : "整理空白";

    internal static bool TryDetectPath(string? path, out TextKind kind)
    {
        var name = Path.GetFileName(path ?? "");
        if (name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            kind = TextKind.Ini;
            return true;
        }
        if (name.ToLowerInvariant() is ".bashrc" or ".bash_profile" or ".zshrc" or ".zprofile" or ".profile")
        {
            kind = TextKind.Shell;
            return true;
        }
        return ByExtension.TryGetValue(Path.GetExtension(path ?? ""), out kind);
    }

    private static LanguageDefinition Define(TextKind kind, string displayName, string[] extensions,
        bool formatting = false, bool validationSupported = false,
        string formattingDescription = WhitespaceOnly, string validation = NoValidation) =>
        new(kind, displayName, Array.AsReadOnly(extensions), formatting, validationSupported,
            formattingDescription, validation);
}
