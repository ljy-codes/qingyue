namespace Qingyue.Core;

public static class AnnotationAnchorResolver
{
    private const int MaximumCandidates = 500;

    public static IReadOnlyList<DocumentAnnotation> Repair(
        string oldText,
        string newText,
        TextKind kind,
        IReadOnlyList<DocumentAnnotation> annotations,
        DateTimeOffset repairedAt)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        ArgumentNullException.ThrowIfNull(annotations);

        _ = oldText;
        _ = kind;
        var results = new List<DocumentAnnotation>(annotations.Count);
        foreach (var item in annotations)
            results.Add(Repair(item, newText, repairedAt));

        return results;
    }

    private static DocumentAnnotation Repair(
        DocumentAnnotation item, string source, DateTimeOffset repairedAt)
    {
        if (item.Quote.Length == 0)
            return Result(item, item.SourceStart, item.SourceEnd,
                AnnotationAnchorState.Missing, repairedAt);

        if (item.SourceStart >= 0 && item.SourceEnd <= source.Length
            && item.SourceEnd - item.SourceStart == item.Quote.Length
            && source.AsSpan(item.SourceStart, item.Quote.Length)
                .SequenceEqual(item.Quote.AsSpan()))
            return Result(item, item.SourceStart, item.SourceEnd,
                AnnotationAnchorState.Exact, repairedAt);

        var candidates = new List<(int Start, int Score)>();
        int cursor = 0;
        while (cursor <= source.Length - item.Quote.Length
            && candidates.Count < MaximumCandidates)
        {
            int found = source.IndexOf(item.Quote, cursor, StringComparison.Ordinal);
            if (found < 0) break;
            int score = ContextScore(source, found, item);
            candidates.Add((found, score));
            cursor = found + item.Quote.Length;
        }

        if (candidates.Count == 0)
            return Result(item,
                Math.Min(item.SourceStart, source.Length),
                Math.Min(item.SourceEnd, source.Length),
                AnnotationAnchorState.Missing, repairedAt);

        var best = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => Math.Abs((long)candidate.Start - item.SourceStart))
            .First();
        bool unambiguous = candidates.Count == 1
            || candidates.Count(candidate => candidate.Score == best.Score) == 1;
        return Result(item, best.Start, best.Start + item.Quote.Length,
            unambiguous ? AnnotationAnchorState.Shifted : AnnotationAnchorState.Uncertain,
            repairedAt);
    }

    private static int ContextScore(string source, int start, DocumentAnnotation item)
    {
        int score = 0;
        if (item.ContextBefore.Length > 0 && start >= item.ContextBefore.Length
            && source.AsSpan(start - item.ContextBefore.Length, item.ContextBefore.Length)
                .SequenceEqual(item.ContextBefore.AsSpan()))
            score++;
        int end = start + item.Quote.Length;
        if (item.ContextAfter.Length > 0 && end <= source.Length - item.ContextAfter.Length
            && source.AsSpan(end, item.ContextAfter.Length)
                .SequenceEqual(item.ContextAfter.AsSpan()))
            score++;
        return score;
    }

    private static DocumentAnnotation Result(
        DocumentAnnotation item,
        int sourceStart,
        int sourceEnd,
        AnnotationAnchorState state,
        DateTimeOffset repairedAt)
    {
        int start = Math.Max(0, sourceStart);
        int end = Math.Max(start, sourceEnd);
        return item with
        {
            SourceStart = start,
            SourceEnd = end,
            AnchorState = state,
            UpdatedAt = repairedAt
        };
    }
}
