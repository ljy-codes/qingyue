using System.IO;
using System.Text.Json;

namespace Qingyue.Windows;

public sealed class ReaderSettings
{
    public bool Dark { get; set; } = true;
    public bool Wrap { get; set; } = true;
    public bool LineNumbers { get; set; } = true;
    public bool Sidebar { get; set; } = true;
    public bool RecoveryEnabled { get; set; } = true;
    public bool RestoreSession { get; set; } = true;
    public double FontSize { get; set; } = 15;
    public double ReadingWidth { get; set; } = 720;
    public double Width { get; set; } = 1120;
    public double Height { get; set; } = 790;
    public List<string> Recent { get; set; } = [];
    public List<string> ExcludedDraftPaths { get; set; } = [];

    public static ReaderSettings Load(string root)
    {
        var path = Path.Combine(root, "Settings.json");
        if (!System.IO.File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) return new();
            var value = JsonSerializer.Deserialize<ReaderSettings>(System.IO.File.ReadAllText(path)) ?? new();
            value.FontSize = double.IsFinite(value.FontSize) ? Math.Clamp(value.FontSize, 10, 30) : 15;
            value.ReadingWidth = double.IsFinite(value.ReadingWidth) ? Math.Clamp(value.ReadingWidth, 400, 5000) : 720;
            value.Width = double.IsFinite(value.Width) ? Math.Clamp(value.Width, 820, 3200) : 1120;
            value.Height = double.IsFinite(value.Height) ? Math.Clamp(value.Height, 520, 2160) : 790;
            value.Recent = (value.Recent ?? []).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
            value.ExcludedDraftPaths ??= [];
            return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, "Settings.json");
        string temporary = Path.Combine(root, "Settings." + Guid.NewGuid() + ".tmp");
        try
        {
            System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            if (System.IO.File.Exists(destination)) System.IO.File.Replace(temporary, destination, destination + ".bak");
            else System.IO.File.Move(temporary, destination);
        }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }
}
