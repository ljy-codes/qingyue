using System.Globalization;
using System.Text;

namespace Qingyue.Core;

public enum SequenceEventKind { Message, Note }

public sealed record SequenceParticipant(string Id, string Label, bool IsActor = false);

public sealed record SequenceEvent(string From, string To, string Text, SequenceEventKind Kind,
    bool Dashed = false, string Arrow = ">>", string Placement = "over");

public sealed record SequenceDiagram(IReadOnlyList<SequenceParticipant> Participants,
    IReadOnlyList<SequenceEvent> Events, bool AutoNumber = false);

public static class MermaidSequence
{
    public static bool TryParse(string source, out SequenceDiagram? diagram, out string? error)
    {
        var parser = new Parser();
        bool parsed = parser.Parse(source, out diagram);
        error = parser.Error;
        return parsed;
    }

    // A deliberately small, offline subset. Scans are linear and bounded by the source limit;
    // no regex, HTML parser, network access, or mutable state is shared between calls.
    private sealed class Parser
    {
        private const int MaximumSourceLength = 32_000;
        private const int MaximumLines = 1_000;
        private const int MaximumParticipants = 20;
        private const int MaximumEvents = 200;
        private const int MaximumLabelLength = 2_000;
        private static readonly string[] Arrows = ["-->>", "-->", "--x", "--)", "->>", "->", "-x", "-)"];
        private readonly List<SequenceParticipant> participants = [];
        private readonly List<SequenceEvent> events = [];
        private readonly Dictionary<string, int> participantIndexes = new(StringComparer.Ordinal);
        private readonly HashSet<string> explicitParticipants = new(StringComparer.Ordinal);
        private int lineNumber;
        private bool autoNumber;

        public string? Error { get; private set; }

        public bool Parse(string? source, out SequenceDiagram? diagram)
        {
            diagram = null;
            if (source is null || source.Length == 0) return Fail("时序图内容不能为空。");
            if (source.Length > MaximumSourceLength) return Fail($"源文本不能超过 {MaximumSourceLength} 个字符。");

            bool headerSeen = false;
            using var reader = new StringReader(source.TrimStart('\ufeff'));
            while (reader.ReadLine() is { } raw)
            {
                if (++lineNumber > MaximumLines) return Fail($"源文本不能超过 {MaximumLines} 行（含空行和注释）。");
                // Directives start with the comment prefix: reject before removing comments.
                if (raw.Contains("%%{", StringComparison.Ordinal))
                    return Fail("不支持 Mermaid 初始化或配置指令（%%{...}%%）。");
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;
                // Mermaid's TXT and alias LINE tokens retain %%. Only these non-text
                // statements may have a trailing comment in the supported subset.
                int comment = line.IndexOf("%%", StringComparison.Ordinal);
                if (comment >= 0)
                {
                    string prefix = line[..comment].TrimEnd();
                    if (Is(prefix, "sequenceDiagram") || Is(prefix, "autonumber")) line = prefix;
                }
                if (line.Contains(';'))
                    return Fail("不支持半角分号（;）及分号分隔的多语句；实体写法（如 &amp;、&#59;、#59;）也不支持，请将语句分行，文字分号可使用全角“；”。");
                if (!headerSeen)
                {
                    if (!Is(line, "sequenceDiagram")) return Fail("图必须以 sequenceDiagram 开头，仅支持时序图。");
                    headerSeen = true;
                    continue;
                }
                if (!ParseLine(line)) return false;
            }

            if (!headerSeen || participants.Count == 0) return Fail("时序图为空，需要声明参与者或添加消息、Note。");
            diagram = new(participants.AsReadOnly(), events.AsReadOnly(), autoNumber);
            return true;
        }

        private bool ParseLine(string line)
        {
            string remaining = line;
            string keyword = Word(ref remaining);
            if (Is(keyword, "participant") || Is(keyword, "actor"))
                return ParseParticipant(remaining, Is(keyword, "actor"));
            if (Is(keyword, "note")) return ParseNote(remaining);
            if (Is(keyword, "autonumber"))
            {
                if (remaining.Length != 0) return Fail("仅支持单独的 autonumber，不支持起始值、步长或 off。");
                if (events.Any(e => e.Kind == SequenceEventKind.Message))
                    return Fail("autonumber 必须位于第一条消息之前，不支持中途切换编号。");
                autoNumber = true;
                return true;
            }
            if (Is(keyword, "activate") || Is(keyword, "deactivate"))
                return Fail("不支持 activate/deactivate 激活语法。");
            return ParseMessage(line);
        }

        private bool ParseParticipant(string remaining, bool actor)
        {
            string id = Word(ref remaining);
            string label = id;
            if (remaining.Length != 0)
            {
                if (!Is(Word(ref remaining), "as")) return Fail("参与者声明格式应为 participant/actor 标识 as 别名。");
                label = remaining;
            }
            if (!Label(label, out string normalized)) return false;
            if (explicitParticipants.Contains(id)) return Fail("参与者被重复声明，请为每个参与者使用唯一标识。");
            if (!Participant(id, normalized, actor)) return false;
            explicitParticipants.Add(id);
            return true;
        }

