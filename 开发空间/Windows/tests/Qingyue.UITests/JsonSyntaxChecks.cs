using System.Text.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using Qingyue.Core;
using Qingyue.Windows;

internal static class JsonSyntaxChecks
{
    // Run on the UI test runner's STA thread. Hosts its own controls in an invisible HWND.
    public static void Check(Action<bool, string> check)
    {
        const string comments = "// 首行\r\n{\"键\" /*说明\r\n \"假键\":9 */ : \"中文\", \"n\": -0e+9}\n// 末行";
        var tokens = JsonSyntax.Tokens(comments).ToArray();
        check(tokens.Count(t => t.Kind == JsonTokenColor.Comment) == 3,
            "JSONC：行注释到换行结束，块注释跨行且只生成一个 token");
        check(tokens.Where(t => t.Kind == JsonTokenColor.Key)
                .Select(t => comments.Substring(t.Start, t.Length)).SequenceEqual(["\"键\"", "\"n\""]),
            "JSONC：块注释里的伪键不染色，键与冒号之间允许注释");
        check(tokens.Any(t => t.Kind == JsonTokenColor.Value && comments.Substring(t.Start, t.Length) == "-0e+9"),
            "JSON：指数与负零原文范围不变");
        const string carriageReturn = "// 注释\r\"键\":true";
        check(JsonSyntax.Tokens(carriageReturn).Count(t => t.Kind == JsonTokenColor.Key) == 1,
            "JSONC：单独 CR 也结束行注释");
        const string strings = "{\"网址\":\"https://例子/\\\"/*并非注释*/\", \"重复\":1,\"重复\":9007199254740993}";
        var escaped = JsonSyntax.Tokens(strings).ToArray();
        check(escaped.Count(t => t.Kind == JsonTokenColor.Key) == 3 &&
              escaped.Count(t => t.Kind == JsonTokenColor.Comment) == 0 &&
              escaped.Count(t => t.Kind == JsonTokenColor.String) == 1,
            "JSON：字符串转义、中文、重复键与大数只读分词");
        var unfinished = JsonSyntax.Tokens("/* 未闭合\n\"不是键\":1").ToArray();
        check(unfinished.Length == 1 && unfinished[0] is { Start: 0, Kind: JsonTokenColor.Comment },
            "JSONC：未闭合块注释延续到文末");

        string longKey = new string('名', 255) + "😀\n下一行" + new string('x', 4000);
        string longValue = new string('中', 498) + "😀" + new string('文', 6000);
        var tree = new JsonStructureView("{" + JsonSerializer.Serialize(longKey) + ":" +
            JsonSerializer.Serialize(longValue) + ",\"重复\":1,\"重复\":2,\"nested\":{\"child\":true}}", TextKind.Json);
        var root = (TreeViewItem)tree.Items[0];
        var leaf = (TreeViewItem)root.Items[0];
        var runs = ((TextBlock)leaf.Header).Inlines.OfType<Run>().ToArray();
        check(runs[0].Text.Length <= 260 && !runs[0].Text.Contains('\n') &&
              !HasUnpairedSurrogate(runs[0].Text),
            "JSON结构：字段名有界、单行，截断不切坏代理对");
        check(runs[1].Text.Length <= 501 && !HasUnpairedSurrogate(runs[1].Text),
            "JSON结构：长字符串预览有界且不切坏代理对");
        check(leaf.ToolTip is string tooltip && tooltip.Length <= 2049 && !HasUnpairedSurrogate(tooltip),
            "JSON结构：tooltip 有界，避免悬停绘制整个大字段");
        check(root.Items.Count == 4 &&
              ((TextBlock)((TreeViewItem)root.Items[1]).Header).Inlines.OfType<Run>().First().Text ==
              ((TextBlock)((TreeViewItem)root.Items[2]).Header).Inlines.OfType<Run>().First().Text,
            "JSON结构：同名属性仍按原顺序分别展示");
        var nested = (TreeViewItem)root.Items[3];
        check(nested.Items.Count == 1 && nested.Items[0] is string,
            "JSON结构：未展开子树保持占位懒加载");
        nested.IsExpanded = true;
        check(nested.Items.Count == 1 && nested.Items[0] is TreeViewItem,
            "JSON结构：解析文档释放后子树仍可延迟展开");
        var controlNames = new JsonStructureView("{\"行\\n名\\u2028尾\":true}", TextKind.Json);
        var controlLeaf = (TreeViewItem)((TreeViewItem)controlNames.Items[0]).Items[0];
        string controlLabel = ((TextBlock)controlLeaf.Header).Inlines.OfType<Run>().First().Text;
        check(!controlLabel.Contains('\n') && !controlLabel.Contains('\u2028') &&
              controlLabel.Contains("\\n") && controlLabel.Contains("\\u2028"),
            "JSON结构：字段名中的换行与 Unicode 行分隔符转义展示");
        CheckCache(check);
        CheckReentrantEditing(check);
    }

