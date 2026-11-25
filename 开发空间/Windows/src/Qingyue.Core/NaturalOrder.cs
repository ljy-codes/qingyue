using System.Globalization;

namespace Qingyue.Core;

/// <summary>
/// Finder-style natural ordering used by the macOS workspace scanner:
/// localized, case-insensitive text with embedded decimal runs compared by value.
/// </summary>
public sealed class NaturalOrder : IComparer<string>
{
    public static NaturalOrder Instance { get; } = new();

    private static readonly CompareInfo CompareInfo = CultureInfo.CurrentCulture.CompareInfo;
    private const CompareOptions TextOptions =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

    private NaturalOrder() { }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int left = 0, right = 0;
        while (left < x.Length && right < y.Length)
        {
            if (IsAsciiDigit(x[left]) && IsAsciiDigit(y[right]))
            {
                int leftEnd = left, rightEnd = right;
                while (leftEnd < x.Length && IsAsciiDigit(x[leftEnd])) leftEnd++;
                while (rightEnd < y.Length && IsAsciiDigit(y[rightEnd])) rightEnd++;

                int leftSignificant = left;
                int rightSignificant = right;
                while (leftSignificant < leftEnd - 1 && x[leftSignificant] == '0') leftSignificant++;
                while (rightSignificant < rightEnd - 1 && y[rightSignificant] == '0') rightSignificant++;

                int leftLength = leftEnd - leftSignificant;
                int rightLength = rightEnd - rightSignificant;
                if (leftLength != rightLength) return leftLength.CompareTo(rightLength);

                int numeric = CompareInfo.Compare(
                    x, leftSignificant, leftLength,
                    y, rightSignificant, rightLength,
                    CompareOptions.Ordinal);
                if (numeric != 0) return numeric;

                left = leftEnd;
                right = rightEnd;
                continue;
            }

            int leftEndText = left;
            int rightEndText = right;
            while (leftEndText < x.Length && !IsAsciiDigit(x[leftEndText])) leftEndText++;
            while (rightEndText < y.Length && !IsAsciiDigit(y[rightEndText])) rightEndText++;
            int text = CompareInfo.Compare(
                x, left, leftEndText - left,
                y, right, rightEndText - right,
                TextOptions);
            if (text != 0) return text;
            left = leftEndText;
            right = rightEndText;
        }

        if (left < x.Length) return 1;
        if (right < y.Length) return -1;
        return CompareInfo.Compare(x, y, TextOptions);
    }

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
}
