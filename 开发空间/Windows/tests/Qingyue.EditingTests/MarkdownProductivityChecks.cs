using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Qingyue.Core;
using Qingyue.Windows;

internal static partial class Program
{
    private static async Task RunMarkdownProductivityChecks(MainWindow window, string root)
    {
        string directory = Path.Combine(root, "图片 中文 #)");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "工作 笔记.md");
        File.WriteAllText(path, "# 笔记\n");
        BitmapSource Image(int width = 3, int height = 2)
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat((byte)180, checked(width * height * 4)).ToArray(), width * 4);
            bitmap.Freeze();
            return bitmap;
        }
        async Task<object> Write(BitmapSource image, string document,
            string suggestedName = "图片", CancellationToken token = default)
        {
            var type = typeof(MainWindow).Assembly.GetType("Qingyue.Windows.MarkdownImageInsertion")
                ?? throw new Exception("缺少本地图片附件写入器");
            var method = type.GetMethod("WriteAsync", BindingFlags.Static | BindingFlags.Public,
                binder: null,
                types: [typeof(BitmapSource), typeof(string), typeof(string), typeof(CancellationToken)],
                modifiers: null) ?? throw new Exception("缺少带建议名的本地图片附件写入器");
            var task = (Task)(method
                .Invoke(null, [image, document, suggestedName, token])!);
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        async Task<bool> Insert(BitmapSource image, CancellationToken token = default)
        {
            var method = typeof(MainWindow).GetMethod("InsertMarkdownImageAsync")
                ?? throw new Exception("缺少截图插入入口");
            return await (Task<bool>)method.Invoke(window, [image, token])!;
        }
        string Value(object item, string name) => (string)item.GetType().GetProperty(name)!.GetValue(item)!;
        await ProductCheck("图片选择器严格采用 macOS 六类格式且不超过八张", async () =>
        {
            Equal(MainWindow.IsSupportedImage("a.png"), true);
            Equal(MainWindow.IsSupportedImage("a.JPG"), true);
            Equal(MainWindow.IsSupportedImage("a.jpeg"), true);
            Equal(MainWindow.IsSupportedImage("a.tif"), true);
            Equal(MainWindow.IsSupportedImage("a.TIFF"), true);
            Equal(MainWindow.IsSupportedImage("a.gif"), true);
            Equal(MainWindow.IsSupportedImage("a.bmp"), true);
            Equal(MainWindow.IsSupportedImage("a.heic"), true);
            Equal(MainWindow.IsSupportedImage("a.webp"), false);
            Equal(MainWindow.IsSupportedImage("a.txt"), false);
            foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".gif", ".bmp", ".heic" })
                Equal(MainWindow.MarkdownImageFileFilter.Contains("*" + extension, StringComparison.Ordinal), true);
            Equal(MainWindow.MarkdownImageFileFilter.Contains("*.*", StringComparison.Ordinal), false);
            Equal(MainWindow.MarkdownImageFileFilter.Contains(".webp", StringComparison.Ordinal), false);
            await Task.CompletedTask;
        });
        await ProductCheck("图片附件真实落盘，中文与特殊路径使用相对链接", async () =>
        {
            var asset = await Write(Image(), path);
            string file = Value(asset, "FilePath"), markdown = Value(asset, "Markdown");
            Equal(File.Exists(file), true);
            Equal(Path.GetDirectoryName(file), Path.Combine(directory, "工作 笔记.assets"));
            Equal(markdown.StartsWith("![图片](%E5%B7%A5%E4%BD%9C%20%E7%AC%94%E8%AE%B0.assets/", StringComparison.Ordinal), true);
            using var stream = File.OpenRead(file);
            var decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Equal(decoded.PixelWidth, 3);
            Equal(decoded.PixelHeight, 2);
        });
        await ProductCheck("重复插图只创建新附件，已有附件内容保持不变", async () =>
        {
            var first = await Write(Image(), path);
            var bytes = File.ReadAllBytes(Value(first, "FilePath"));
            var second = await Write(Image(), path);
            Equal(Value(first, "FilePath") == Value(second, "FilePath"), false);
            Equal(File.ReadAllBytes(Value(first, "FilePath")).SequenceEqual(bytes), true);
        });
        await ProductCheck("取消与超像素图片不创建附件，宽幅图片不再受单边限制", async () =>
        {
            string doc = Path.Combine(directory, "取消.md");
            File.WriteAllText(doc, "");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await Write(Image(), doc, token: cancellation.Token); throw new Exception("已取消写入仍成功"); }
            catch (OperationCanceledException) { }
            Equal(Directory.Exists(Path.Combine(directory, "取消.assets")), false);
            var wide = await Write(Image(9000, 1), doc);
            Equal(File.Exists(Value(wide, "FilePath")), true);
            try { await Write(Image(4001, 4000), doc); throw new Exception("超像素图片仍成功"); }
            catch (ArgumentException) { }
        });
        await ProductCheck("附件目录被同名文件占用时失败且保留原文件", async () =>
        {
            string doc = Path.Combine(directory, "占用.md");
            File.WriteAllText(doc, "");
            string collision = Path.Combine(directory, "占用.assets");
            File.WriteAllText(collision, "用户原有文件");
            try { await Write(Image(), doc); throw new Exception("目录冲突仍成功"); }
            catch (IOException) { }
            Equal(File.ReadAllText(collision), "用户原有文件");
        });
        await ProductCheck("编码超过8MiB的图片不会生成残留附件", async () =>
        {
            string doc = Path.Combine(directory, "编码预算.md");
            File.WriteAllText(doc, "");
            var pixels = new byte[2000 * 1500 * 4];
            new Random(53).NextBytes(pixels);
            var bitmap = BitmapSource.Create(2000, 1500, 96, 96, PixelFormats.Bgra32, null, pixels, 2000 * 4);
            bitmap.Freeze();
            try { await Write(bitmap, doc); throw new Exception("超编码预算仍成功"); }
            catch (IOException) { }
            Equal(Directory.Exists(Path.Combine(directory, "编码预算.assets")), false);
        });
        await ProductCheck("附件文件名安全清理、ALT转义与80字符限制匹配macOS", async () =>
        {
            string doc = Path.Combine(directory, "命名.md");
            File.WriteAllText(doc, "");
            var special = await Write(Image(), doc, "a]b[c\\d\n截 图#1");
            string file = Value(special, "FilePath"), markdown = Value(special, "Markdown");
            Equal(Path.GetFileName(file).StartsWith("a-b-c-d-截-图-1-", StringComparison.Ordinal), true);
            Equal(markdown.StartsWith("![a\\]b\\[c\\\\d 截 图#1](", StringComparison.Ordinal), true);
            var empty = await Write(Image(), doc, "");
            Equal(Path.GetFileName(Value(empty, "FilePath")).StartsWith("image-", StringComparison.Ordinal), true);
            Equal(Value(empty, "Markdown").StartsWith("![图片](", StringComparison.Ordinal), true);
            string longName = new('中', 100);
            var limited = await Write(Image(), doc, longName);
            string limitedName = Path.GetFileName(Value(limited, "FilePath"));
            Equal(limitedName.Length, 80 + 1 + 32 + 4);
        });

        await window.OpenPathsAsync([path]);
        if (window.CurrentTab!.IsReading) window.ToggleReading();
        var tab = window.CurrentTab!;
        tab.Editor.CaretIndex = tab.Editor.Text.Length;
        await Idle(window);
        await ProductCheck("截图插入可一次撤销重做，原文只在明确保存后写入", async () =>
        {
            string before = tab.Editor.Text;
            int attachments = Directory.GetFiles(Path.Combine(directory, "工作 笔记.assets"), "*.png").Length;
            Equal(await Insert(Image()), true);
            string inserted = tab.Editor.Text;
            Equal(inserted.Contains("![pasted-image](", StringComparison.Ordinal), true);
            Equal(File.ReadAllText(path), "# 笔记\n");
            tab.Editor.Undo();
            Equal(tab.Editor.Text, before);
            tab.Editor.Redo();
            Equal(tab.Editor.Text, inserted);
            Equal(Directory.GetFiles(Path.Combine(directory, "工作 笔记.assets"), "*.png").Length,
                attachments + 1);
        });
        await ProductCheck("图片后台写入期间正文变化则丢弃结果并清理本次附件", async () =>
        {
            int before = Directory.GetFiles(Path.Combine(directory, "工作 笔记.assets")).Length;
            var pending = Insert(Image());
            tab.Editor.AppendText("\n用户继续编写");
            Equal(await pending, false);
            Equal(Directory.GetFiles(Path.Combine(directory, "工作 笔记.assets")).Length, before);
            Equal(tab.Editor.Text.EndsWith("用户继续编写", StringComparison.Ordinal), true);
        });
        await ProductCheck("非Markdown文档不接管截图插入", async () =>
        {
            tab.Kind = TextKind.Plain;
            Equal(await Insert(Image()), false);
            tab.Kind = TextKind.Markdown;
        });
        Test("表格工具栏操作保留单元格选区并可一次撤销", () =>
        {
            tab.Editor.Text = "| A | B |\n| --- | --- |\n| 甲 | 乙 |";
            tab.Editor.CaretIndex = tab.Editor.Text.IndexOf("甲", StringComparison.Ordinal);
            var actionType = typeof(DocumentFile).Assembly.GetType("Qingyue.Core.MarkdownTableAction")
                ?? throw new Exception("缺少表格编辑动作");
            var execute = tab.MarkdownWriter.GetType().GetMethod("ExecuteTable")
                ?? throw new Exception("缺少WPF表格命令");
            string before = tab.Editor.Text;
            Equal(execute.Invoke(tab.MarkdownWriter, [Enum.Parse(actionType, "NextCell")]), true);
            Equal(tab.Editor.SelectedText, "乙");
            execute.Invoke(tab.MarkdownWriter, [Enum.Parse(actionType, "InsertRow")]);
            Equal(tab.Editor.Text.Count(c => c == '\n'), 3);
            tab.Editor.Undo();
            Equal(tab.Editor.Text, before);
        });
        Test("表格真实Tab和Enter事件走结构操作且组合输入不接管", () =>
        {
            tab.Editor.Text = "| A | B |\n| --- | --- |\n| 甲 | 乙 |";
            tab.Editor.CaretIndex = tab.Editor.Text.IndexOf("甲", StringComparison.Ordinal);
            var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(tab.Editor), 0, System.Windows.Input.Key.Tab)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            tab.Editor.RaiseEvent(key);
            Equal(key.Handled, true);
            Equal(tab.Editor.SelectedText, "乙");
            var enter = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(tab.Editor), 0, System.Windows.Input.Key.Enter)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            tab.Editor.RaiseEvent(enter);
            Equal(enter.Handled, true);
            Equal(tab.Editor.Text.Count(c => c == '\n'), 3);
            var composition = new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, tab.Editor, "中");
            tab.Editor.RaiseEvent(new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, composition)
                { RoutedEvent = System.Windows.Input.TextCompositionManager.PreviewTextInputStartEvent });
            string before = tab.Editor.Text;
            Equal(tab.MarkdownWriter.ExecuteTable(MarkdownTableAction.InsertRow), false);
            Equal(tab.Editor.Text, before);
            tab.Editor.RaiseEvent(new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, composition)
                { RoutedEvent = System.Windows.Input.TextCompositionManager.PreviewTextInputEvent });
        });
        Test("显式粘贴TSV为表格可撤销，普通文本粘贴事件保持原生", () =>
        {
            tab.Editor.Text = "";
            Equal(tab.MarkdownWriter.PasteTable("名称\t状态\r\n中文\t完成"), true);
            Equal(tab.Editor.Text.ReplaceLineEndings("\n"), "| 名称 | 状态 |\n| --- | --- |\n| 中文 | 完成 |");
            tab.Editor.Undo();
            Equal(tab.Editor.Text, "");
            var plain = new DataObject(DataFormats.UnicodeText, "普通文本");
            var paste = new DataObjectPastingEventArgs(plain, false, DataFormats.UnicodeText);
            tab.Editor.RaiseEvent(paste);
            Equal(paste.CommandCancelled, false);
        });
        await ProductCheck("合成截图粘贴事件真实写附件并插入Markdown", async () =>
        {
            tab.Editor.Text = "# 截图\n\n";
            tab.Editor.CaretIndex = tab.Editor.Text.Length;
            var data = new DataObject(DataFormats.Bitmap, Image());
            var paste = new DataObjectPastingEventArgs(data, false, DataFormats.Bitmap);
            tab.Editor.RaiseEvent(paste);
            Equal(paste.CommandCancelled, true);
            for (int i = 0; i < 100 && !tab.Editor.Text.Contains("![pasted-image](", StringComparison.Ordinal); i++)
            { await Task.Delay(20); await Idle(window); }
            Equal(tab.Editor.Text.Contains("![pasted-image](", StringComparison.Ordinal), true);
        });
        Test("剪贴板同时含图片和文本时保留原生文本粘贴", () =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, Image());
            data.SetData(DataFormats.UnicodeText, "网页原文");
            var paste = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText);
            tab.Editor.RaiseEvent(paste);
            Equal(paste.CommandCancelled, false);
        });
        await ProductCheck("截图相对链接在实际预览显示并可内嵌导出", async () =>
        {
            var preview = new MarkdownPreview(tab.Editor.Text, 16, 680, path);
            var host = new Window { Content = preview, Width = 720, Height = 450 };
            host.Show();
            try
            {
                static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
                {
                    if (parent is T typed) yield return typed;
                    foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
                        foreach (var match in Descendants<T>(child)) yield return match;
                }
                for (int i = 0; i < 100 && !Descendants<System.Windows.Controls.Image>(preview.Document)
                    .Any(image => image.Source is BitmapSource); i++)
                { await Task.Delay(20); await Idle(window); }
                var bitmap = Descendants<System.Windows.Controls.Image>(preview.Document)
                    .Select(image => image.Source).OfType<BitmapSource>().Single();
                Equal(bitmap.PixelWidth, 3);
                Equal(bitmap.PixelHeight, 2);
                var exported = ReadingHtmlExport.CreateDocument(preview, "截图验证")!;
                Equal(exported.EmbeddedImages, 1);
                Equal(exported.OmittedImages, 0);
                Equal(exported.Html.Contains("data:image/png;base64,", StringComparison.Ordinal), true);
            }
            finally { host.Close(); }
        });
        await ProductCheck("选图后台读取期间切换标签不会插入到新文档", async () =>
        {
            string selected = Directory.GetFiles(Path.Combine(directory, "工作 笔记.assets"), "*.png")[0];
            string before = tab.Editor.Text;
            int attachments = Directory.GetFiles(Path.Combine(directory, "工作 笔记.assets")).Length;
            var task = window.InsertMarkdownImageFileAsync(selected);
            var other = window.NewDocument("# 新标签");
            Equal(await task, false);
            Equal(other.Editor.Text, "# 新标签");
            Equal(tab.Editor.Text, before);
            Equal(Directory.GetFiles(Path.Combine(directory, "工作 笔记.assets")).Length, attachments);
            window.SelectTab(tab);
        });
        await ProductCheck("已取消的未保存文档插图不弹选址且不改文", async () =>
        {
            var unsaved = window.NewDocument("# 未保存");
            unsaved.Kind = TextKind.Markdown;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Equal(await Insert(Image(), cancellation.Token), false);
            Equal(unsaved.File.Path, null);
            Equal(unsaved.Editor.Text, "# 未保存");
            window.SelectTab(tab);
        });
        Test("原生TextBox悬挂缩进可行性验证：附加TextIndent不改变软换行布局", () =>
        {
            var probe = new TextBox
            {
                Text = "- " + new string('中', 180), TextWrapping = TextWrapping.Wrap,
                Width = 250, Height = 180, FontSize = 16
            };
            var host = new Window { Content = probe, Width = 280, Height = 230 };
            host.Show();
            try
            {
                host.UpdateLayout();
                int offset = probe.GetCharacterIndexFromLineIndex(1);
                double original = probe.GetRectFromCharacterIndex(offset).Left;
                probe.SetValue(System.Windows.Documents.Paragraph.TextIndentProperty, 32d);
                host.UpdateLayout();
                Equal(probe.GetRectFromCharacterIndex(offset).Left, original);
            }
            finally { host.Close(); }
        });
    }

    private static async Task ProductCheck(string name, Func<Task> action)
    {
        try { await action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + "\n" + ex); }
    }
}
