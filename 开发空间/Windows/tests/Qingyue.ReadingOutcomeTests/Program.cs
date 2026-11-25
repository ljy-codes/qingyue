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
    Check(legacy.Theme == "classic" && !legacy.Dark && !legacy.FollowSystem,
        "旧设置迁移为 Classic 并保留原明暗");
    Check(legacy.FontFamily.Length > 0 && legacy.LineSpacing >= 0 && legacy.ParagraphSpacing >= 0,
        "阅读外观旧设置缺字段仍有默认值");

    File.WriteAllText(Path.Combine(root, "Settings.json"),
        """{"Dark":true,"Theme":"WarmPaper","FontSize":17}""");
    var legacyWarmPaper = ReaderSettings.Load(root);
    Check(legacyWarmPaper.Theme == "coffee" && !legacyWarmPaper.Dark
        && !legacyWarmPaper.EffectiveDark,
        "旧 WarmPaper 迁移为 Coffee 浅色，即使旧设置记录为深色");

    File.WriteAllText(Path.Combine(root, "Settings.json"),
        """{"Dark":true,"Theme":"coffee","FollowSystem":false,"FontSize":17}""");
    var coffeeDark = ReaderSettings.Load(root);
    Check(coffeeDark.Theme == "coffee" && coffeeDark.Dark
        && coffeeDark.EffectiveDark,
        "新版 Coffee 深色设置保留明暗选择");

    Check(ReaderTheme.All.Count == 7, "提供 7 个外观主题");
    foreach (var theme in ReaderTheme.All)
    {
        Check(!string.IsNullOrWhiteSpace(ReaderTheme.Label(theme))
            && !string.IsNullOrWhiteSpace(ReaderTheme.Detail(theme))
            && ReaderTheme.Swatches(theme).Count == 5,
            $"主题 {theme} 提供可用的标题、详情和 5 个色块");
        for (int darkIndex = 0; darkIndex < 2; darkIndex++)
        {
            bool dark = darkIndex == 1;
            var palette = ReaderTheme.GetPalette(theme, dark);
            string mode = dark ? "深色" : "浅色";
            Check(palette.Theme == theme && palette.Dark == dark,
                $"{theme} {mode} 保留主题与明暗标识");
            Check(ReaderTheme.Contrast(palette.Text, palette.Editor) >= 4.5,
                $"{theme} {mode} 正文对比度至少 4.5");
            Check(ReaderTheme.Contrast(palette.Muted, palette.Panel) >= 4.5,
                $"{theme} {mode} 辅助文字对比度至少 4.5");
            Check(ReaderTheme.Contrast(palette.Accent, palette.Editor) >= 3.0,
                $"{theme} {mode} 强调色对比度至少 3.0");
            Check(ReaderTheme.Contrast(palette.AccentText, palette.Editor) >= 4.5,
                $"{theme} {mode} 强调文字对比度至少 4.5");
            Check(ReaderTheme.Contrast(palette.AccentForeground, palette.Accent) >= 4.5,
                $"{theme} {mode} 强调按钮前景对比度至少 4.5");
            Check(ReaderTheme.Contrast(palette.Text, palette.Main) >= 4.5,
                $"{theme} {mode} 主区域文字对比度至少 4.5");
            Check(ReaderTheme.Contrast(palette.Text, palette.Hover) >= 4.5,
                $"{theme} {mode} 悬停文字对比度至少 4.5");
            Check(palette.Window != palette.Panel && palette.Window != palette.Editor,
                $"{theme} {mode} 保留窗口、面板和编辑器层级");

            var paletteResources = new ResourceDictionary();
            var applied = ReaderTheme.Apply(paletteResources, theme, dark);
            Check(applied == palette
                && paletteResources["AccentForegroundBrush"] is SolidColorBrush
                && paletteResources["SyntaxKeyBrush"] is SolidColorBrush
                && paletteResources["SyntaxStringBrush"] is SolidColorBrush
                && paletteResources["SyntaxValueBrush"] is SolidColorBrush
                && paletteResources["SyntaxCommentBrush"] is SolidColorBrush,
                $"{theme} {mode} 应用完整主题 token");
        }
    }

    Check(ReaderTheme.Parse("Dark", true) == ReaderThemeKind.Classic
        && ReaderTheme.Parse("Light", false) == ReaderThemeKind.Classic
        && ReaderTheme.Parse("unknown", true) == ReaderThemeKind.Classic,
        "旧 Dark/Light 与未知主题迁移为 Classic");

    var defaults = new ReaderSettings();
    var presentation = defaults.ToPresentation();
    Check(defaults.ReadingWidth == 1_000_000,
        "新配置冷启动阅读宽度为 macOS 的全宽默认值");
    Check(presentation.FontSize == defaults.FontSize && presentation.FontFamily == defaults.FontFamily,
        "阅读外观由设置生成");
    Check(Math.Abs(presentation.LineHeightFor(presentation.FontSize)
        - (presentation.FontSize * ReadingPresentation.NaturalLineHeightFactor + presentation.LineSpacing)) < .001,
        "阅读行高按字号自然行高加实际行距计算");
    Check(ReadingFontCatalog.Normalize("不存在的字体") == ReadingFontCatalog.Fallback,
        "未知字体回退到稳定字体");
    Check(ReadingFontCatalog.Normalize("Microsoft YaHei UI") == "Microsoft YaHei UI",
        "已支持字体保持原值");
    Check(ReadingFontCatalog.Normalize("PingFang SC") == ReadingFontCatalog.Fallback &&
          ReadingFontCatalog.Normalize("Songti SC") == ReadingFontCatalog.Song,
        "macOS 字体标识迁移到 Windows 等价字体");
    Check(ReadingFontCatalog.Preferences.Select(item => item.Label)
            .SequenceEqual(["微软雅黑（苹方替代）", "宋体", "系统字体"]),
        "阅读字体下拉提供与 macOS 等价的三种选择，并明确苹方在 Windows 的替代字体");
    Check(ReadingFontCatalog.Normalize("system") == ReadingFontCatalog.SystemPreference
        && ReadingFontCatalog.Resolve("system") != ReadingFontCatalog.SystemPreference,
        "系统字体偏好保留特殊值并解析为实际系统字体");

    var configured = new ReaderSettings();
    new ReadingAppearanceSelection(
        "不存在的字体", 6, -3, ReaderThemeKind.WarmPaper, AppIconThemeKind.Peach,
        LineSpacing: 6, ThemeId: "coffee", Dark: false, FollowSystem: false,
        LineNumbers: true, Wrap: false, FontSize: 18, ReadingWidth: 840).ApplyTo(configured);
    Check(configured.FontFamily == ReadingFontCatalog.Fallback
        && configured.LineSpacing == 6
        && configured.ParagraphSpacing == 0
        && configured.Theme == "coffee"
        && !configured.Dark
        && !configured.FollowSystem
        && configured.LineNumbers
        && !configured.Wrap
        && configured.FontSize == 18
        && configured.ReadingWidth == 840
        && configured.IconTheme == "Peach",
        "阅读外观选择会规范化字体、实际行距、段距、主题、显示开关、字号和版心");

    var preferenceDefaults = ReadingAppearanceSelection.FromSettings(new ReaderSettings());
    Check(preferenceDefaults.Theme == ReaderThemeKind.Classic
        && preferenceDefaults.FontSize == 14
        && preferenceDefaults.LineSpacing == 6
        && preferenceDefaults.ParagraphSpacing == 13
        && preferenceDefaults.ReadingWidth == 1_000_000
        && preferenceDefaults.FollowSystem == true
        && preferenceDefaults.LineNumbers == true
        && preferenceDefaults.Wrap == true,
        "阅读外观设置从当前 macOS 启动状态读取");

    string settingsPath = Path.Combine(root, "Settings.json");
    File.Delete(settingsPath);
    Check(ReaderSettings.Load(root).ReadingWidth == 1_000_000,
        "空配置目录加载后阅读宽度为 macOS 的全宽默认值");
    File.WriteAllText(settingsPath, """{"Dark":false,"FontSize":14}""");
    Check(ReaderSettings.Load(root).ReadingWidth == 1_000_000,
        "旧设置缺少 ReadingWidth 时迁移为 macOS 的全宽默认值");
    foreach (double width in new[] { 520d, 560d, 680d, 720d, 840d, 900d, 1_000_000d })
    {
        File.WriteAllText(settingsPath,
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Dark"] = false,
                ["FontSize"] = 14,
                ["ReadingWidth"] = width
            }));
        Check(ReaderSettings.Load(root).ReadingWidth == width,
            $"保留 macOS 阅读宽度预设 {width:0}");
    }
    foreach (double width in new[] { 400d, 1_000d, 1_000_001d })
    {
        File.WriteAllText(settingsPath,
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["Dark"] = false,
                ["FontSize"] = 14,
                ["ReadingWidth"] = width
            }));
        Check(ReaderSettings.Load(root).ReadingWidth == 680,
            $"非 macOS 预设阅读宽度 {width:0} 回退为 680");
    }
    Check(ReaderSettings.NormalizeMenuReadingWidth(5000) == 1_000_000
        && ReaderSettings.NormalizeMenuReadingWidth(560) == 560
        && ReaderSettings.NormalizeMenuReadingWidth(720) == 720
        && ReaderSettings.NormalizeMenuReadingWidth(900) == 900
        && ReaderSettings.NormalizeMenuReadingWidth(520) == 680,
        "菜单宽度严格使用 macOS 的 560 / 720 / 900 / 5000 契约");

    var resources = new ResourceDictionary();
    foreach (var theme in ReaderTheme.All)
    {
        var palette = ReaderTheme.Apply(resources, theme, dark: true);
        Check(palette.Theme == theme, $"主题 {theme} 可以应用");
        Check(resources["WindowBrush"] is SolidColorBrush
            && resources["AnnotationAmberBrush"] is SolidColorBrush
            && resources["AnnotationVioletBrush"] is SolidColorBrush,
            $"主题 {theme} 提供批注画刷");
    }

    Check(AnnotationEditorDialog.ParseTags("重点, 回顾；重点  待读")
        .SequenceEqual(["重点", " 回顾；重点  待读"]),
        "新增批注标签只按半角逗号拆分，不按分号、空格或换行拆分");
    Check(AnnotationEditorDialog.ParseTags("重点，回顾")
        .SequenceEqual(["重点，回顾"]),
        "新增批注标签不把全角逗号当作分隔符");

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
    string formattedQuote = "这是 **重要结论**，请回顾。";
    var formattedAnnotation = new DocumentAnnotation(
        Guid.NewGuid(), Guid.NewGuid(), null,
        markdown.IndexOf("这是", StringComparison.Ordinal),
        markdown.Length, formattedQuote, "", "", "备注", ["重点"],
        AnnotationColor.Amber, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        AnnotationAnchorState.Exact);
    Check(AnnotationHighlightAdorner.TryGetReadingRange(
            model, formattedAnnotation, out int formattedStart, out int formattedLength)
        && model.VisibleText.Substring(formattedStart, formattedLength) == "这是 重要结论，请回顾。",
        "带 Markdown 标记的批注无法精确匹配时高亮整个渲染块");
    string repeated = "# 标题\n\n这是 **重要结论**，请回顾。\n\n其他段落：重要结论。";
    var repeatedModel = ReadingModel.Build(repeated, TextKind.Markdown);
    var repeatedAnnotation = new DocumentAnnotation(
        Guid.NewGuid(), Guid.NewGuid(), null,
        repeated.IndexOf("这是", StringComparison.Ordinal),
        repeated.IndexOf("\n\n其他", StringComparison.Ordinal),
        formattedQuote, "", "", "备注", ["重点"],
        AnnotationColor.Amber, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        AnnotationAnchorState.Exact);
    Check(AnnotationHighlightAdorner.TryGetReadingRange(
            repeatedModel, repeatedAnnotation, out int repeatedStart, out int repeatedLength)
        && repeatedModel.VisibleText.Substring(repeatedStart, repeatedLength) == "这是 重要结论，请回顾。",
        "批注高亮不会跨块搜索可见 Quote");
    var selectedDraft = AnnotationSelection.FromReading(
        Guid.NewGuid(), sourcePath: null, model, displayStart, displayLength);
    Check(selectedDraft.Quote == "这是 **重要结论**，请回顾。"
        && selectedDraft.SourceStart == markdown.IndexOf("这是", StringComparison.Ordinal)
        && selectedDraft.SourceEnd == markdown.Length
        && selectedDraft.ContextBefore == "# 标题\n\n"
        && selectedDraft.ContextAfter == "",
        "阅读选区反向映射为 macOS 一致的整块 Markdown 原文");

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
