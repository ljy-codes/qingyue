namespace Qingyue.Core;

public static class AnnotationAnchorResolver
{
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

        if (oldText == newText)
            return annotations.ToArray();

        ReadingModel model = ReadingModel.Build(newText, kind);
        var results = new List<DocumentAnnotation>(annotations.Count);
        foreach (var item in annotations)
        {
            int mappedStart = Math.Clamp(
                TextAnchorMapper.Map(oldText, newText, item.SourceStart), 0, newText.Length);
            int mappedEnd = Math.Clamp(
                TextAnchorMapper.Map(oldText, newText, item.SourceEnd), mappedStart, newText.Length);

            var matches = Occurrences(model.VisibleText, item.Quote);
            var contextualMatches = matches
                .Where(match => ContextMatches(model.VisibleText, match, item))
                .ToArray();

            if (contextualMatches.Length == 1)
            {
                results.Add(Update(item, model, contextualMatches[0], repairedAt,
                    mappedStart, mappedEnd));
                continue;
            }

            if (matches.Count == 1)
            {
                results.Add(Update(item, model, matches[0], repairedAt,
                    mappedStart, mappedEnd));
                continue;
            }

            // A mapped range is only trusted when it is unique in the visible
            // text. This prevents a repeated quote from silently moving to the
            // first equally valid occurrence.
            string mappedQuote = VisibleAt(model, mappedStart, mappedEnd);
            if (matches.Count > 1 && string.Equals(mappedQuote, item.Quote, StringComparison.Ordinal))
            {
                results.Add(item with
                {
                    SourceStart = mappedStart,
                    SourceEnd = mappedEnd,
                    AnchorState = AnnotationAnchorState.Uncertain,
                    UpdatedAt = repairedAt
                });
                continue;
            }

            results.Add(item with
            {
                SourceStart = mappedStart,
                SourceEnd = mappedEnd,
                AnchorState = matches.Count == 0
                    ? AnnotationAnchorState.Missing
                    : AnnotationAnchorState.Uncertain,
                UpdatedAt = repairedAt
            });
        }

        return results;
    }

    private static DocumentAnnotation Update(
        DocumentAnnotation item,
        ReadingModel model,
        int displayStart,
        DateTimeOffset repairedAt,
        int mappedStart,
        int mappedEnd)
    {
        int sourceStart = Math.Clamp(model.DisplayToSource(displayStart), 0, int.MaxValue);
        int sourceEnd = Math.Clamp(
            model.DisplayToSource(displayStart + item.Quote.Length),
            sourceStart,
            int.MaxValue);
        bool unchangedRange = sourceStart == mappedStart && sourceEnd == mappedEnd;

        return item with
        {
            SourceStart = sourceStart,
            SourceEnd = sourceEnd,
            AnchorState = unchangedRange
                ? AnnotationAnchorState.Exact
                : AnnotationAnchorState.Shifted,
            UpdatedAt = repairedAt
        };
    }

    private static bool ContextMatches(string visibleText, int start, DocumentAnnotation item)
    {
        if (item.ContextBefore.Length > 0
            && (start < item.ContextBefore.Length
                || !visibleText.AsSpan(0, start).EndsWith(item.ContextBefore.AsSpan(),
                    StringComparison.Ordinal)))
            return false;

        int end = start + item.Quote.Length;
        return item.ContextAfter.Length == 0
            || end <= visibleText.Length - item.ContextAfter.Length
                && visibleText.AsSpan(end).StartsWith(item.ContextAfter.AsSpan(),
                    StringComparison.Ordinal);
    }

    private static string VisibleAt(ReadingModel model, int sourceStart, int sourceEnd)
    {
        int start = model.SourceToDisplay(sourceStart);
        int end = model.SourceToDisplay(sourceEnd);
        if (end <= start && sourceEnd > sourceStart)
            end = Math.Min(model.VisibleText.Length, start + sourceEnd - sourceStart);
        start = Math.Clamp(start, 0, model.VisibleText.Length);
        end = Math.Clamp(end, start, model.VisibleText.Length);
        return model.VisibleText[start..end];
    }

    private static List<int> Occurrences(string text, string quote)
    {
        var result = new List<int>(2);
        if (quote.Length == 0) return result;

        int cursor = 0;
        while (cursor <= text.Length - quote.Length)
        {
            int found = text.IndexOf(quote, cursor, StringComparison.Ordinal);
            if (found < 0) break;
            result.Add(found);
            if (result.Count == 2) break;
            cursor = found + Math.Max(1, quote.Length);
        }
        return result;
    }
}