        private bool ParseMessage(string line)
        {
            int colon = line.IndexOf(':');
            if (colon < 0) return Fail("不支持或无法识别此语法；消息应为 A->>B: 文本，仅支持参与者、消息、Note 和 autonumber。");
            string endpoints = line[..colon].Trim();
            for (int index = 0; index < endpoints.Length; index++)
            {
                if (endpoints[index] != '-') continue;
                foreach (string token in Arrows)
                {
                    if (!endpoints.AsSpan(index).StartsWith(token, StringComparison.Ordinal)) continue;
                    string from = endpoints[..index].Trim();
                    string to = endpoints[(index + token.Length)..].Trim();
                    if (to.StartsWith('+') || to.StartsWith('-'))
                        return Fail("不支持箭头后的 +/- 激活或取消激活后缀。");
                    if (!Label(line[(colon + 1)..], out string text)) return false;
                    if (!Participant(from) || !Participant(to)) return false;
                    bool dashed = token.StartsWith("--", StringComparison.Ordinal);
                    string arrow = token[(dashed ? 2 : 1)..];
                    return Add(new(from, to, text, SequenceEventKind.Message, dashed, arrow));
                }
            }
            return Fail("不支持或无法识别此语法；消息箭头仅支持 ->、->>、-->、-->>、-x、--x、-) 和 --)。");
        }

        private bool ParseNote(string remaining)
        {
            int colon = remaining.IndexOf(':');
            if (colon < 0) return Fail("Note 缺少冒号或正文，应为 Note over A,B: 文本 / Note right of A: 文本。");
            string targets = remaining[..colon].Trim();
            string placement = Word(ref targets).ToLowerInvariant();
            if (placement is "left" or "right")
            {
                if (!Is(Word(ref targets), "of")) return Fail("Note 左右位置必须写为 left of A 或 right of A。");
            }
            else if (placement != "over")
                return Fail("Note 位置仅支持 over A、over A,B、right of A 和 left of A。");

            string[] ids = targets.Split(',');
            if (ids.Length > (placement == "over" ? 2 : 1)) return Fail("Note over 最多指定两个参与者，left/right of 只能指定一个。");
            string from = ids[0].Trim();
            string to = ids[^1].Trim();
            if (!Label(remaining[(colon + 1)..], out string text)) return false;
            if (!Participant(from) || !Participant(to)) return false;
            return Add(new(from, to, text, SequenceEventKind.Note, Placement: placement));
        }

        // Explicit declarations may supply an alias/actor for an earlier implicit participant.
        // First-use order and ordinal ID identity remain stable.
        private bool Participant(string id, string? label = null, bool actor = false)
        {
            if (id.Length > MaximumLabelLength) return Fail($"角色标识不能超过 {MaximumLabelLength} 个字符。");
            if (!SafeId(id)) return Fail("角色标识不能为空，且只能包含普通 Unicode 字母、数字、组合字符、下划线、点或内部连字符。");
            if (participantIndexes.TryGetValue(id, out int index))
            {
                if (label is not null) participants[index] = new(id, label, actor);
                return true;
            }
            if (participants.Count >= MaximumParticipants) return Fail($"参与者不能超过 {MaximumParticipants} 个（含自动声明）。");
            participantIndexes.Add(id, participants.Count);
            participants.Add(new(id, label ?? id, actor));
            return true;
        }

        private bool Add(SequenceEvent value)
        {
            if (events.Count >= MaximumEvents) return Fail($"事件不能超过 {MaximumEvents} 个（含消息和 Note）。");
            events.Add(value);
            return true;
        }

        private bool Label(string raw, out string text)
        {
            text = raw.Trim();
            if (text.Length == 0) return Fail("参与者别名、消息或 Note 正文不能为空。");
            if (text.Length > MaximumLabelLength) return Fail($"单个标签或正文不能超过 {MaximumLabelLength} 个字符。");
            // Treat all other HTML as literal text; never interpret tags or decode entities.
            text = text.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        private bool Fail(string reason)
        {
            Error = lineNumber > 0 ? $"第 {lineNumber} 行：{reason}" : reason;
            return false;
        }

        private static bool Is(string value, string expected) =>
            value.Equals(expected, StringComparison.OrdinalIgnoreCase);

        private static string Word(ref string text)
        {
            text = text.TrimStart();
            int length = 0;
            while (length < text.Length && !char.IsWhiteSpace(text[length])) length++;
            string word = text[..length];
            text = text[length..].TrimStart();
            return word;
        }

        private static bool SafeId(string id)
        {
            if (id.Length == 0 || id[0] == '-' || id[^1] == '-' || id.Contains("--", StringComparison.Ordinal)) return false;
            for (int index = 0; index < id.Length;)
            {
                if (!Rune.TryGetRuneAt(id, index, out var rune)) return false;
                var category = Rune.GetUnicodeCategory(rune);
                if (!Rune.IsLetterOrDigit(rune) && rune.Value is not '.' and not '-' &&
                    category is not UnicodeCategory.NonSpacingMark and not UnicodeCategory.SpacingCombiningMark and
                    not UnicodeCategory.EnclosingMark and not UnicodeCategory.ConnectorPunctuation)
                    return false;
                index += rune.Utf16SequenceLength;
            }
            return true;
        }
    }
}
