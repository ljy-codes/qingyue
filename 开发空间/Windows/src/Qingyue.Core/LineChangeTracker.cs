namespace Qingyue.Core;

public enum LineChangeKind { Unsaved, Saved }

/// <param name="Line">One-based logical line in the current editor text.</param>
/// <param name="IsDeletion">A removed run is anchored before the next line, or at the last line at EOF.</param>
public readonly record struct LineChange(int Line, LineChangeKind Kind, bool IsDeletion = false);

/// <summary>
/// Immutable comparison baselines: initial open for session-saved changes, last save for unsaved changes.
/// Safe to compare on a worker thread. CRLF/CR/LF representations are equivalent.
/// </summary>
public sealed class LineChangeTracker
{
    public string InitialBaseline { get; }
    public string SavedBaseline { get; }

    public LineChangeTracker(string initialBaseline) : this(initialBaseline, initialBaseline) { }

    private LineChangeTracker(string initialBaseline, string savedBaseline)
    {
        InitialBaseline = Normalize(initialBaseline);
        SavedBaseline = Normalize(savedBaseline);
    }

    public LineChangeTracker WithSavedBaseline(string text) => new(InitialBaseline, text);

    public IReadOnlyList<LineChange> Compare(string currentText, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string current = Normalize(currentText);
        if (current == SavedBaseline && SavedBaseline == InitialBaseline) return [];
        var unsaved = Diff(SavedBaseline, current, cancellationToken);
        var result = new SortedDictionary<int, LineChange>();
        if (!string.Equals(InitialBaseline, SavedBaseline, StringComparison.Ordinal))
        {
            var saved = Diff(InitialBaseline, SavedBaseline, cancellationToken);
            foreach (var change in saved.Changes)
            {
                int mapped = unsaved.Mapping[change.Line - 1];
                if (mapped >= 0)
                    result[mapped + 1] = new(mapped + 1, LineChangeKind.Saved, change.IsDeletion);
            }
        }
        // A current unsaved edit always wins over an already-saved mark on the same line.
        foreach (var change in unsaved.Changes) result[change.Line] = change;
        return result.Values.ToArray();
    }

    private sealed record Comparison(int[] Mapping, List<LineChange> Changes);

    private static Comparison Diff(string baseline, string current, CancellationToken token)
    {
        var before = baseline.Split('\n');
        var after = current.Split('\n');
        var mapping = new int[before.Length];
        Array.Fill(mapping, -1);
        MatchRange(0, before.Length, 0, after.Length);
        var changes = new List<LineChange>();
        int oldStart = 0, newStart = 0;
        for (int old = 0; old <= before.Length; old++)
        {
            token.ThrowIfCancellationRequested();
            if (old < before.Length && mapping[old] < 0) continue;
            int next = old == before.Length ? after.Length : mapping[old];
            if (next > newStart)
                for (int row = newStart; row < next; row++) changes.Add(new(row + 1, LineChangeKind.Unsaved));
            else if (old > oldStart)
                changes.Add(new(Math.Min(newStart + 1, after.Length), LineChangeKind.Unsaved, true));
            oldStart = old + 1;
            newStart = next + 1;
        }
        return new(mapping, changes);

        void MatchRange(int a, int aEnd, int b, int bEnd)
        {
            token.ThrowIfCancellationRequested();
            while (a < aEnd && b < bEnd && before[a] == after[b]) mapping[a++] = b++;
            while (a < aEnd && b < bEnd && before[aEnd - 1] == after[bEnd - 1]) mapping[--aEnd] = --bEnd;
            if (a == aEnd || b == bEnd) return;
            if (TryMyers(a, aEnd, b, bEnd)) return;

            // Bound worst-case diff time/memory for unrelated large documents. Patience anchors
            // retain unchanged islands; recurse into gaps rather than allocating an N*M table.
            var oldUnique = Unique(before, a, aEnd);
            var newUnique = Unique(after, b, bEnd);
            var pairs = oldUnique.Where(p => p.Value >= 0 && newUnique.TryGetValue(p.Key, out int row) && row >= 0)
                .Select(p => (Old: p.Value, New: newUnique[p.Key])).OrderBy(p => p.Old).ToArray();
            if (pairs.Length == 0) return;
            var tails = new List<int>();
            var previous = new int[pairs.Length];
            Array.Fill(previous, -1);
            for (int i = 0; i < pairs.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                int low = 0, high = tails.Count;
                while (low < high)
                {
                    int middle = (low + high) / 2;
                    if (pairs[tails[middle]].New < pairs[i].New) low = middle + 1; else high = middle;
                }
                if (low > 0) previous[i] = tails[low - 1];
                if (low == tails.Count) tails.Add(i); else tails[low] = i;
            }
            var anchors = new Stack<int>();
            for (int i = tails[^1]; i >= 0; i = previous[i]) anchors.Push(i);
            foreach (int i in anchors)
            {
                var pair = pairs[i];
                MatchRange(a, pair.Old, b, pair.New);
                mapping[pair.Old] = pair.New;
                a = pair.Old + 1;
                b = pair.New + 1;
            }
            MatchRange(a, aEnd, b, bEnd);
        }

        bool TryMyers(int a, int aEnd, int b, int bEnd)
        {
            int n = aEnd - a, m = bEnd - b, limit = Math.Min(n + m, 256), origin = limit + 1;
            if (Math.Abs(n - m) > limit) return false;
            var frontier = new int[2 * limit + 3];
            var trace = new List<int[]>();
            for (int depth = 0; depth <= limit; depth++)
            {
                token.ThrowIfCancellationRequested();
                for (int diagonal = -depth; diagonal <= depth; diagonal += 2)
                {
                    int at = origin + diagonal;
                    int x = diagonal == -depth || (diagonal != depth && frontier[at - 1] < frontier[at + 1])
                        ? frontier[at + 1] : frontier[at - 1] + 1;
                    int y = x - diagonal;
                    while (x < n && y < m && before[a + x] == after[b + y]) { x++; y++; }
                    frontier[at] = x;
                    if (x < n || y < m) continue;
                    // Trace stores the completed frontier from each preceding edit depth.
                    for (int d = depth; d > 0; d--)
                    {
                        var prior = trace[d - 1];
                        int k = x - y;
                        int previousK = k == -d || (k != d && prior[origin + k - 1] < prior[origin + k + 1])
                            ? k + 1 : k - 1;
                        int previousX = prior[origin + previousK], previousY = previousX - previousK;
                        while (x > previousX && y > previousY) mapping[a + --x] = b + --y;
                        x = previousX; y = previousY;
                    }
                    while (x > 0 && y > 0) mapping[a + --x] = b + --y;
                    return true;
                }
                trace.Add((int[])frontier.Clone());
            }
            return false;
        }
    }

    private static Dictionary<string, int> Unique(string[] lines, int start, int end)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = start; i < end; i++)
            if (!result.TryAdd(lines[i], i)) result[lines[i]] = -1;
        return result;
    }

    private static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
