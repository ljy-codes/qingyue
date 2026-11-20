using System.IO;
using System.Windows;
using System.Windows.Media;
using Qingyue.Core;
using Qingyue.Windows;

int checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

var root = Path.Combine(Path.GetTempPath(), "qingyue-reading-outcome-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    File.WriteAllText(Path.Combine(root, "Settings.json"), """{"Dark":false,"FontSize":17}""");
    var legacy = ReaderSettings.Load(root);
    Check(legacy.Theme == "Light", "旧设置按 Dark 值回退到浅色主题");
    Check(legacy.FontFamily.Length > 0 && legacy.LineHeight > 1 && legacy.ParagraphSpacing >= 0,
        "阅读外观旧设置缺字段仍有默认值");

    var defaults = new ReaderSettings();
    var presentation = defaults.ToPresentation();
    Check(presentation.FontSize == defaults.FontSize && presentation.FontFamily == defaults.FontFamily,
        "阅读外观由设置生成");
    Check(ReadingFontCatalog.Normalize("不存在的字体") == ReadingFontCatalog.Fallback,
        "未知字体回退到稳定字体");
    Check(ReadingFontCatalog.Normalize("Microsoft YaHei UI") == "Microsoft YaHei UI",
        "已支持字体保持原值");

    var configured = new ReaderSettings();
    new ReadingAppearanceSelection(
        "不存在的字体", 9, -3, ReaderThemeKind.WarmPaper, AppIconThemeKind.Peach).ApplyTo(configured);
    Check(configured.FontFamily == ReadingFontCatalog.Fallback
        && configured.LineHeight == 3.2
        && configured.ParagraphSpacing == 0
        && configured.Theme == "WarmPaper"
        && !configured.Dark
        && configured.IconTheme == "Peach",
        "阅读外观选择会规范化字体、行高、段距和暖纸主题");

    var resources = new ResourceDictionary();
    foreach (var theme in new[] { ReaderThemeKind.Dark, ReaderThemeKind.Light, ReaderThemeKind.WarmPaper })
    {
        var palette = ReaderTheme.Apply(resources, theme);
        Check(palette.Theme == theme, $"主题 {theme} 可以应用");
        Check(resources["WindowBrush"] is SolidColorBrush
            && resources["AnnotationAmberBrush"] is SolidColorBrush
            && resources["AnnotationVioletBrush"] is SolidColorBrush,
            $"主题 {theme} 提供批注画刷");
    }

    Check(AnnotationEditorDialog.ParseTags("重点, 回顾；重点  待读")
        .SequenceEqual(["重点", "回顾", "待读"]), "批注标签支持逗号、分号和重复去重");

    string markdown = "# 标题\n\n这是 **重要结论**，请回顾。";
    var model = ReadingModel.Build(markdown, TextKind.Markdown);
    int sourceStart = markdown.IndexOf("重要结论", StringComparison.Ordinal);
    var annotation = new DocumentAnnotation(
        Guid.NewGuid(), Guid.NewGuid(), null, sourceStart, sourceStart + "重要结论".Length,
        "重要结论", "这是 ", "，请回顾。", "备注", ["重点"],
        AnnotationColor.Amber, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        AnnotationAnchorState.Exact);
    Check(AnnotationHighlightAdorner.TryGetReadingRange(model, annotation, out int displayStart, out int displayLength)
        && model.VisibleText.Substring(displayStart, displayLength) == "重要结论",
        "批注高亮使用阅读视图可见文本定位");
    var selectedDraft = AnnotationSelection.FromReading(
        Guid.NewGuid(), sourcePath: null, model, displayStart, displayLength);
    Check(selectedDraft.Quote == "重要结论"
        && selectedDraft.ContextBefore.EndsWith("这是 ", StringComparison.Ordinal)
        && selectedDraft.ContextAfter.StartsWith("，请回顾", StringComparison.Ordinal),
        "阅读选区转换为批注草稿并保留可见上下文");

    string sourcePath = Path.Combine(root, "source.md");
    File.WriteAllText(sourcePath, markdown);
    var briefInput = ReadingBriefExportService.CreateInput(
        "示例文档", sourcePath, TextKind.Markdown, markdown, 35,
        DateTimeOffset.UnixEpoch, [annotation]);
    string briefPath = Path.Combine(root, "brief.md");
    await ReadingBriefExportService.ExportAsync(
        briefInput, briefPath, html: false, [sourcePath]);
    Check(File.ReadAllText(briefPath).Contains("重要结论", StringComparison.Ordinal),
        "当前文档 Markdown 简报可以原子导出");
    Check(ReadingBriefExportService.ValidateTarget(sourcePath, [sourcePath]) is not null,
        "简报导出拒绝覆盖当前原文");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine($"全部通过：{checks} 项");