    private static void CheckReentrantEditing(Action<bool, string> check)
    {
        var layerType = typeof(JsonSyntax).Assembly.GetType("Qingyue.Windows.JsonSyntaxLayer")!;
        var render = layerType.GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var refresh = layerType.GetMethod("Refresh")!;
        foreach (var kind in new[] { TextKind.Markdown, TextKind.Jsonc, TextKind.Json })
        {
            // EditorTab's first TextChanged handler asks TextBox for LineCount, which may
            // force layout before the syntax layer receives the same TextChanged event.
            var tab = new EditorTab(new DocumentFile(kind == TextKind.Markdown ? "# 原标题" : "{\"键\":1}"))
            {
                Kind = kind
            };
            FrameworkElement? layer = null;
            bool duringEdit = false, renderedEarly = false, fileWasOld = false;
            tab.Editor.TextChanged += (_, _) =>
            {
                if (!duringEdit || layer is null) return;
                fileWasOld = tab.File.Text != tab.Editor.Text;
                tab.EditorSurface.UpdateLayout();
                // Deliberately render in an earlier subscriber, using the actual TextBox's
                // visual rows. This makes the layout reentry deterministic without a timer.
                using var context = new DrawingVisual().RenderOpen();
                render.Invoke(layer, [context]);
                renderedEarly = true;
            };
            layer = (FrameworkElement)Activator.CreateInstance(layerType, [tab])!;
            Grid.SetColumn(layer, 1);
            tab.EditorSurface.Children.Add(layer);
            tab.EditorSurface.Resources["TextBrush"] = Brushes.Black;
            using var host = new HwndSource(new HwndSourceParameters("Qingyue syntax regression")
            {
                Width = 640, Height = 400,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP without WS_VISIBLE; never shown.
                PositionX = -32000, PositionY = -32000
            });
            try
            {
                host.RootVisual = tab.EditorSurface;
                tab.EditorSurface.Measure(new Size(640, 400));
                tab.EditorSurface.Arrange(new Rect(0, 0, 640, 400));
                tab.Editor.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                tab.EditorSurface.UpdateLayout();
                refresh.Invoke(layer, null);
                check(tab.Editor.IsLoaded && tab.Editor.LineCount > 0,
                    $"{kind}重入回归：使用已加载的实际 WPF TextBox 与隐藏宿主");
                duringEdit = true;
                // Original source stays unchanged intentionally: syntax must read Editor.Text.
                tab.Editor.AppendText("\n## 新增目录\n正文内容比旧缓存更长，确保新行范围超过原文。\n");
                tab.Editor.Select(0, tab.Editor.Text.Length);
                tab.Editor.SelectedText = kind == TextKind.Markdown ? "# 短" : "{}";
                tab.Editor.Undo();
                tab.Editor.Text = new string('x', 20_000);
                check(tab.Editor.Foreground != Brushes.Transparent,
                    $"{kind}重入回归：编辑到超预算文本立即恢复原生前景色");
                tab.Editor.Text = kind == TextKind.Markdown ? "# 恢复标题" : "{\"恢复\":true}";
                check(renderedEarly && fileWasOld,
                    $"{kind}重入回归：追加、缩短、撤销及超限恢复先于图层 TextChanged 均不崩溃");
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException { InnerException: not null } wrapped
                    ? wrapped.InnerException : ex;
                check(false, $"{kind}重入回归：实际控件编辑失败：{cause!.GetType().Name}: {cause.Message}");
            }
            finally
            {
                duringEdit = false;
                host.RootVisual = null;
            }
        }
    }

