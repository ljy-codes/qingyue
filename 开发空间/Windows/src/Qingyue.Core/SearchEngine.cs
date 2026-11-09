using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Qingyue.Core;

public static class SearchEngine
{
    public const int MaximumTextLength = 5_000_000;
    private const int MaximumQueryLength = 4096;
    private const int MaximumReplacementLength = 1_000_000;
    private const int MaximumMatchCount = 100_000;
    private const int MaximumResultLength = 10_000_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(350);

    public static IReadOnlyList<SearchMatch> Find(string text, string query, string replacement, SearchOptions options)
    {
        ValidateText(text);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(options);
        if (query.Length > MaximumQueryLength) throw new ArgumentException("搜索内容不能超过 4096 个 UTF-16 单元。");
        if (replacement.Length > MaximumReplacementLength) throw new ArgumentException("替换内容不能超过 100 万个 UTF-16 单元。");
        if (query.Length == 0) return Array.Empty<SearchMatch>();
        var found = new List<SearchMatch>();
        long resultLength = text.Length;

        void Add(int start, int length, string value)
        {
            if (found.Count == MaximumMatchCount) throw new InvalidOperationException("匹配数量超过 10 万，请缩小搜索范围。");
            resultLength += (long)value.Length - length;
            if (resultLength > MaximumResultLength) throw new InvalidOperationException("替换结果超过 1000 万个 UTF-16 单元。");
            found.Add(new(start, length, value));
        }

        if (!options.Regex)
        {
            var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int cursor = 0;
            while (cursor <= text.Length - query.Length)
            {
                int start = text.IndexOf(query, cursor, comparison);
                if (start < 0) break;
                if (IsBoundary(text, start) && IsBoundary(text, start + query.Length) &&
                    (!options.WholeWord || IsWholeWord(text, start, query.Length)))
                    Add(start, query.Length, replacement);
                cursor = start + query.Length;
            }
        }
        else
        {
            try
            {
                var flags = RegexOptions.CultureInvariant;
                if (!options.CaseSensitive) flags |= RegexOptions.IgnoreCase;
                var regex = new Regex(query, flags, RegexTimeout);
                var groupNames = regex.GetGroupNames().ToHashSet(StringComparer.Ordinal);
                var groupNumbers = regex.GetGroupNumbers();
                foreach (int number in groupNumbers) groupNames.Add(number.ToString(CultureInfo.InvariantCulture));
                int lastGroupNumber = groupNumbers[^1];
                foreach (Match match in regex.Matches(text))
                {
                    if (!IsBoundary(text, match.Index) || !IsBoundary(text, match.Index + match.Length)) continue;
                    if (options.WholeWord && !IsWholeWord(text, match.Index, match.Length)) continue;
                    Add(match.Index, match.Length, ExpandReplacement(text, match, replacement, groupNames, lastGroupNumber));
                }
            }
            catch (RegexMatchTimeoutException error)
            {
                throw new InvalidOperationException("正则搜索超时（350 毫秒），请简化表达式或缩小文本。", error);
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException("正则表达式无效或替换内容不合法：" + error.Message, error);
            }
        }
        return found.AsReadOnly();
    }

