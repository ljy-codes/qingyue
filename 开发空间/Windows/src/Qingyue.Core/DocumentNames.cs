using System.Globalization;

namespace Qingyue.Core;

/// <summary>A window's monotonically increasing, collision-free untitled document names.</summary>
public sealed class DocumentNames
{
    private const string Prefix = "新文件 ";
    private long next = 1;

    public string Next(IEnumerable<string> openTitles)
    {
        ArgumentNullException.ThrowIfNull(openTitles);
        foreach (var title in openTitles)
        {
            if (title.StartsWith(Prefix, StringComparison.Ordinal)
                && long.TryParse(title.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long number)
                && number >= next)
                next = checked(number + 1);
        }
        string result = Prefix + next.ToString(CultureInfo.InvariantCulture);
        next = checked(next + 1);
        return result;
    }
}
