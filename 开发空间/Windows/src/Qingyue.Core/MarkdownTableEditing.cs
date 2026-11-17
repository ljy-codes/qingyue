using System.Text;

namespace Qingyue.Core;

public enum MarkdownTableAction
{
    NextCell, PreviousCell, InsertRow, DeleteRow, InsertColumn, DeleteColumn,
    AlignLeft, AlignCenter, AlignRight, InsertTable
}

/// <summary>
/// Conservative, source-preserving Markdown table commands. All positions are UTF-16 offsets.
/// Null means the caller should leave the source alone. Navigation can return a selection-only edit.
/// </summary>
public static class MarkdownTableEditing
{
    private const int MaxColumns = 32, MaxCells = 1024, MaxCharacters = 100_000;
    private readonly record struct Line(int Start, int End, int Next);
    private readonly record struct Cell(int Start, int End);
    private sealed record Row(Line Line, List<Cell> Cells, string Indent);
    private sealed record Table(List<Row> Rows)
    {
        public int Start => Rows[0].Line.Start;
        public int End => Rows[^1].Line.End;
        public int Columns => Rows[0].Cells.Count;
        // A delimiter row is syntax, not a row of data cells.
        public int CellCount => (Rows.Count - 1) * Columns;
    }

    public static MarkdownEdit? Apply(string text, int start, int length, MarkdownTableAction action)
    {
        Validate(text, start, length);
        if (!Boundary(text, start) || !Boundary(text, start + length) ||
            InCode(text, start, length)) return null;
        if (action == MarkdownTableAction.InsertTable)
            return InsertBlock(text, start, length, "| 列 1 | 列 2 |" + Newline(text, At(text, start)) +
                "| --- | --- |" + Newline(text, At(text, start)) + "|  |  |", 2, 3);

        var table = FindTable(text, start);
        if (table is null) return null;
        int rowIndex = table.Rows.FindIndex(r => start >= r.Line.Start && start <= r.Line.End);
        if (rowIndex < 0) return null;
        var row = table.Rows[rowIndex];
        int column = ColumnAt(row, start);
        var cell = row.Cells[column];
        // A caret may be on a pipe or padding; an explicit selection must stay within one raw cell.
        if (length > 0 && (start < cell.Start || start + length > cell.End)) return null;
        if (rowIndex == 1 && action is MarkdownTableAction.NextCell or MarkdownTableAction.PreviousCell
            or MarkdownTableAction.InsertRow or MarkdownTableAction.DeleteRow) return null;

        switch (action)
        {
            case MarkdownTableAction.NextCell:
                if (column + 1 < table.Columns) return Select(text, start, row.Cells[column + 1]);
                int nextRow = rowIndex == 0 ? 2 : rowIndex + 1;
                return nextRow < table.Rows.Count
                    ? Select(text, start, table.Rows[nextRow].Cells[0])
                    : AddRow(text, table, table.Rows.Count - 1, 0);
            case MarkdownTableAction.PreviousCell:
                if (column > 0) return Select(text, start, row.Cells[column - 1]);
                int previousRow = rowIndex == 2 ? 0 : rowIndex - 1;
                return previousRow >= 0 ? Select(text, start, table.Rows[previousRow].Cells[^1]) : null;
            case MarkdownTableAction.InsertRow:
                return AddRow(text, table, rowIndex == 0 ? 1 : rowIndex, column);
            case MarkdownTableAction.DeleteRow:
                return rowIndex < 2 ? null : RemoveRow(text, table, rowIndex, column);
            case MarkdownTableAction.InsertColumn:
            case MarkdownTableAction.DeleteColumn:
                return ChangeColumn(text, table, rowIndex, column, action == MarkdownTableAction.InsertColumn);
            case MarkdownTableAction.AlignLeft:
            case MarkdownTableAction.AlignCenter:
            case MarkdownTableAction.AlignRight:
                var aligned = Align(text, start, length, table.Rows[1].Cells[column], action);
                return table.End - table.Start + aligned.Text.Length - aligned.Length > MaxCharacters
                    ? null : aligned;
            default:
                return null;
        }
    }

