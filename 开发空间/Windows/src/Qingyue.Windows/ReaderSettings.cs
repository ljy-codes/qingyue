using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Qingyue.Core;

namespace Qingyue.Windows;

public sealed class ReaderSettings
{
    private const int MaximumRecentPositions = 100;
    private const int RecentPositionRetentionWindow = 80;
    private static readonly double[] ReadingWidthPresets = [520, 680, 840, 1_000_000];

    public bool Dark { get; set; } = ReaderTheme.SystemIsDark();
    public string Theme { get; set; } = "classic";
    public bool FollowSystem { get; set; } = true;
    public bool Wrap { get; set; } = true;
    public bool LineNumbers { get; set; } = true;
    public bool Sidebar { get; set; } = true;
    public bool RecoveryEnabled { get; set; } = true;
    public bool RestoreSession { get; set; } = true;
    public double FontSize { get; set; } = 14;
    public double ReadingWidth { get; set; } = 1_000_000;
    public string FontFamily { get; set; } = ReadingFontCatalog.Fallback;

    // Retained only to migrate the multiplier-based setting used before 0.19.
    public double LineHeight { get; set; } = 1.33 + 6d / 14d;
    public double LineSpacing { get; set; } = 6;
    public double ParagraphSpacing { get; set; } = 13;
    public string IconTheme { get; set; } = "Sky";
    public double Width { get; set; } = 1120;
    public double Height { get; set; } = 790;
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public List<string> Recent { get; set; } = [];
    public Dictionary<string, RecentPosition> RecentPositions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> ExcludedDraftPaths { get; set; } = [];
    public string? WorkspaceRoot { get; set; }
    public List<string> FavoriteDirectories { get; set; } = [];

    [JsonIgnore]
    public ReaderThemeKind ThemeKind => ReaderTheme.Parse(Theme, Dark);

    [JsonIgnore]
    public bool EffectiveDark => FollowSystem ? ReaderTheme.SystemIsDark()
        : ReaderTheme.IsDark(ThemeKind, Dark);

    public void ApplySystemAppearance()
    {
        Dark = FollowSystem ? ReaderTheme.SystemIsDark() : ReaderTheme.IsDark(ThemeKind, Dark);
    }

