using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Qingyue.Core;
using Qingyue.Windows;

internal static class Program
{
    private static int failed;
    private static int passed;
    private static readonly byte[] Png = MakePng();
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "artifacts", "中文 图片");
    private static readonly string SourcePath = Path.Combine(FixtureDirectory, "使用 指南.md");
    private static string DataUri => "data:image/png;base64," + Convert.ToBase64String(Png);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--extractor-probe"]) return ExtractorProbe();
        Directory.CreateDirectory(FixtureDirectory);
        File.WriteAllBytes(Path.Combine(FixtureDirectory, "本地 图 (1).png"), Png);
        Test("embedded PNG becomes a real frozen WPF bitmap", Embedded);
        Test("code and escaped image syntax stay literal", LiteralCode);
        Test("mapped reading keeps one Run and UTF16 pointer roundtrips", Mapping);
        Test("relative absolute file URI Chinese spaces and URL escapes in both renderers", LocalPaths);
        Test("mapped reading inserts images without changing the mapping Runs", MappedImages);
        Test("long inline base64 keeps rich paragraphs tables lists and Mermaid", LongEmbedded);
        Test("image table list and Mermaid block tags retain original source ranges", TagsAndContainers);
        Test("image captions and text after images remain selectable through rich mapping", CaptionMapping);
        Test("rich navigation keeps list boundaries after tables and document host transfer", ListNavigation);
        Test("100k malformed image starts stay bounded and preserve their source", MalformedImages);
        Test("full reading can retain selectable Mermaid source while split preview stays compact", DiagramSource);
        Test("parser fallback never renders embedded image data as body text", SafeFallback);
        Test("discarded bitmap results release the document decode budget", DiscardedBitmap);
        Test("missing corrupt invalid base64 and missing source path show reasons", Failures);
        Test("HTTP UNC encoded UNC and nonlocal file URIs never load", NoNetwork);
        Test("image byte pixel dimension and document count limits", Limits);
        Test("wide images decode to a bounded frozen bitmap", Downsample);
        Test("image layout scales with viewport and keeps its aspect ratio", Responsive);
        Test("preview replacement unload cancellation and recovery", Replacement);
        if (args.Length == 2 && args[0] == "--samples")
        {
            foreach (string name in new[] { "01-简介与接入方式.md", "03-管理台接口.md", "04-业务后端接口.md" })
            {
                Test("real embedded document " + name, () => RealSample(Path.Combine(args[1], name)));
                Test("embedded document without images directory " + name, () =>
                {
                    string original = Path.Combine(args[1], name);
                    string directory = Path.Combine(AppContext.BaseDirectory, "artifacts", "samples-without-images");
                    Directory.CreateDirectory(directory);
                    Check(!Directory.Exists(Path.Combine(directory, "images")), "隔离样本目录存在 images 子目录");
                    string copy = Path.Combine(directory, name);
                    File.Copy(original, copy, overwrite: true);
                    Check(File.ReadAllBytes(original).SequenceEqual(File.ReadAllBytes(copy)), "样本副本正文被改变");
                    RealSample(copy);
                });
            }
            Test("no-image document 02 opens", () => NoImageSample(Path.Combine(args[1], "02-移动端接口.md")));
        }
        Console.WriteLine($"Image checks: {passed}/{passed + failed} passed");
        return failed == 0 ? 0 : 1;
    }

    private static void Embedded()
    {
        var preview = new MarkdownPreview("# 图片\n\n![红蓝图片](" + DataUri + ")\n\n后文😀", 16, 680);
        var image = Images(preview.Document).SingleOrDefault();
        Check(image is not null, "图片语法没有生成真实 WPF Image");
        Activate(preview.Document);
        PumpUntil(() => image!.Source is not null, "异步图片未完成");
        var bitmap = (BitmapSource)image!.Source;
        Check(bitmap.IsFrozen && bitmap.PixelWidth == 640 && bitmap.PixelHeight == 320,
            "位图必须解码、冻结并保留尺寸");
        Check(!Text(preview.Document).Contains("base64,"), "Base64 不应显示为正文");
    }

    private static void LiteralCode()
    {
        string code = "![literal](" + DataUri + ")";
        var preview = new MarkdownPreview("```md\n" + code + "\n```\n\n`" + code + "`\n\n\\![escaped](missing.png)", 16, 680);
        Check(!Images(preview.Document).Any(), "代码或转义语法被错误识别为图片");
        Check(Text(preview.Document).Contains(code), "代码原文丢失");
    }

    private static void Mapping()
    {
        const string text = "# 标题😀\n\n前文 **加粗** ![图片](missing.png) 后文\n\n```mermaid\nsequenceDiagram\nA->>B: 继续\n```";
        var model = ReadingModel.Build(text, TextKind.Markdown);
        var reading = new RenderedReading(model);
        Check(reading.Document.Blocks.OfType<Paragraph>().All(p => p.Inlines.Count == 1 && p.Inlines.FirstInline is Run),
            "映射段落不再是单 Run");
        for (int offset = 0; offset <= model.VisibleText.Length; offset++)
        {
            if (offset > 0 && offset < model.VisibleText.Length &&
                char.IsHighSurrogate(model.VisibleText[offset - 1]) && char.IsLowSurrogate(model.VisibleText[offset])) continue;
            Check(reading.OffsetAt(reading.PointerAt(offset)) == offset, $"指针往返失败：{offset}");
        }
    }

    private static void LocalPaths()
    {
        string absolute = Path.Combine(FixtureDirectory, "本地 图 (1).png");
        foreach (string target in new[]
        {
            "本地 图 (1).png", Uri.EscapeDataString("本地 图 (1).png"), absolute,
            absolute.Replace(" ", "%20"), new Uri(absolute).AbsoluteUri,
            "<本地 图 (1).png>", "本地 图 (1).png \"可选标题\"",
            @"本地 图 \(1\).png", "<" + absolute + "> \"标题\""
        })
        {
            string source = "前文😀 ![本地图片](" + target + ") 后文";
            var preview = new MarkdownPreview(source, 16, 680, SourcePath);
            AssertLoaded(preview.Document, target);
            var model = ReadingModel.Build(source, TextKind.Markdown);
            var reading = new RenderedReading(model, sourcePath: SourcePath, sourceText: source);
            AssertLoaded(reading.Document, "mapped " + target);
            Roundtrip(reading, model);
        }
    }

    private static void MappedImages()
    {
        string source = "# 前文😀\n\n![一](" + DataUri + ")\n\n正文 **加粗** ![二](missing.png)\n\n尾段";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        var reading = new RenderedReading(model, sourcePath: SourcePath, sourceText: source);
        Check(Images(reading.Document).Count() == 2, "映射阅读器没有插入两张图片");
        AssertLoaded(reading.Document, "映射内嵌图片", 0);
        Check(reading.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text)
            .SequenceEqual(model.Blocks.Select(b => b.Text)), "原映射 Run 文本被改变");
        Roundtrip(reading, model);
        foreach (var block in model.Blocks)
        {
            int sourceOffset = model.DisplayToSource(block.DisplayStart);
            Check(sourceOffset >= 0 && sourceOffset <= source.Length, "源映射越界");
        }
    }

    private static string LongSource()
    {
        // A valid PNG may contain trailing bytes after IEND. This makes a deterministic
        // 600,000+ character data URI without a large image allocation or external fixture.
        var padded = new byte[450_001];
        Png.CopyTo(padded, 0);
        return "# 长图片😀\n\n| 名称 | 值 |\n| --- | --- |\n| 图片 | 内嵌 |\n\n" +
            "- 第一项\n- 第二项\n\n![长图](data:image/png;base64," + Convert.ToBase64String(padded) + ")\n\n" +
            "```mermaid\nsequenceDiagram\nA->>B: 后续\n```\n\n## 图后标题\n\n图后正文😀";
    }

    private static void LongEmbedded()
    {
        string source = LongSource();
        Check(source.Length > 600_000, "测试数据没有达到真实内嵌图长度");
        var clock = Stopwatch.StartNew();
        var preview = new MarkdownPreview(source, 16, 680, SourcePath);
        Check(clock.Elapsed < TimeSpan.FromSeconds(3), "图片提取阻塞正文构建");
        AssertLoaded(preview.Document, "600K 内嵌 PNG");
        Check(preview.Document.Blocks.OfType<Table>().Any() &&
            preview.Document.Blocks.OfType<System.Windows.Documents.List>().Any(), "长图片导致富表格/列表退化");
        Check(Controls(preview.Document.Blocks).Any(c => Descendants<MermaidSequenceView>(c).Any()), "Mermaid 丢失");
        string visible = Text(preview.Document);
        Check(!visible.Contains("base64,") && !visible.Contains("qyimg") && visible.Contains("图后正文😀"), "图片数据或内部 token 泄漏为正文");
        Console.WriteLine($"  long source {source.Length:N0} chars, construction + decode {clock.ElapsedMilliseconds} ms");
    }

    private static void TagsAndContainers()
    {
        string source = "# 首部\n\n![图片](" + DataUri + ")\n\n" +
            "| 名称 | 图 |\n| --- | --- |\n| 行 | ![表格图](" + DataUri + ") |\n\n" +
            "- ![列表图](" + DataUri + ")\n\n```mermaid\nsequenceDiagram\nA->>B: 消息\n```\n\n尾段😀";
        var preview = new MarkdownPreview(source, 16, 680, SourcePath);
        var originalModel = ReadingModel.Build(source, TextKind.Markdown);
        Check(Images(preview.Document).Count() == 3, "普通、表格、列表图片数量错误");
        for (int i = 0; i < 3; i++) AssertLoaded(preview.Document, "各容器图片", i);
        foreach (var block in preview.Document.Blocks.Where(b => b is Paragraph or Table or BlockUIContainer))
        {
            Check(block.Tag is ReadingBlock, "段落/表格/Mermaid 缺少源块 Tag");
            var tag = (ReadingBlock)block.Tag;
            Check(originalModel.Blocks.Contains(tag), "Tag 仍携带图片投影的 Text 或 DisplayStart");
            Check(tag.SourceStart >= 0 && tag.SourceLength >= 0 && tag.SourceStart + tag.SourceLength <= source.Length,
                "源块 Tag 范围越界");
            string original = source.Substring(tag.SourceStart, tag.SourceLength);
            if (block is Table) Check(original.StartsWith("| 名称 | 图 |"), "表格偏移未还原到原文");
            if (block is BlockUIContainer) Check(original.StartsWith("```mermaid"), "Mermaid 偏移未还原到原文");
        }
        var tail = preview.Document.Blocks.OfType<Paragraph>().Last();
        var tailTag = (ReadingBlock)tail.Tag;
        Check(tailTag.SourceStart == source.IndexOf("尾段😀", StringComparison.Ordinal) && tailTag.Text == "尾段😀",
            "图片后正文映射错误");
        Check(tailTag.DisplayStart == originalModel.Blocks.Last().DisplayStart, "图片投影改变了后文 DisplayStart");
    }

    private static void Failures()
    {
        string broken = Path.Combine(FixtureDirectory, "损坏.png");
        File.WriteAllText(broken, "这不是图片");
        foreach (var item in new[]
        {
            (Target: "不存在.png", Path: (string?)SourcePath, Reason: "找不到"),
            (Target: "损坏.png", Path: (string?)SourcePath, Reason: "损坏"),
            (Target: "本地 图 (1).png", Path: (string?)null, Reason: "文档路径"),
            (Target: "data:image/png;base64,!!!", Path: (string?)null, Reason: "损坏"),
            (Target: "data:image/svg+xml;base64,PHN2Zy8+", Path: (string?)null, Reason: "仅支持")
        })
        {
            var preview = new MarkdownPreview("![错误图](" + item.Target + ")\n\n后文保留", 16, 680, item.Path);
            Activate(preview.Document);
            PumpUntil(() => Status(preview.Document).Any(t => t.Contains(item.Reason)), "缺少图片错误提示：" + item.Reason);
            Check(Images(preview.Document).All(i => i.Source is null) && Text(preview.Document).Contains("后文保留"),
                "错误图片破坏后续正文");
        }
    }

    private static void CaptionMapping()
    {
        string source = "前文 ![普通图](" + DataUri + ") 图后正文😀\n\n" +
            "| 图 | 名称 |\n| --- | --- |\n| ![表格图](" + DataUri + ") | 图后单元格 |\n\n" +
            "- ![列表图](" + DataUri + ") 列表后文\n\n![**强调图注**](" + DataUri + ")\n\n图后 base64 正文\n\n尾段";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        var preview = new MarkdownPreview(source, 16, 680, SourcePath);
        var map = new RichDocumentMap(preview.Document, model);
        foreach (string label in new[] { "[图片：普通图]", "[图片：表格图]", "[图片：列表图]" })
            Check(Text(preview.Document).Contains(label), "图片缺少可见图注：" + label);
        foreach (string fragment in new[] { "前文", "[图片：普通图]", "[图片：表格图]", "[图片：列表图]",
            "普通图", "表格图", "列表图", "强调图注",
            "图后正文😀", "图后单元格", "列表后文", "图后 base64 正文", "尾段" })
        {
            Check(Text(preview.Document).Contains(fragment), "图片缺少可见图注或正文：" + fragment);
            SelectFragment(map, model.VisibleText, fragment);
        }
    }

    private static void DiagramSource()
    {
        const string source = "```mermaid\nsequenceDiagram\nA->>B: /api/tasks/approve\n```\n\n尾段";
        var constructor = typeof(MarkdownPreview).GetConstructor([typeof(string), typeof(double), typeof(double),
            typeof(string), typeof(bool)]);
        Check(constructor is not null, "缺少可选 includeDiagramSource 参数");
        var preview = (MarkdownPreview)constructor!.Invoke([source, 16d, 680d, SourcePath, true]);
        Check(preview.Document.Blocks.OfType<Paragraph>().Any(p =>
            p.Tag is ReadingBlock b && MermaidBlock.IsMermaid(b)), "未保留 Mermaid 源段落");
        var model = ReadingModel.Build(source, TextKind.Markdown);
        SelectFragment(new RichDocumentMap(preview.Document, model), model.VisibleText, "/api/tasks/approve");
        var compact = new MarkdownPreview(source, 16, 680, SourcePath);
        Check(!compact.Document.Blocks.OfType<Paragraph>().Any(p =>
            p.Tag is ReadingBlock b && MermaidBlock.IsMermaid(b)), "分屏默认不应额外展开源段落");
    }

    private static void ListNavigation()
    {
        const string source = "# 阅读标题\n\n正文包含 **中文** 与第二处中文。\n\n" +
            "| 参数 | 说明 |\n| --- | --- |\n| key | 值 |\n\n- 列表\n";
        var model = ReadingModel.Build(source, TextKind.Markdown);
        var reading = RenderedReading.Markdown(model, source, SourcePath, 16, 680);
        var host = new RichTextBox { Document = reading.Document };
        Layout(host, 900);
        foreach (string token in new[] { "阅读标题", "中文", "参数", "key", "值", "列表" })
        {
            int offset = model.VisibleText.IndexOf(token, StringComparison.Ordinal);
            var start = reading.PointerAt(offset);
            var end = reading.PointerAt(offset + token.Length);
            string selected = new TextRange(start, end).Text;
            Check(selected == token && reading.OffsetAt(start) == offset,
                $"导航 {token}：期望 {offset}，实际 {reading.OffsetAt(start)}，选择 {selected.Replace("\r", "\\r").Replace("\n", "\\n")}");
        }
        const string variants = "- **首项** 后文\n- 第二项\n\n3. 有序首项\n4. 有序末项\n\n- [ ] 待办\n- [x] 完成\n\n尾段";
        var variantModel = ReadingModel.Build(variants, TextKind.Markdown);
        var variantReading = RenderedReading.Markdown(variantModel, variants, SourcePath, 16, 680);
        var variantHost = new RichTextBox { Document = variantReading.Document };
        Layout(variantHost, 900);
        var variantMap = new RichDocumentMap(variantReading.Document, variantModel);
        foreach (string token in new[] { "首项", "第二项", "有序首项", "有序末项", "待办", "完成", "尾段" })
            SelectFragment(variantMap, variantModel.VisibleText, token);
    }

    private static void MalformedImages()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--extractor-probe");
        using var child = Process.Start(start)!;
        if (!child.WaitForExit(3000))
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            throw new InvalidOperationException("100k 个未闭合图片起点扫描超过 3 秒");
        }
        Check(child.ExitCode == 0, child.StandardError.ReadToEnd());
        Console.Write(child.StandardOutput.ReadToEnd());
    }

    private static int ExtractorProbe()
    {
        try
        {
            string prefix = string.Concat(Enumerable.Repeat("![", 100_000)) + new string('x', 500_001);
            var clock = Stopwatch.StartNew();
            foreach (string suffix in new[] { "", "] invalid", "](data:image/png;base64," + new string('A', 100_000) })
            {
                string source = prefix + suffix;
                var images = MarkdownImages.Extract(source);
                Check(images.References.Count == 0 && images.Text == source && images.FallbackText(source) == source,
                    "未闭合/无效 alt 或 target 改变了源码");
            }
            Console.WriteLine($"  malformed extractor: three 700K+ inputs, {clock.ElapsedMilliseconds} ms");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void SafeFallback()
    {
        string source = LongSource();
        var preview = new MarkdownPreview("", 16, 680);
        preview.Update(source, _ => throw new InvalidOperationException("模拟构建失败"));
        string visible = Text(preview.Document);
        Check(!visible.Contains("base64,") && !visible.Contains("qyimg") && visible.Contains("[图片：长图]") &&
            visible.Contains("图后正文😀"), "构建失败将图片数据渲染为正文");
    }

    private static void DiscardedBitmap()
    {
        var budget = new MarkdownImageBudget();
        for (int i = 0; i < 4; i++)
        {
            var task = MarkdownImageLoader.LoadAsync(DataUri, null, budget, CancellationToken.None);
            PumpUntil(() => task.IsCompleted, "加载器未完成");
            var result = task.GetAwaiter().GetResult();
            Check(result.Bitmap is not null, "预备位图未解码");
            Check((object)result is IDisposable, "未被 UI 接收的位图没有释放预算的方法");
            ((IDisposable)(object)result).Dispose();
        }
        Check(budget.Reserve(64L * 1024 * 1024, 48_000_000), "丢弃解码结果耗尽文档预算");
        budget.Release(64L * 1024 * 1024, 48_000_000);
    }

    private static void SelectFragment(RichDocumentMap map, string text, string fragment)
    {
        int offset = text.IndexOf(fragment, StringComparison.Ordinal);
        Check(offset >= 0, "模型中没有待选文字：" + fragment);
        var begin = map.PointerAt(offset);
        var end = map.PointerAt(offset + fragment.Length);
        string selected = new TextRange(begin, end).Text;
        Check(selected == fragment, $"富文档无法准确选中：{fragment}，实际 {selected.Replace("\r", "\\r").Replace("\n", "\\n")}");
        Check(map.OffsetAt(begin) == offset, "富文档反向映射错误：" + fragment);
    }

    private static void NoNetwork()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string remote = $"http://127.0.0.1:{port}/image.png";
            var preview = new MarkdownPreview("![远程](" + remote + ")", 16, 680, SourcePath);
            Activate(preview.Document);
            Check(!Images(preview.Document).Any() && Text(preview.Document).Contains("[图片：远程]"), "远程图片没有保持惰性标签");
            foreach (string target in new[]
            {
                remote, @"\\127.0.0.1\share\x.png", "//127.0.0.1/share/x.png",
                "%5C%5C127.0.0.1%5Cshare%5Cx.png", "file://127.0.0.1/share/x.png",
                "%68%74%74%70%3A//127.0.0.1/x.png", "ftp://127.0.0.1/x.png"
            })
            {
                var result = Load(target);
                Check(result.Bitmap is null && result.Error!.Contains("网络"), "网络地址未拒绝：" + target);
            }
            Check(!listener.Pending(), "图片组件发起了 HTTP 请求");
        }
        finally { listener.Stop(); }
    }

    private static void Limits()
    {
        string large = Path.Combine(FixtureDirectory, "超大.png");
        using (var file = File.Create(large)) file.SetLength(MarkdownImageLoader.MaximumBytes + 1L);
        Check(Load(large).Error!.Contains("16 MiB"), "没有拒绝超大文件");
        var header = (byte[])Png.Clone();
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16, 4), 8000);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20, 4), 8000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(29, 4), Crc32(header.AsSpan(12, 17)));
        string hugePixels = Path.Combine(FixtureDirectory, "超像素.png");
        File.WriteAllBytes(hugePixels, header);
        Check(Load(hugePixels).Error!.Contains("像素"), "没有在完整解码前拒绝超像素图");
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16, 4), 40000);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(29, 4), Crc32(header.AsSpan(12, 17)));
        File.WriteAllBytes(hugePixels, header);
        Check(Load(hugePixels).Error!.Contains("像素"), "没有拒绝超长边图片");
        var many = new MarkdownPreview(string.Join("\n\n", Enumerable.Repeat("![多图](" + DataUri + ")", 34)), 16, 680);
        Check(Status(many.Document).Count(t => t.Contains("32 张")) == 2, "文档控件数量未限制");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var task = MarkdownImageLoader.LoadAsync(DataUri, null, new MarkdownImageBudget(), canceled.Token);
        PumpUntil(() => task.IsCompleted, "取消任务未完成");
        Check(task.IsCanceled, "取消未生效");
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0);
        }
        return ~crc;
    }

    private static void Downsample()
    {
        var bitmap = BitmapSource.Create(9000, 200, 96, 96, PixelFormats.Gray8, null, new byte[9000 * 200], 9000);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var result = Load("data:image/png;base64," + Convert.ToBase64String(stream.ToArray()));
        Check(result.Bitmap is { IsFrozen: true, PixelWidth: 4096 } && result.Bitmap.PixelHeight <= 92,
            "宽图未限制解码尺寸");
    }

    private static void Responsive()
    {
        var preview = new MarkdownPreview("![比例](" + DataUri + ")", 16, 680);
        AssertLoaded(preview.Document, "比例图片");
        var image = Images(preview.Document).Single();
        Layout(preview, 900);
        double wide = image.ActualWidth;
        Layout(preview, 320);
        double narrow = image.ActualWidth;
        Check(wide > narrow && narrow > 100 && narrow <= 272, $"宽度未自适应：{wide} -> {narrow}");
        Check(Math.Abs(image.ActualWidth / image.ActualHeight - 2) < .01, "图片失去原始宽高比");
        Layout(preview, 900);
        Check(image.ActualWidth > narrow, "变宽后图片未恢复");
        var bitmap = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(preview);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(FixtureDirectory, "图片预览.png"));
        encoder.Save(output);
        var pixels = new byte[900 * 700 * 4];
        bitmap.CopyPixels(pixels, 900 * 4, 0);
        int colorful = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (Math.Abs(pixels[i] - pixels[i + 2]) > 100 && pixels[i + 3] > 0) colorful++;
        Check(colorful > 20_000, "离屏预览没有真实彩色图片像素");
    }

    private static void Replacement()
    {
        var preview = new MarkdownPreview("![图](" + DataUri + ")", 16, 680);
        Activate(preview.Document);
        var old = preview.Document;
        foreach (var control in Controls(old.Blocks))
            control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        preview.Update("# 新正文\n\n不能被旧任务覆盖");
        Check(!Images(preview.Document).Any() && !preview.IsDocumentEnabled, "文档更新残留图片状态");
        preview.Update("![恢复](" + DataUri + ")");
        AssertLoaded(preview.Document, "更新后重新加载");
        Check(!Text(preview.Document).Contains("base64,"), "更新后出现 Base64 正文");
    }

    private static void RealSample(string path)
    {
        string source = File.ReadAllText(path);
        var clock = Stopwatch.StartNew();
        var preview = new MarkdownPreview(source, 16, 680, path);
        var image = AssertLoaded(preview.Document, Path.GetFileName(path));
        var bitmap = (BitmapSource)image.Source;
        Check(bitmap.PixelWidth == 2880 && bitmap.PixelHeight is 3660 or 2880 or 3760, "真实样本图片尺寸不符");
        Check(!Text(preview.Document).Contains("base64,") && !Text(preview.Document).Contains("qyimg"), "真实样本退化为源文");
        Check(preview.Document.Blocks.OfType<Table>().Any(), "真实样本漂亮表格丢失");
        // The mapped renderer retains whichever authoritative model Core chose. The host
        // uses the rich preview for full MD reading, including styled tables and lists.
        var model = ReadingModel.Build(source, TextKind.Markdown);
        var reading = new RenderedReading(model, sourcePath: path, sourceText: source);
        AssertLoaded(reading.Document, "mapped " + Path.GetFileName(path));
        Roundtrip(reading, model);
        var rich = RenderedReading.Markdown(model, source, path, 16, 680);
        AssertLoaded(rich.Document, "full reading " + Path.GetFileName(path));
        foreach (var heading in model.Headings.Take(3))
        {
            var start = rich.PointerAt(heading.DisplayStart);
            var end = rich.PointerAt(heading.DisplayStart + heading.Title.Length);
            Check(new TextRange(start, end).Text == heading.Title, "真实样本标题无法选中");
        }
        Check(File.ReadAllText(path) == source, "验收改变了真实 Markdown 文件");
        Console.WriteLine($"  {source.Length:N0} chars, {bitmap.PixelWidth}x{bitmap.PixelHeight}, {clock.ElapsedMilliseconds} ms");
    }

    private static void NoImageSample(string path)
    {
        string source = File.ReadAllText(path);
        Check(MarkdownImages.Extract(source).References.Count == 0, "02 样本意外包含图片");
        var preview = new MarkdownPreview(source, 16, 680, path);
        Check(!Images(preview.Document).Any() && Text(preview.Document).Length > 0, "02 无图文档打开失败");
        var model = ReadingModel.Build(source, TextKind.Markdown);
        Check(RenderedReading.Markdown(model, source, path, 16, 680).Document.Blocks.Count > 0,
            "02 完整阅读打开失败");
        Check(File.ReadAllText(path) == source, "验收改变了 02 原文件");
    }

    private static void Roundtrip(RenderedReading reading, ReadingModel model)
    {
        for (int i = 0; i <= model.VisibleText.Length; i++)
        {
            if (i > 0 && i < model.VisibleText.Length && char.IsHighSurrogate(model.VisibleText[i - 1]) &&
                char.IsLowSurrogate(model.VisibleText[i])) continue;
            Check(reading.OffsetAt(reading.PointerAt(i)) == i, $"图片插入破坏映射：{i}");
        }
    }

    private static Image AssertLoaded(FlowDocument document, string message, int index = 0)
    {
        var image = Images(document).ElementAtOrDefault(index);
        Check(image is not null, "缺少真实 Image：" + message);
        Activate(document);
        PumpUntil(() => image!.Source is not null || Status(document).Any(t => t.StartsWith("图片加载失败")), message);
        Check(image!.Source is BitmapSource { IsFrozen: true }, message + "：" + string.Join("; ", Status(document)));
        return image;
    }

    private static IEnumerable<string> Status(FlowDocument document) => Controls(document.Blocks)
        .SelectMany(c => Descendants<TextBlock>(c)).Select(t => t.Text);

    private static MarkdownImageResult Load(string target)
    {
        var task = MarkdownImageLoader.LoadAsync(target, SourcePath, new MarkdownImageBudget(), CancellationToken.None);
        PumpUntil(() => task.IsCompleted, "加载器未完成");
        return task.GetAwaiter().GetResult();
    }

    private static void Layout(FrameworkElement element, double width)
    {
        element.Measure(new Size(width, 700));
        element.Arrange(new Rect(0, 0, width, 700));
        element.UpdateLayout();
    }

    private static byte[] MakePng()
    {
        var pixels = new byte[640 * 320 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i / 4 % 640 < 320 ? 230 : 20);
            pixels[i + 2] = (byte)(i / 4 % 640 < 320 ? 20 : 230);
        }
        var bitmap = BitmapSource.Create(640, 320, 96, 96, PixelFormats.Bgr32, null, pixels, 640 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static IEnumerable<FrameworkElement> Controls(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is BlockUIContainer container && container.Child is FrameworkElement root) yield return root;
            if (block is Paragraph paragraph)
                foreach (var control in Controls(paragraph.Inlines)) yield return control;
            if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems)
                    foreach (var control in Controls(item.Blocks)) yield return control;
            if (block is Table table)
                foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var control in Controls(cell.Blocks)) yield return control;
        }
    }

    private static IEnumerable<FrameworkElement> Controls(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is InlineUIContainer container && container.Child is FrameworkElement control) yield return control;
            if (inline is Span span)
                foreach (var child in Controls(span.Inlines)) yield return child;
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T typed) yield return typed;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var match in Descendants<T>(child)) yield return match;
    }

    private static IEnumerable<Image> Images(FlowDocument document) =>
        Controls(document.Blocks).SelectMany(c => Descendants<Image>(c));

    private static void Activate(FlowDocument document)
    {
        foreach (var control in Controls(document.Blocks))
            foreach (var element in Descendants<FrameworkElement>(control).ToArray())
                element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
    }

    private static void PumpUntil(Func<bool> condition, string message, int seconds = 15)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Check(clock.Elapsed < TimeSpan.FromSeconds(seconds), message);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
    }

    private static string Text(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Test(string name, Action body)
    {
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
    }
}