    /// <summary>
    /// Converts a rectangular TSV selection into a new, isolated table; the first row is the header.
    /// Quoted tabs and doubled quotes are supported. Embedded newlines are refused because this
    /// reader has no HTML line-break support. HTML/entity text uses literal inline code; combinations
    /// requiring unsupported code-span delimiters are refused. Other Markdown punctuation is escaped.
    /// </summary>
    public static MarkdownEdit? FromTsv(string text, int start, int length, string tsv)
    {
        Validate(text, start, length);
        ArgumentNullException.ThrowIfNull(tsv);
        if (!Boundary(text, start) || !Boundary(text, start + length) ||
            tsv.Length == 0 || tsv.Length > MaxCharacters || length > MaxCharacters ||
            InCode(text, start, length)) return null;
        var rows = ParseTsv(tsv);
        if (rows is null) return null;
        string newline = Newline(text, At(text, start));
        var result = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) result.Append(newline);
            result.Append("| ").AppendJoin(" | ", rows[i]).Append(" |");
            if (i == 0)
                result.Append(newline).Append("| ").AppendJoin(" | ",
                    Enumerable.Repeat("---", rows[0].Count)).Append(" |");
            if (result.Length > MaxCharacters) return null;
        }
        return InsertBlock(text, start, length, result.ToString(), 2, rows[0][0].Length);
    }

    private static void Validate(string text, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (start < 0 || length < 0 || start > text.Length - length)
            throw new ArgumentOutOfRangeException(nameof(start));
    }

    private static bool Boundary(string text, int offset) =>
        SearchEngine.IsBoundary(text, offset) &&
        !(offset > 0 && offset < text.Length && text[offset - 1] == '\r' && text[offset] == '\n');

    private static MarkdownEdit Select(string text, int origin, Cell cell)
    {
        var selected = Content(text, cell);
        return new(origin, 0, "", selected.Start, selected.End - selected.Start);
    }

    private static Cell Content(string text, Cell cell)
    {
        int start = cell.Start, end = cell.End;
        while (start < end && text[start] is ' ' or '\t') start++;
        while (end > start && text[end - 1] is ' ' or '\t') end--;
        // Leave the caret between the spaces of a generated empty cell.
        return start == end ? new(cell.Start + (cell.End > cell.Start ? 1 : 0),
            cell.Start + (cell.End > cell.Start ? 1 : 0)) : new(start, end);
    }

    private static int ColumnAt(Row row, int start)
    {
        for (int i = 0; i < row.Cells.Count; i++)
            if (start <= row.Cells[i].End) return i;
        return row.Cells.Count - 1;
    }

    private static MarkdownEdit? AddRow(string text, Table table, int after, int column)
    {
        if (table.CellCount + table.Columns > MaxCells) return null;
        var anchor = table.Rows[after];
        string newline = Newline(text, anchor.Line);
        string value = newline + table.Rows[0].Indent + "| " +
            string.Join(" | ", Enumerable.Repeat("", table.Columns)) + " |";
        if (table.End - table.Start + value.Length > MaxCharacters) return null;
        int position = anchor.Line.End;
        var row = ParseRow(value, At(value, newline.Length))!;
        var cell = Content(value, row.Cells[column]);
        return new(position, 0, value, position + cell.Start, cell.End - cell.Start);
    }

    private static MarkdownEdit RemoveRow(string text, Table table, int rowIndex, int column)
    {
        var row = table.Rows[rowIndex];
        int begin = row.Line.Start, end = row.Line.Next;
        int targetRow;
        if (rowIndex + 1 < table.Rows.Count) targetRow = rowIndex + 1;
        else
        {
            targetRow = rowIndex > 2 ? rowIndex - 1 : 0;
            // At EOF remove the preceding newline rather than invent a trailing one.
            if (row.Line.Next == row.Line.End) begin = table.Rows[rowIndex - 1].Line.End;
        }
        var selected = Content(text, table.Rows[targetRow].Cells[column]);
        int selection = selected.Start >= end ? selected.Start - (end - begin) : selected.Start;
        return new(begin, end - begin, "", selection, selected.End - selected.Start);
    }

    private static MarkdownEdit? ChangeColumn(string text, Table table, int rowIndex, int column, bool insert)
    {
        int columns = table.Columns + (insert ? 1 : -1);
        if (columns < 1 || columns > MaxColumns || (table.Rows.Count - 1) * columns > MaxCells) return null;
        var result = new StringBuilder();
        int targetStart = 0;
        int targetRow = rowIndex == 1 ? 0 : rowIndex;
        for (int i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            if (i == targetRow) targetStart = result.Length;
            result.Append(text.AsSpan(row.Line.Start, row.Cells[0].Start - row.Line.Start));
            var cells = row.Cells.Select(c => text[c.Start..c.End]).ToList();
            if (insert) cells.Insert(column + 1, i == 1 ? " --- " : "  ");
            else cells.RemoveAt(column);
            result.AppendJoin('|', cells);
            result.Append(text.AsSpan(row.Cells[^1].End, row.Line.End - row.Cells[^1].End));
            if (i + 1 < table.Rows.Count)
                result.Append(text.AsSpan(row.Line.End, row.Line.Next - row.Line.End));
            if (result.Length > MaxCharacters) return null;
        }
        string replacement = result.ToString();
        var changed = ParseRow(replacement, At(replacement, targetStart))!;
        int targetColumn = insert ? column + 1 : Math.Min(column, columns - 1);
        var selected = Content(replacement, changed.Cells[targetColumn]);
        return Minimal(text, table.Start, table.End - table.Start, replacement,
            table.Start + selected.Start, selected.End - selected.Start);
    }

    private static MarkdownEdit Align(string text, int start, int length, Cell cell, MarkdownTableAction action)
    {
        var content = Content(text, cell);
        string old = text[content.Start..content.End];
        string dashes = old.Trim(':');
        string value = action switch
        {
            MarkdownTableAction.AlignLeft => ":" + dashes,
            MarkdownTableAction.AlignCenter => ":" + dashes + ":",
            _ => dashes + ":"
        };
        int Map(int position) => position < content.Start ? position :
            position >= content.End ? position + value.Length - old.Length :
            content.Start + Math.Min(position - content.Start, value.Length);
        return Minimal(text, content.Start, old.Length, value, Map(start), Map(start + length) - Map(start));
    }

    // Trim identical source on both sides, keeping replacement and selection boundaries intact.
    private static MarkdownEdit Minimal(string text, int start, int length, string value,
        int selection, int selectionLength)
    {
        int prefix = 0, suffix = 0;
        while (prefix < length && prefix < value.Length && text[start + prefix] == value[prefix]) prefix++;
        while (prefix > 0 && (!Boundary(text, start + prefix) || !Boundary(value, prefix))) prefix--;
        while (suffix < length - prefix && suffix < value.Length - prefix &&
            text[start + length - suffix - 1] == value[value.Length - suffix - 1]) suffix++;
        while (suffix > 0 && (!Boundary(text, start + length - suffix) ||
            !Boundary(value, value.Length - suffix))) suffix--;
        return new(start + prefix, length - prefix - suffix, value[prefix..(value.Length - suffix)],
            selection, selectionLength);
    }

    private static MarkdownEdit? InsertBlock(string text, int start, int length, string table,
        int selection, int selectionLength)
    {
        if (length > MaxCharacters || table.Length > MaxCharacters) return null;
        var first = At(text, start);
        var last = At(text, start + length);
        // Avoid nested or partially overwritten tables, including malformed pipe rows.
        for (var line = first; ; line = ReadLine(text, line.Next))
        {
            if (text.AsSpan(line.Start, line.End - line.Start).Contains('|')) return null;
            if (line.Next > last.Start || line.Next == line.End) break;
        }
        string newline = Newline(text, first);
        int before = 0, after = 0;
        for (int i = start; i > 0 && before < 2;)
        {
            if (text[i - 1] == '\n') { i--; if (i > 0 && text[i - 1] == '\r') i--; before++; }
            else if (text[i - 1] == '\r') { i--; before++; }
            else break;
        }
        for (int i = start + length; i < text.Length && after < 2;)
        {
            if (text[i] == '\r') { i++; if (i < text.Length && text[i] == '\n') i++; after++; }
            else if (text[i] == '\n') { i++; after++; }
            else break;
        }
        string prefix = start == 0 ? "" : string.Concat(Enumerable.Repeat(newline, 2 - before));
        string suffix = start + length == text.Length ? "" : string.Concat(Enumerable.Repeat(newline, 2 - after));
        string value = prefix + table + suffix;
        return value.Length > MaxCharacters ? null :
            new(start, length, value, start + prefix.Length + selection, selectionLength);
    }

    private static Table? FindTable(string text, int position)
    {
        var current = At(text, position);
        var row = ParseRow(text, current);
        if (row is null) return null;
        Line header = current;
        var next = current.Next > current.End ? ParseRow(text, ReadLine(text, current.Next)) : null;
        if (next is null || !Separator(text, next))
        {
            int count = 0;
            while (!Separator(text, row))
            {
                if (row.Line.Start == 0 || ++count > MaxCells ||
                    position - row.Line.Start > MaxCharacters) return null;
                row = ParseRow(text, At(text, row.Line.Start - 1));
                if (row is null) return null;
            }
            if (row.Line.Start == 0) return null;
            header = At(text, row.Line.Start - 1);
        }
        var head = ParseRow(text, header);
        if (head is null || Separator(text, head) || InCode(text, header.Start, 0) ||
            header.Next == header.End) return null;
        var separator = ParseRow(text, ReadLine(text, header.Next));
        if (separator is null || !Separator(text, separator) ||
            head.Cells.Count != separator.Cells.Count || head.Indent != separator.Indent) return null;
        var rows = new List<Row> { head, separator };
        var lineAfter = separator.Line;
        while (lineAfter.Next > lineAfter.End && lineAfter.Next < text.Length)
        {
            var line = ReadLine(text, lineAfter.Next);
            var body = ParseRow(text, line);
            if (body is null)
            {
                // A pipe-looking continuation which cannot be parsed is incomplete/over budget.
                if (text.AsSpan(line.Start, line.End - line.Start).Contains('|')) return null;
                break;
            }
            if (body.Cells.Count != head.Cells.Count || body.Indent != head.Indent ||
                Separator(text, body)) return null;
            rows.Add(body);
            if ((rows.Count - 1) * head.Cells.Count > MaxCells || line.End - header.Start > MaxCharacters) return null;
            lineAfter = line;
        }
        var table = new Table(rows);
        return position <= table.End && table.End - table.Start <= MaxCharacters ? table : null;
    }

    private static Row? ParseRow(string text, Line line)
    {
        if (line.End - line.Start > MaxCharacters) return null;
        int first = line.Start, end = line.End;
        while (first < end && text[first] == ' ') first++;
        if (first - line.Start > 3 || first == end || text[first] == '\t') return null;
        while (end > first && text[end - 1] is ' ' or '\t') end--;
        // Match equal-length backtick runs in linear time; an unmatched run is literal text.
        var runs = new List<(int Start, int End)>();
        for (int i = first; i < end;)
        {
            if (text[i] != '`') { i++; continue; }
            int begin = i++;
            while (i < end && text[i] == '`') i++;
            runs.Add((begin, i));
        }
        var nextRun = new Dictionary<int, int>();
        var closing = new Dictionary<int, int>();
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            var run = runs[i];
            if (nextRun.TryGetValue(run.End - run.Start, out int match)) closing[run.Start] = match;
            nextRun[run.End - run.Start] = run.End;
        }
        var pipes = new List<int>();
        for (int i = first; i < end; i++)
        {
            if (text[i] == '\\') { if (i + 1 < end) i++; continue; }
            if (text[i] == '`')
            {
                int runStart = i;
                while (i + 1 < end && text[i + 1] == '`') i++;
                if (closing.TryGetValue(runStart, out int close)) i = close - 1;
            }
            else if (text[i] == '|')
            {
                pipes.Add(i);
                if (pipes.Count > MaxColumns + 1) return null;
            }
        }
        if (pipes.Count == 0) return null;
        bool leading = pipes[0] == first, trailing = pipes[^1] == end - 1;
        int beginCell = leading ? first + 1 : first;
        var cells = new List<Cell>();
        foreach (int pipe in pipes)
        {
            if (leading && pipe == first) continue;
            cells.Add(new(beginCell, pipe));
            beginCell = pipe + 1;
        }
        if (!trailing) cells.Add(new(beginCell, line.End));
        if (cells.Count is < 1 or > MaxColumns) return null;
        return new(line, cells, text[line.Start..first]);
    }

    private static bool Separator(string text, Row row)
    {
        foreach (var cell in row.Cells)
        {
            var content = text.AsSpan(cell.Start, cell.End - cell.Start).Trim();
            if (content.Length > 0 && content[0] == ':') content = content[1..];
            if (content.Length > 0 && content[^1] == ':') content = content[..^1];
            if (content.Length < 3) return false;
            foreach (char c in content) if (c != '-') return false;
        }
        return true;
    }

    private static Line At(string text, int position)
    {
        // Internal backwards traversal can land on LF; treat the entire CRLF as one break.
        if (position > 0 && position < text.Length && text[position] == '\n' && text[position - 1] == '\r')
            position--;
        int start = position;
        while (start > 0 && text[start - 1] is not '\r' and not '\n') start--;
        return ReadLine(text, start);
    }

    private static Line ReadLine(string text, int start)
    {
        int end = start;
        while (end < text.Length && text[end] is not '\r' and not '\n') end++;
        int next = end;
        if (next < text.Length && text[next++] == '\r' && next < text.Length && text[next] == '\n') next++;
        return new(start, end, next);
    }

    private static string Newline(string text, Line line)
    {
        if (line.Next > line.End) return text[line.End..line.Next];
        for (int i = line.Start - 1; i >= 0; i--)
        {
            if (text[i] == '\n') return i > 0 && text[i - 1] == '\r' ? "\r\n" : "\n";
            if (text[i] == '\r') return "\r";
        }
        return "\n";
    }

    // Scan context without copying or allocating an array for the whole document.
    // This is intended for commands, not for probing on every selection-change event.
    private static bool InCode(string text, int start, int length)
    {
        char fence = '\0';
        int fenceLength = 0, selectionEnd = length == 0 ? start : start + length - 1;
        for (var line = ReadLine(text, 0); ; line = ReadLine(text, line.Next))
        {
            int first = line.Start, indent = 0;
            while (first < line.End && text[first] is ' ' or '\t')
            { indent += text[first++] == '\t' ? 4 - indent % 4 : 1; }
            bool code = fenceLength > 0 || indent >= 4;
            if (indent < 4 && first < line.End && text[first] is '`' or '~')
            {
                char marker = text[first];
                int runEnd = first + 1;
                while (runEnd < line.End && text[runEnd] == marker) runEnd++;
                int runLength = runEnd - first;
                var rest = text.AsSpan(runEnd, line.End - runEnd);
                if (fenceLength > 0 && marker == fence && runLength >= fenceLength && rest.Trim().IsEmpty)
                { code = true; fenceLength = 0; }
                else if (fenceLength == 0 && runLength >= 3 && (marker != '`' || !rest.Contains('`')))
                { code = true; fence = marker; fenceLength = runLength; }
            }
            if (code && line.Start <= selectionEnd && (line.Next > start || line.End == text.Length && start == line.End))
                return true;
            if (line.Next > selectionEnd || line.Next == line.End) return false;
        }
    }

    private static List<List<string>>? ParseTsv(string tsv)
    {
        var rows = new List<List<string>>();
        int position = 0, columns = 0, cells = 0, encodedCharacters = 0;
        while (position < tsv.Length)
        {
            var row = new List<string>();
            bool moreCells;
            do
            {
                bool quoted = position < tsv.Length && tsv[position] == '"';
                if (quoted) position++;
                var field = new StringBuilder();
                bool closed = !quoted;
                while (position < tsv.Length)
                {
                    char c = tsv[position];
                    if (quoted && c == '"')
                    {
                        position++;
                        if (position < tsv.Length && tsv[position] == '"')
                        { field.Append('"'); position++; continue; }
                        closed = true;
                        break;
                    }
                    if (!quoted && c is '\t' or '\r' or '\n') break;
                    if (!quoted && c == '"') return null;
                    field.Append(c);
                    position++;
                }
                if (!closed || position < tsv.Length && tsv[position] is not '\t' and not '\r' and not '\n') return null;
                string? encoded = EncodeCell(field.ToString());
                if (encoded is null) return null;
                row.Add(encoded);
                encodedCharacters += encoded.Length;
                if (++cells > MaxCells || row.Count > MaxColumns || encodedCharacters > MaxCharacters) return null;
                moreCells = position < tsv.Length && tsv[position] == '\t';
                if (moreCells) position++;
            } while (moreCells);
            if (columns == 0) columns = row.Count;
            if (row.Count != columns) return null;
            rows.Add(row);
            if (position < tsv.Length)
            {
                if (tsv[position++] == '\r' && position < tsv.Length && tsv[position] == '\n') position++;
            }
        }
        return rows.Count == 0 ? null : rows;
    }

    private static string? EncodeCell(string source)
    {
        bool literal = source.IndexOfAny(['<', '>', '&']) >= 0;
        // The reader supports single-backtick spans, not arbitrary code-span delimiter lengths.
        if (literal && source.Contains('`')) return null;
        int trailingSlashes = 0;
        for (int i = source.Length - 1; i >= 0 && source[i] == '\\'; i--) trailingSlashes++;
        if (literal && trailingSlashes % 2 != 0) return null;
        var value = new StringBuilder();
        if (literal) value.Append('`');
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c is '\r' or '\n') return null;
            if (char.IsSurrogate(c))
            {
                if (!char.IsHighSurrogate(c) || i + 1 == source.Length || !char.IsLowSurrogate(source[i + 1]))
                    return null;
                value.Append(c).Append(source[++i]);
                continue;
            }
            if (char.IsControl(c) && c != '\t') return null;
            switch (c)
            {
                case '\t': value.Append("    "); break;
                case '\\': case '|': case '`': case '!': case '[': case ']':
                case '*': case '_': case '~':
                    if (!literal) value.Append('\\');
                    value.Append(c);
                    break;
                default: value.Append(c); break;
            }
            if (value.Length > MaxCharacters) return null;
        }
        if (literal) value.Append('`');
        return value.ToString();
    }
}