    public static ReaderSettings Load(string root)
    {
        var path = Path.Combine(root, "Settings.json");
        if (!System.IO.File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) return new();
            string json = System.IO.File.ReadAllText(path);
            var value = JsonSerializer.Deserialize<ReaderSettings>(json) ?? new();
            using var document = JsonDocument.Parse(json);
            var rootElement = document.RootElement;

            bool hasTheme = rootElement.TryGetProperty(nameof(Theme), out _);
            bool hasFollowSystem = rootElement.TryGetProperty(nameof(FollowSystem), out _);
            bool hasLineSpacing = rootElement.TryGetProperty(nameof(LineSpacing), out _);
            bool hasLineHeight = rootElement.TryGetProperty(nameof(LineHeight), out _);
            bool hasReadingWidth = rootElement.TryGetProperty(nameof(ReadingWidth), out _);
            bool legacyWarmPaper = hasTheme && IsLegacyWarmPaper(value.Theme);

            // Settings written before the seven-theme model had no FollowSystem
            // field; preserve their explicit day/night choice.
            if (!hasFollowSystem) value.FollowSystem = false;
            if (!hasTheme) value.Theme = "classic";
            // Only the pre-seven-theme WarmPaper value selects a light surface.
            // The current "coffee" identifier intentionally supports both modes.
            if (legacyWarmPaper) value.Dark = false;
            value.Theme = ReaderTheme.NormalizeName(value.Theme, value.Dark);

            value.FontSize = Number(value.FontSize, 14, 10, 30);
            // macOS starts new profiles at full width. The preferences panel's
            // restore action separately resets the visible presets to 680.
            value.ReadingWidth = hasReadingWidth
                ? NormalizePersistedReadingWidth(value.ReadingWidth)
                : 1_000_000;
            value.FontFamily = ReadingFontCatalog.Normalize(value.FontFamily);
            value.LineHeight = Number(value.LineHeight, 1.33 + 6d / value.FontSize, 1.1, 5);
            if (hasLineSpacing)
                value.LineSpacing = Number(value.LineSpacing, 6, 0, 48);
            else if (hasLineHeight)
                value.LineSpacing = Math.Clamp((value.LineHeight - 1.33) * value.FontSize, 0, 48);
            else
                value.LineSpacing = 6;
            value.ParagraphSpacing = Number(value.ParagraphSpacing, 13, 0, 48);
            value.IconTheme = AppIconTheme.NormalizeName(value.IconTheme);
            value.Width = Number(value.Width, 1120, 820, 3200);
            value.Height = Number(value.Height, 790, 520, 2160);
            value.Recent = (value.Recent ?? [])
                .Where(Path.IsPathFullyQualified)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(15)
                .ToList();
            value.RecentPositions = NormalizeRecentPositions(value.RecentPositions);
            value.ExcludedDraftPaths ??= [];
            value.WorkspaceRoot = string.IsNullOrWhiteSpace(value.WorkspaceRoot)
                ? null
                : Path.GetFullPath(value.WorkspaceRoot);
            value.FavoriteDirectories = (value.FavoriteDirectories ?? [])
                .Where(Path.IsPathFullyQualified)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList();
            value.ApplySystemAppearance();
            return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public ReadingPresentation ToPresentation() =>
        new ReadingPresentation(
            ReadingFontCatalog.Resolve(FontFamily),
            FontSize,
            LineSpacing,
            ParagraphSpacing).Normalize();

    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, "Settings.json");
        string temporary = Path.Combine(root, "Settings." + Guid.NewGuid() + ".tmp");
        try
        {
            RecentPositions = NormalizeRecentPositions(RecentPositions);
            Theme = ReaderTheme.NormalizeName(Theme, Dark);
            IconTheme = AppIconTheme.NormalizeName(IconTheme);
            LineHeight = 1.33 + LineSpacing / Math.Max(10, FontSize);
            System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            if (System.IO.File.Exists(destination))
            {
                System.IO.File.Copy(destination, destination + ".bak", overwrite: true);
                System.IO.File.Move(temporary, destination, overwrite: true);
            }
            else
            {
                System.IO.File.Move(temporary, destination);
            }
        }
        finally
        {
            if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
        }
    }

    public void ClearRecentHistory()
    {
        Recent.Clear();
        RecentPositions.Clear();
    }

    public void RememberRecentPosition(string path, RecentPosition position)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }
        if (!TryNormalizeRecentPosition(position, out var normalized))
        {
            RecentPositions.Remove(fullPath);
            return;
        }
        RecentPositions[fullPath] = normalized;
        if (RecentPositions.Count <= MaximumRecentPositions) return;
        var retained = Recent.Take(RecentPositionRetentionWindow)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        retained.Add(fullPath);
        foreach (string candidate in RecentPositions.Keys
            .Where(candidate => !retained.Contains(candidate))
            .ToArray())
            RecentPositions.Remove(candidate);
    }

    public bool MoveRecentPosition(string oldPath, string newPath)
    {
        if (!RecentPositions.TryGetValue(oldPath, out var position)) return false;
        RecentPositions.Remove(oldPath);
        if (TryFullPath(newPath, out string fullPath))
            RecentPositions[fullPath] = position;
        return true;
    }

    private static Dictionary<string, RecentPosition> NormalizeRecentPositions(
        Dictionary<string, RecentPosition>? values)
    {
        var normalized = new Dictionary<string, RecentPosition>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return normalized;
        foreach (var (path, position) in values)
        {
            if (!TryFullPath(path, out string fullPath) ||
                !TryNormalizeRecentPosition(position, out var valid))
                continue;
            normalized.TryAdd(fullPath, valid);
            if (normalized.Count >= MaximumRecentPositions) break;
        }
        return normalized;
    }

    private static bool TryFullPath(string path, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private static bool TryNormalizeRecentPosition(
        RecentPosition? position, out RecentPosition normalized)
    {
        normalized = null!;
        if (position is null ||
            position.SelectionStart < 0 || position.SelectionStart >= 100_000_000 ||
            position.SelectionLength < 0 || position.SelectionLength >= 100_000_000 ||
            position.SourceAnchor < 0 || position.SourceAnchor >= 100_000_000 ||
            position.EditorViewportAnchor < 0 || position.EditorViewportAnchor >= 100_000_000 ||
            position.ReadingViewportAnchor < 0 || position.ReadingViewportAnchor >= 100_000_000 ||
            !double.IsFinite(position.EditorHorizontalOffset) ||
            position.EditorHorizontalOffset < 0 || position.EditorHorizontalOffset >= 100_000_000 ||
            !double.IsFinite(position.ReadingViewportDelta))
            return false;
        normalized = position with
        {
            ReadingViewportDelta = Math.Clamp(position.ReadingViewportDelta, -2_000, 2_000)
        };
        return true;
    }

    private static double Number(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    public static double NormalizeReadingWidth(double value) =>
        ReadingWidthPresets.Contains(value) ? value : 680;

    public static double NormalizeMenuReadingWidth(double value) =>
        double.IsFinite(value) && value is 560 or 720 or 900 or 5_000
            ? value == 5_000 ? 1_000_000 : value
            : 680;

    private static double NormalizePersistedReadingWidth(double value) =>
        double.IsFinite(value) && (ReadingWidthPresets.Contains(value) || value is 560 or 720 or 900)
            ? value
            : 680;

    private static bool IsLegacyWarmPaper(string? value) =>
        value?.Trim().ToLowerInvariant() is "warmpaper" or "warm-paper" or "暖纸";
}

public sealed record RecentPosition(
    int SelectionStart,
    int SelectionLength,
    int SourceAnchor,
    int EditorViewportAnchor,
    double EditorHorizontalOffset,
    int ReadingViewportAnchor,
    double ReadingViewportDelta,
    bool IsReading);
