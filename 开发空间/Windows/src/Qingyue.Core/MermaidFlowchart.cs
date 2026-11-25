namespace Qingyue.Core;

public enum MermaidFlowchartDirection
{
    TopDown,
    BottomUp,
    LeftRight,
    RightLeft
}

public enum MermaidFlowchartNodeShape
{
    Rectangle,
    Rounded,
    Diamond,
    Circle,
    Subroutine
}

public sealed record MermaidFlowchartNode(
    string Id,
    string Label,
    MermaidFlowchartNodeShape Shape);

public sealed record MermaidFlowchartEdge(
    string From,
    string To,
    string? Label,
    bool Dashed = false,
    bool Thick = false,
    bool Directed = true);

public sealed record MermaidFlowchart(
    MermaidFlowchartDirection Direction,
    IReadOnlyList<MermaidFlowchartNode> Nodes,
    IReadOnlyList<MermaidFlowchartEdge> Edges)
{
    public static bool TryParse(string? source, out MermaidFlowchart? diagram, out string? error)
    {
        var parser = new Parser();
        bool parsed = parser.Parse(source, out diagram);
        error = parser.Error;
        return parsed;
    }

    private sealed class Parser
    {
        private const int MaximumSourceLength = 32_000;
        private const int MaximumLines = 1_000;
        private const int MaximumNodes = 100;
        private const int MaximumEdges = 200;
        private const int MaximumLabelLength = 2_000;
        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "subgraph", "end", "classdef", "class", "style", "linkstyle",
            "click", "direction", "acctitle", "accdescr"
        };
        private readonly List<MermaidFlowchartNode> nodes = [];
        private readonly List<MermaidFlowchartEdge> edges = [];
        private readonly Dictionary<string, int> nodeIndexes = new(StringComparer.Ordinal);
        private int lineNumber;
        private MermaidFlowchartDirection direction;

        public string? Error { get; private set; }

        public bool Parse(string? source, out MermaidFlowchart? diagram)
        {
            diagram = null;
            if (source is null || source.Length == 0) return Fail("流程图内容不能为空。");
            if (source.Length > MaximumSourceLength)
                return Fail($"流程图源文本不能超过 {MaximumSourceLength} 个字符。");

            bool headerSeen = false;
            using var reader = new StringReader(source.TrimStart('\ufeff'));
            while (reader.ReadLine() is { } raw)
            {
                if (++lineNumber > MaximumLines)
                    return Fail($"流程图源文本不能超过 {MaximumLines} 行（含空行和注释）。");
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;
                if (line.Contains(';', StringComparison.Ordinal))
                    return Fail("不支持半角分号及分号分隔的多语句，请将流程图语句分行。");

                if (!headerSeen)
                {
                    if (!TryDirection(line, out direction))
                        return Fail("流程图必须以 flowchart 或 graph 开头，并指定方向 TD、TB、BT、LR 或 RL。");
                    headerSeen = true;
                    continue;
                }

                int firstWordEnd = line.IndexOfAny(' ', '\t');
                string firstWord = firstWordEnd < 0 ? line : line[..firstWordEnd];
                if (Reserved.Contains(firstWord))
                    return Fail("暂不支持子图、样式、交互或局部方向指令。");
                if (!ParseStatement(line)) return false;
            }

            if (!headerSeen) return Fail("流程图缺少 flowchart 或 graph 头部。");
            if (nodes.Count == 0) return Fail("流程图不能为空，需要至少一个节点。");
            diagram = new(direction, nodes.AsReadOnly(), edges.AsReadOnly());
            return true;
        }

        private bool ParseStatement(string line)
        {
            int index = 0;
            if (!TryNode(line, ref index, out var from, out bool fromDefined)) return false;
            SkipSpaces(line, ref index);
            if (index == line.Length)
                return AddNode(from, fromDefined);

            if (!TryEdge(line, ref index, out string? label, out bool dashed, out bool thick,
                out bool directed))
                return false;
            SkipSpaces(line, ref index);
            if (!TryNode(line, ref index, out var to, out bool toDefined)) return false;
            SkipSpaces(line, ref index);
            if (index != line.Length) return Fail("流程图语句格式错误；每行仅支持一个节点或一条节点连线。");
            if (!AddNode(from, fromDefined) || !AddNode(to, toDefined)) return false;
            if (edges.Count >= MaximumEdges)
                return Fail($"流程图连线不能超过 {MaximumEdges} 条。");
            edges.Add(new(from.Id, to.Id, label, dashed, thick, directed));
            return true;
        }

        private bool TryNode(string line, ref int index, out ParsedNode node, out bool defined)
        {
            node = default;
            defined = false;
            SkipSpaces(line, ref index);
            int start = index;
            while (index < line.Length)
            {
                char value = line[index];
                if (char.IsWhiteSpace(value) || value is '[' or '(' or '{') break;
                if (value is '-' or '=' && IsArrowStart(line, index)) break;
                index++;
            }
            string id = line[start..index];
            if (!SafeId(id))
                return Fail("节点标识格式错误；标识只能包含 Unicode 字母、数字、下划线、点或连字符。");

            var shape = MermaidFlowchartNodeShape.Rectangle;
            string label = id;
            if (index < line.Length && line[index] is '[' or '(' or '{')
            {
                defined = true;
                if (line.AsSpan(index).StartsWith("[/", StringComparison.Ordinal) ||
                    line.AsSpan(index).StartsWith("[\\", StringComparison.Ordinal) ||
                    line.AsSpan(index).StartsWith("([", StringComparison.Ordinal) ||
                    line.AsSpan(index).StartsWith("{{", StringComparison.Ordinal))
                    return Fail("暂不支持这种节点形状，请查看源码。");
                char opener = line[index];
                string close;
                if (opener == '[' && index + 1 < line.Length && line[index + 1] == '[')
                {
                    shape = MermaidFlowchartNodeShape.Subroutine;
                    close = "]]";
                    index += 2;
                }
                else if (opener == '(' && index + 1 < line.Length && line[index + 1] == '(')
                {
                    shape = MermaidFlowchartNodeShape.Circle;
                    close = "))";
                    index += 2;
                }
                else
                {
                    shape = opener switch
                    {
                        '[' => MermaidFlowchartNodeShape.Rectangle,
                        '(' => MermaidFlowchartNodeShape.Rounded,
                        _ => MermaidFlowchartNodeShape.Diamond
                    };
                    close = opener switch
                    {
                        '[' => "]",
                        '(' => ")",
                        _ => "}"
                    };
                    index++;
                }

                int end = line.IndexOf(close, index, StringComparison.Ordinal);
                if (end < 0) return Fail("节点标签格式错误；缺少右括号。");
                label = line[index..end];
                if (!Label(label, "节点标签")) return false;
                index = end + close.Length;
            }
            node = new(id, label, shape);
            return true;
        }

        private bool TryEdge(string line, ref int index, out string? label, out bool dashed,
            out bool thick, out bool directed)
        {
            label = null;
            dashed = false;
            thick = false;
            directed = true;
            if (StartsWith(line, index, "-.->"))
            {
                index += 4;
                dashed = true;
            }
            else if (StartsWith(line, index, "==>"))
            {
                index += 3;
                thick = true;
            }
            else if (StartsWith(line, index, "-->"))
            {
                index += 3;
            }
            else if (StartsWith(line, index, "---"))
            {
                index += 3;
                directed = false;
            }
            else
            {
                return Fail("流程图连线格式错误；仅支持 -->、---、-.-> 和 ==>。");
            }

            SkipSpaces(line, ref index);
            if (index >= line.Length || line[index] != '|') return true;
            int end = line.IndexOf('|', index + 1);
            if (end < 0) return Fail("边标签格式错误；缺少右侧 |。");
            label = line[(index + 1)..end];
            if (!Label(label, "边标签")) return false;
            index = end + 1;
            return true;
        }

        private bool AddNode(ParsedNode value, bool defined)
        {
            if (nodeIndexes.TryGetValue(value.Id, out int existing))
            {
                if (defined) nodes[existing] = new(value.Id, value.Label, value.Shape);
                return true;
            }
            if (nodes.Count >= MaximumNodes)
                return Fail($"流程图节点不能超过 {MaximumNodes} 个。");
            nodeIndexes.Add(value.Id, nodes.Count);
            nodes.Add(new(value.Id, value.Label, value.Shape));
            return true;
        }

        private bool Label(string value, string kind)
        {
            if (value.Length > MaximumLabelLength)
                return Fail($"{kind}不能超过 {MaximumLabelLength} 个字符。");
            return true;
        }

        private bool Fail(string reason)
        {
            Error = lineNumber > 0 ? $"第 {lineNumber} 行：{reason}" : reason;
            return false;
        }

        private static bool TryDirection(string line, out MermaidFlowchartDirection value)
        {
            value = default;
            string[] words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 2 ||
                !words[0].Equals("flowchart", StringComparison.OrdinalIgnoreCase) &&
                !words[0].Equals("graph", StringComparison.OrdinalIgnoreCase))
                return false;
            value = words[1].ToUpperInvariant() switch
            {
                "TD" or "TB" => MermaidFlowchartDirection.TopDown,
                "BT" => MermaidFlowchartDirection.BottomUp,
                "LR" => MermaidFlowchartDirection.LeftRight,
                "RL" => MermaidFlowchartDirection.RightLeft,
                _ => (MermaidFlowchartDirection)(-1)
            };
            return (int)value >= 0;
        }

        private static bool IsArrowStart(string text, int index) =>
            StartsWith(text, index, "-->") || StartsWith(text, index, "---") ||
            StartsWith(text, index, "-.->") || StartsWith(text, index, "==>");

        private static bool StartsWith(string text, int index, string value) =>
            index >= 0 && index + value.Length <= text.Length &&
            text.AsSpan(index, value.Length).SequenceEqual(value);

        private static void SkipSpaces(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        }

        private static bool SafeId(string id)
        {
            if (id.Length == 0 || id.Length > 2_000 || id[0] is '-' or '.' ||
                id[^1] is '-' or '.' || id.Contains("--", StringComparison.Ordinal))
                return false;
            foreach (char value in id)
                if (!char.IsLetterOrDigit(value) && value is not '_' and not '-' and not '.')
                    return false;
            return true;
        }

        private readonly record struct ParsedNode(
            string Id,
            string Label,
            MermaidFlowchartNodeShape Shape);
    }
}
