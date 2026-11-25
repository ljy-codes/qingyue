using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Qingyue.Core;

public readonly record struct SearchProgress(int CompletedUtf16, int TotalUtf16)
{
    public double Fraction => TotalUtf16 <= 0
        ? 1
        : Math.Clamp((double)CompletedUtf16 / TotalUtf16, 0, 1);
}

public static class SearchEngine
{
    public const int MaximumTextLength = 50_000_000;
    public const int ProgressChunkSize = 1_048_576;
    private const int MaximumQueryLength = 4096;
    private const int MaximumReplacementLength = 1_000_000;
    public const int MaximumMatchCount = 100_000;
    private const int MaximumResultLength = 10_000_000;
    private static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromMilliseconds(350);

    public static IReadOnlyList<SearchMatch> Find(string text, string query, string replacement, SearchOptions options)
        => FindWithProgress(text, query, replacement, options);

    public static IReadOnlyList<SearchMatch> FindWithProgress(
        string text,
        string query,
        string replacement,
        SearchOptions options,
        Action<SearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateTextLength(text.Length);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(options);
        if (query.Length > MaximumQueryLength) throw new ArgumentException("搜索内容不能超过 4096 个 UTF-16 单元。");
        if (replacement.Length > MaximumReplacementLength) throw new ArgumentException("替换内容不能超过 100 万个 UTF-16 单元。");
        if (query.Length == 0) return Array.Empty<SearchMatch>();
        cancellationToken.ThrowIfCancellationRequested();

        var found = new List<SearchMatch>();
        long resultLength = text.Length;

        void Add(int start, int length, string value)
        {
            if (found.Count == MaximumMatchCount) throw new InvalidOperationException("匹配数量超过 10 万，请缩小搜索范围。");
            resultLength += (long)value.Length - length;
            if (resultLength > MaximumResultLength) throw new InvalidOperationException("替换结果超过 1000 万个 UTF-16 单元。");
            found.Add(new(start, length, value));
        }
        void Report(int completed) =>
            progress?.Invoke(new SearchProgress(Math.Clamp(completed, 0, text.Length), text.Length));

        Report(0);
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        TimeSpan limit = TimeLimitFor(text.Length);
        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > limit)
                throw new InvalidOperationException(
                    $"查找耗时超过 {limit.TotalMilliseconds:0} 毫秒，请简化条件或缩小文件后再试。");
        }

        if (!options.Regex)
        {
            var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int overlap = Math.Max(0, query.Length - 1);
            int chunkStart = 0;
            while (chunkStart < text.Length)
            {
                CheckBudget();
                int chunkEnd = Math.Min(text.Length, chunkStart + ProgressChunkSize);
                int extendedEnd = Math.Min(text.Length, chunkEnd + overlap);
                int searchStart = chunkStart;
                int iterations = 0;
                while (searchStart <= extendedEnd - query.Length)
                {
                    if ((iterations++ & 255) == 0) CheckBudget();
                    int start = text.IndexOf(query, searchStart, extendedEnd - searchStart, comparison);
                    if (start < 0 || start >= chunkEnd) break;
                    if (IsBoundary(text, start) && IsBoundary(text, start + query.Length) &&
                        (!options.WholeWord || IsWholeWord(text, start, query.Length)))
                        Add(start, query.Length, replacement);
                    searchStart = start + query.Length;
                }
                chunkStart = chunkEnd;
                Report(chunkStart);
                cancellationToken.ThrowIfCancellationRequested();
            }
            Report(text.Length);
            return found.AsReadOnly();
        }

        try
        {
            var flags = RegexOptions.CultureInvariant;
            if (!options.CaseSensitive) flags |= RegexOptions.IgnoreCase;
            var regex = new Regex(ScalarAwarePattern(query), flags, limit);
            var groupNames = regex.GetGroupNames().ToHashSet(StringComparer.Ordinal);
            var groupNumbers = regex.GetGroupNumbers();
            foreach (int number in groupNumbers) groupNames.Add(number.ToString(CultureInfo.InvariantCulture));
            int lastGroupNumber = groupNumbers[^1];
            int nextProgress = ProgressChunkSize;
            int matchCount = 0;
            foreach (Match match in regex.Matches(text))
            {
                if ((matchCount++ & 255) == 0) CheckBudget();
                if (!IsBoundary(text, match.Index) || !IsBoundary(text, match.Index + match.Length)) continue;
                if (options.WholeWord && !IsWholeWord(text, match.Index, match.Length)) continue;
                Add(match.Index, match.Length, ExpandReplacement(text, match, replacement, groupNames, lastGroupNumber));
                int completed = match.Index + match.Length;
                if (completed >= nextProgress)
                {
                    Report(completed);
                    while (nextProgress <= completed) nextProgress += ProgressChunkSize;
                }
            }
        }
        catch (RegexMatchTimeoutException error)
        {
            throw new InvalidOperationException(
                $"正则搜索超时（{limit.TotalMilliseconds:0} 毫秒），请简化表达式或缩小文本。", error);
        }
        catch (ArgumentException error)
        {
            throw new ArgumentException("正则表达式无效或替换内容不合法：" + error.Message, error);
        }
        CheckBudget();
        Report(text.Length);
        return found.AsReadOnly();
    }

    public static string ReplaceAll(string text, IReadOnlyList<SearchMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(matches);
        // Snapshot and validate the entire input before writing any output.
        var snapshot = matches.ToArray();
        if (snapshot.Length == 0 || snapshot.Length > MaximumMatchCount) return text;
        long size = text.Length;
        int cursor = 0;
        foreach (var match in snapshot)
        {
            if (match is null || match.Replacement is null || match.Start < 0 || match.Length < 0 ||
                match.Start > text.Length || match.Length > text.Length - match.Start ||
                match.Start < cursor ||
                !IsBoundary(text, match.Start) || !IsBoundary(text, match.Start + match.Length))
                return text;
            size += (long)match.Replacement.Length - match.Length;
            if (size > MaximumResultLength) return text;
            cursor = match.Start + match.Length;
        }
        var result = new StringBuilder((int)size);
        cursor = 0;
        foreach (var match in snapshot)
        {
            result.Append(text.AsSpan(cursor, match.Start - cursor));
            result.Append(match.Replacement);
            cursor = match.Start + match.Length;
        }
        result.Append(text.AsSpan(cursor));
        return result.ToString();
    }

    public static void ValidateTextLength(int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length > MaximumTextLength)
            throw new ArgumentException("当前查找最多处理 50,000,000 个文本单元，请缩小文件后再试。");
    }

    public static TimeSpan TimeLimitFor(int textLength) =>
        textLength > 20_000_000 ? TimeSpan.FromSeconds(4)
        : textLength > 5_000_000 ? TimeSpan.FromSeconds(2)
        : DefaultTimeLimit;

    internal static bool IsBoundary(string text, int offset) =>
        offset == 0 || offset == text.Length || !char.IsHighSurrogate(text[offset - 1]) || !char.IsLowSurrogate(text[offset]);

    /// <summary>
    /// macOS NSRegularExpression treats a supplementary Unicode character as one
    /// scalar for "." and character classes. .NET regex otherwise sees its two
    /// UTF-16 surrogate code units separately. Preserve the public expression
    /// while making an unescaped dot consume a complete surrogate pair.
    /// </summary>
    private static string ScalarAwarePattern(string pattern)
    {
        var rewritten = new StringBuilder(pattern.Length + 32);
        bool inClass = false;
        int classPosition = 0;
        for (int index = 0; index < pattern.Length; index++)
        {
            char character = pattern[index];
            if (character == '\\' && index + 1 < pattern.Length)
            {
                rewritten.Append(character).Append(pattern[++index]);
                if (inClass) classPosition++;
                continue;
            }

            if (!inClass && character == '[')
            {
                inClass = true;
                classPosition = 0;
                rewritten.Append(character);
                continue;
            }

            if (inClass)
            {
                rewritten.Append(character);
                if (character == '^' && classPosition == 0)
                {
                    classPosition++;
                }
                else if (character == ']' && classPosition > 0)
                {
                    inClass = false;
                }
                else
                {
                    classPosition++;
                }
                continue;
            }

            if (character == '.')
                rewritten.Append(@"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|.)");
            else
                rewritten.Append(character);
        }
        return rewritten.ToString();
    }

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
            || rune.Value == '_';
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