    private static void CheckCache(Action<bool, string> check)
    {
        var cache = new DocumentSyntaxCache();
        const string json = "{\"中文\":\"甲乙😀丙丁\",/*第一行\r\n第二行*/\"x\":true}";
        check(cache.Update(json, TextKind.Jsonc), "高亮缓存：支持完整 JSONC 文档");
        int middle = json.IndexOf("乙", StringComparison.Ordinal);
        var wrapped = cache.TokensInRange(middle, 5).ToArray();
        check(wrapped.Length == 1 && wrapped[0] is { Start: 0, Length: 5, ResourceKey: "SyntaxStringBrush" },
            "高亮缓存：字符串在视觉折行中间开始仍保持字符串状态与 UTF-16 长度");
        int block = json.IndexOf("第二行", StringComparison.Ordinal);
        var blockRow = cache.TokensInRange(block, 5).ToArray();
        check(blockRow.Length == 1 && blockRow[0] is { Start: 0, Length: 5, ResourceKey: "SyntaxCommentBrush" },
            "高亮缓存：跨行块注释按原文 CRLF 偏移映射");
        var cachedTokens = cache.Tokens;
        cache.Update(new string(json.ToCharArray()), TextKind.Jsonc);
        check(ReferenceEquals(cachedTokens, cache.Tokens),
            "高亮缓存：内容未变时复用 token，滚动和字体刷新不重新分词");
        check(cache.TokensInRange(json.Length, 0).Count == 0,
            "高亮缓存：文末空行无需构造 token");

        const string markdown = "````md\r\n# 代码标题\r\n```\r\n仍为代码 **文本**\r\n````\r\n# 正文\r\n~~~txt\n# 波浪围栏\n~~~ nope\n# 仍在围栏\n~~~~\n# 结束";
        check(cache.Update(markdown, TextKind.Markdown), "高亮缓存：支持完整 Markdown 文档");
        bool ColorAt(string value, string color)
        {
            int start = markdown.IndexOf(value, StringComparison.Ordinal);
            var range = cache.TokensInRange(start, value.Length);
            return range.Count == 1 && range[0].Start == 0 &&
                range[0].Length == value.Length && range[0].ResourceKey == color;
        }
        check(ColorAt("# 代码标题", "SyntaxStringBrush") &&
              ColorAt("仍为代码 **文本**", "SyntaxStringBrush"),
            "Markdown高亮：短于开围栏的反引号不闭合，多行内容不当标题或强调");
        check(ColorAt("# 波浪围栏", "SyntaxStringBrush") &&
              ColorAt("# 仍在围栏", "SyntaxStringBrush"),
            "Markdown高亮：波浪围栏支持跨行，带非空白尾部的围栏不能闭合");
        check(ColorAt("# 正文", "AccentTextBrush") && ColorAt("# 结束", "AccentTextBrush"),
            "Markdown高亮：匹配围栏闭合后恢复正文状态");
        const string indented = "    ```\n# 普通标题\n";
        cache.Update(indented, TextKind.Markdown);
        check(cache.TokensInRange(8, 6).Any(t => t.ResourceKey == "AccentTextBrush"),
            "Markdown高亮：缩进代码块内的反引号不打开围栏");
        cache.Update("\"changed\":false", TextKind.Json);
        check(!ReferenceEquals(cachedTokens, cache.Tokens) &&
              cache.TokensInRange(10, 5).Single().ResourceKey == "SyntaxValueBrush",
            "高亮缓存：修改原文或切换语言后刷新全部状态");
        check(!cache.Update(new string('x', 100_000), TextKind.Json),
            "高亮预算：超长逻辑单行即使折行也回退原生颜色");
        check(!cache.Update(new string('\n', 1_000_001), TextKind.Json),
            "高亮预算：大文档回退原生颜色");
        check(!cache.Update(string.Concat(Enumerable.Repeat("\"x\":1,\n", 60_000)), TextKind.Json),
            "高亮预算：过密 token 有界，超限整层回退");
        check(DocumentSyntaxCache.CanRenderViewport([20, 30, 0]),
            "高亮预算：普通可见范围可绘制");
        check(!DocumentSyntaxCache.CanRenderViewport(Enumerable.Repeat(1, 300).ToArray()),
            "高亮预算：可见行超限整层回退，不留下透明的尾部行");
        check(!DocumentSyntaxCache.CanRenderViewport([10_000]),
            "高亮预算：超长视觉行不进入 FormattedText");
        check(!DocumentSyntaxCache.CanRenderViewport([8000, 8000, 8000, 8000, 8000]),
            "高亮预算：可见字符总量超限整层回退");
        check(cache.Tokens.Count == 0 && !cache.Update("plain", TextKind.Plain),
            "高亮预算：拒绝文档时清除旧 token，不误染其他语言");
    }

    private static bool HasUnpairedSurrogate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return true;
            }
            else if (char.IsLowSurrogate(text[i])) return true;
        }
        return false;
    }
}