    public static string ReplaceAll(string text, IReadOnlyList<SearchMatch> matches)
    {
        ValidateText(text);
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count > MaximumMatchCount) throw new ArgumentException("匹配数量不能超过 10 万。");
        // Snapshot and validate the entire input before writing any output.
        var snapshot = matches.ToArray();
        long size = text.Length;
        int previousEnd = 0, previousStart = -1;
        foreach (var match in snapshot)
        {
            if (match is null || match.Replacement is null || match.Start < 0 || match.Length < 0 ||
                match.Start > text.Length || match.Length > text.Length - match.Start ||
                match.Start < previousEnd || match.Start == previousStart ||
                !IsBoundary(text, match.Start) || !IsBoundary(text, match.Start + match.Length))
                throw new ArgumentException("替换范围无效、重叠、未按顺序排列或切断 Unicode 字符，请重新搜索。");
            if (match.Replacement.Length > MaximumReplacementLength) throw new ArgumentException("单项替换内容不能超过 100 万个 UTF-16 单元。");
            size += (long)match.Replacement.Length - match.Length;
            if (size > MaximumResultLength) throw new InvalidOperationException("替换结果超过 1000 万个 UTF-16 单元。");
            previousStart = match.Start;
            previousEnd = match.Start + match.Length;
        }
        if (snapshot.Length == 0) return text;
        var result = new StringBuilder((int)size);
        int cursor = 0;
        foreach (var match in snapshot)
        {
            result.Append(text.AsSpan(cursor, match.Start - cursor));
            result.Append(match.Replacement);
            cursor = match.Start + match.Length;
        }
        result.Append(text.AsSpan(cursor));
        return result.ToString();
    }

    private static void ValidateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumTextLength) throw new ArgumentException("文本超过搜索上限 500 万个 UTF-16 单元。");
    }

    internal static bool IsBoundary(string text, int offset) =>
        offset == 0 || offset == text.Length || !char.IsHighSurrogate(text[offset - 1]) || !char.IsLowSurrogate(text[offset]);

    private static bool IsWholeWord(string text, int start, int length) =>
        (start == 0 || !IsWordRune(text, PreviousRuneStart(text, start))) &&
        (start + length == text.Length || !IsWordRune(text, start + length));

    private static int PreviousRuneStart(string text, int offset) =>
        offset >= 2 && char.IsLowSurrogate(text[offset - 1]) && char.IsHighSurrogate(text[offset - 2]) ? offset - 2 : offset - 1;

    private static bool IsWordRune(string text, int offset)
    {
        if (!Rune.TryGetRuneAt(text, offset, out var rune)) return false;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
            or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.Format;
    }

    private static string ExpandReplacement(string text, Match match, string replacement, HashSet<string> groupNames, int lastGroupNumber)
    {
        // Expand one token at a time, bounding allocations before appending even $` / $' / $_.
        // This avoids Match.Result allocating unbounded repeated full-document substitutions.
        var result = new StringBuilder();
        void Append(ReadOnlySpan<char> part)
        {
            if ((long)result.Length + part.Length > MaximumReplacementLength)
                throw new InvalidOperationException("捕获展开后的替换内容超过 100 万个 UTF-16 单元。");
            result.Append(part);
        }
        for (int i = 0; i < replacement.Length; i++)
        {
            if (replacement[i] != '$' || i + 1 == replacement.Length)
            {
                Append(replacement.AsSpan(i, 1));
                continue;
            }
            int end = i + 2;
            var token = replacement[i + 1];
            switch (token)
            {
                case '$': Append("$"); break;
                case '&': Append(match.ValueSpan); break;
                case '`': Append(text.AsSpan(0, match.Index)); break;
                case '\'': Append(text.AsSpan(match.Index + match.Length)); break;
                case '_': Append(text); break;
                case '+': Append(match.Groups[lastGroupNumber].ValueSpan); break;
                default:
                    string? name = null;
                    if (token == '{')
                    {
                        int closing = replacement.IndexOf('}', i + 2);
                        if (closing >= 0)
                        {
                            name = replacement[(i + 2)..closing];
                            if (name.Length > 0 && name.All(character => character is >= '0' and <= '9') &&
                                int.TryParse(name, out int number))
                                name = number.ToString(CultureInfo.InvariantCulture);
                            end = closing + 1;
                        }
                    }
                    else if (token is >= '0' and <= '9')
                    {
                        while (end < replacement.Length && replacement[end] is >= '0' and <= '9') end++;
                        var digits = replacement.AsSpan(i + 1, end - i - 1);
                        if (int.TryParse(digits, out int number)) name = number.ToString(CultureInfo.InvariantCulture);
                    }
                    if (name is null || !groupNames.Contains(name))
                    {
                        Append("$");
                        continue;
                    }
                    Append(int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int groupNumber)
                        ? match.Groups[groupNumber].ValueSpan : match.Groups[name].ValueSpan);
                    break;
            }
            i = end - 1;
        }
        return result.ToString();
    }
}
