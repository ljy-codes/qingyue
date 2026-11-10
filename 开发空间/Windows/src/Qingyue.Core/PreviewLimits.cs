namespace Qingyue.Core;

/// <summary>Limits formatted body text separately from embedded Markdown image payloads.</summary>
public static class PreviewLimits
{
    public const int MaximumBodyLength = 500_000;

    public static bool CanRender(string text, TextKind kind)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= MaximumBodyLength) return true;
        if (kind != TextKind.Markdown || text.Length > DocumentFile.MaximumSize) return false;
        // Count only syntactically complete inline image data. Leave fenced code, ordinary
        // URLs and arbitrary data text in the body budget. Decode limits belong to the loader.
        int bodyLength = text.Length;
        bool fenced = false;
        char fenceChar = '\0';
        int fenceLength = 0;
        for (int start = 0; start < text.Length;)
        {
            int end = text.AsSpan(start).IndexOfAny('\r', '\n');
            end = end < 0 ? text.Length : start + end;
            int next = end;
            if (next < text.Length && text[next++] == '\r' && next < text.Length && text[next] == '\n') next++;
            var line = text.AsSpan(start, end - start);
            int indent = 0;
            while (indent < line.Length && line[indent] == ' ') indent++;
            bool fenceLine = false;
            if (indent <= 3 && indent < line.Length && line[indent] is '`' or '~')
            {
                char marker = line[indent];
                int count = 0;
                while (indent + count < line.Length && line[indent + count] == marker) count++;
                fenceLine = count >= 3;
                if (fenceLine && !fenced) { fenced = true; fenceChar = marker; fenceLength = count; }
                else if (fenced && marker == fenceChar && count >= fenceLength &&
                    line[(indent + count)..].Trim().IsEmpty) fenced = false;
            }
            if (!fenced && !fenceLine)
            {
                int cursor = 0;
                while (cursor < line.Length)
                {
                    if (line[cursor] == '\\') { cursor += 2; continue; }
                    if (line[cursor] == '`')
                    {
                        int codeEnd = cursor + 1;
                        while (codeEnd < line.Length && line[codeEnd] == '`') codeEnd++;
                        int ticks = codeEnd - cursor;
                        bool closed = false;
                        for (int scan = codeEnd; scan < line.Length;)
                        {
                            if (line[scan] != '`') { scan++; continue; }
                            int endTicks = scan + 1;
                            while (endTicks < line.Length && line[endTicks] == '`') endTicks++;
                            if (endTicks - scan == ticks)
                            {
                                cursor = endTicks;
                                closed = true;
                                break;
                            }
                            scan = endTicks;
                        }
                        if (!closed) break;
                        continue;
                    }
                    if (line[cursor] != '!' || cursor + 1 >= line.Length || line[cursor + 1] != '[')
                    { cursor++; continue; }
                    int image = cursor++;
                    int altEnd = image + 2;
                    while (altEnd < line.Length && line[altEnd] != ']')
                    {
                        if (line[altEnd] == '\\') altEnd++;
                        altEnd++;
                    }
                    if (altEnd >= line.Length) break;
                    if (altEnd + 1 >= line.Length || line[altEnd + 1] != '(')
                    {
                        // Earlier candidates inside this alt share the same closing
                        // bracket. Skip them once instead of rescanning a long line.
                        cursor = altEnd + 1;
                        continue;
                    }
                    int target = altEnd + 2;
                    int last = line[target..].IndexOf(')');
                    if (last < 0) break;
                    last += target;
                    var uri = line[target..last].Trim();
                    int comma = uri.IndexOf(',');
                    if (uri.StartsWith("data:image/".AsSpan(), StringComparison.OrdinalIgnoreCase) && comma > 0 &&
                        uri[..comma].EndsWith(";base64".AsSpan(), StringComparison.OrdinalIgnoreCase))
                        bodyLength -= uri.Length - comma - 1;
                    cursor = last + 1;
                }
            }
            if (bodyLength <= MaximumBodyLength) return true;
            start = next;
        }
        return bodyLength <= MaximumBodyLength;
    }
}
