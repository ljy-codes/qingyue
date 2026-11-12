namespace Qingyue.Core;

public sealed record MarkdownOutlineEntry(
    int Level,
    string Title,
    int SourceStart,
    int SourceLength,
    int DisplayStart);

public static class MarkdownOutline
{
    public static IReadOnlyList<MarkdownOutlineEntry> Build(string text, ReadingModel model)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(model);

        var result = new List<MarkdownOutlineEntry>();
        foreach (var heading in model.Headings)
        {
            if (heading.SourceStart < 0 || heading.SourceStart > text.Length) continue;
            var block = model.Blocks.FirstOrDefault(candidate =>
                candidate.Kind == ReadingBlockKind.Heading &&
                candidate.SourceStart == heading.SourceStart);
            int length = block?.SourceLength ?? Math.Min(text.Length - heading.SourceStart, Math.Max(1, heading.Title.Length));
            result.Add(new(
                Math.Clamp(heading.Level, 1, 6),
                heading.Title,
                heading.SourceStart,
                Math.Clamp(length, 0, text.Length - heading.SourceStart),
                Math.Clamp(heading.DisplayStart, 0, model.VisibleText.Length)));
        }
        return result.AsReadOnly();
    }
}
