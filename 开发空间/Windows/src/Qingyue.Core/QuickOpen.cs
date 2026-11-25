using System.Globalization;
using System.Text;

namespace Qingyue.Core;

public sealed record QuickOpenItem(
    string Id,
    string Title,
    string Subtitle,
    bool IsOpen);

/// <summary>
/// Stable, platform-neutral quick-open filtering shared by the Windows picker.
/// It mirrors the macOS folding and ranking contract without walking the disk.
/// </summary>
public static class QuickOpenRanker
{
    public static IReadOnlyList<QuickOpenItem> Filter(
        IEnumerable<QuickOpenItem> source,
        string? query)
    {
        string foldedQuery = Fold(query ?? string.Empty).Trim();
        string[] terms = foldedQuery
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Fold)
            .ToArray();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ranked = new List<(QuickOpenItem Item, int Index, int Rank)>();
        int index = 0;
        foreach (var item in source)
        {
            if (!seen.Add(item.Id)) continue;
            if (terms.Length == 0)
            {
                ranked.Add((item, index++, 0));
                continue;
            }

            string title = Fold(item.Title);
            string subtitle = Fold(item.Subtitle);
            if (!terms.All(term => title.Contains(term, StringComparison.Ordinal)
                    || subtitle.Contains(term, StringComparison.Ordinal)))
            {
                index++;
                continue;
            }

            int rank =
                title == foldedQuery ? 0
                : title.StartsWith(foldedQuery, StringComparison.Ordinal) ? 1
                : title.Contains(foldedQuery, StringComparison.Ordinal) ? 2
                : terms.All(term => title.Contains(term, StringComparison.Ordinal)) ? 3
                : 4;
            ranked.Add((item, index++, rank));
        }

        return ranked
            .OrderByDescending(row => row.Item.IsOpen)
            .ThenBy(row => row.Rank)
            .ThenBy(row => row.Index)
            .Select(row => row.Item)
            .ToArray();
    }

    public static string Fold(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormKD).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (char character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
                continue;
            builder.Append(character);
        }
        return builder.ToString();
    }
}
