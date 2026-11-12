using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Qingyue.Core;
using Qingyue.Windows;

internal static partial class Program
{
    private static partial async Task RunAdditionalTests(MainWindow window, string root)
    {
        await CoreSafety(root);
        await EncodingSafety(root);
        await ViewState(window, root);
        await ReadingAnchorFollowsInsertedPrefix(window, root);
        await SearchFocusDuringRefresh(window, root);
        await NoticesAndRetry(window, root);
        await ActivationStorm(window, root);
    }

    private static async Task CoreSafety(string root)
    {
        string path = Path.Combine(root, "core.txt");
        File.WriteAllText(path, "original");
        var file = DocumentFile.Open(path);
        var id = file.Id;
        var timestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "external");
        File.SetLastWriteTimeUtc(path, timestamp);
        var result = await new ExternalFileRefresh(file).ReadAsync();
        Check(result.Status == ExternalRefreshStatus.Changed && result.TryApply(),
            "相同大小时间戳仍按摘要发现外部变化");
        Check(file.Id == id && file.Text == "external" && !file.IsDirty && !file.DiskChanged(),
            "原地接纳保留ID并更新正文与磁盘基线");
        file.Text = "local edit";
        file.Save();
        Check(File.ReadAllText(path) == "local edit", "刷新后正常保存使用新基线");
        File.WriteAllText(path, "external 2");
        file.Text = "keep edits";
        var before = file.CaptureSnapshot();
        result = await new ExternalFileRefresh(file).ReadAsync();
        Check(result.Status == ExternalRefreshStatus.Conflict && !result.TryApply()
            && file.CaptureSnapshot() == before, "脏文档仅报告冲突，正文编码与基线完全保留");
        Throws<FileConflictException>(() => file.Save(), "脏文档刷新后保存仍拦截外部冲突");

        file = DocumentFile.Open(path);
        File.WriteAllText(path, "candidate");
        result = await new ExternalFileRefresh(file).ReadAsync();
        file.Text = "typing";
        file.Text = "external 2";
        Check(!result.TryApply() && file.Text == "external 2",
            "输入后撤回相同正文也淘汰异步旧结果");

        result = await new ExternalFileRefresh(file).ReadAsync();
        before = file.CaptureSnapshot();
        file.Save(allowOverwrite: true);
        Check(file.CaptureSnapshot() == before && !result.TryApply(),
            "相同内容再次保存也淘汰保存前的旧读盘结果");

        File.WriteAllText(path, "first disk");
        var first = await new ExternalFileRefresh(file).ReadAsync();
        File.WriteAllText(path, "later disk");
        var later = await new ExternalFileRefresh(file).ReadAsync();
        Check(later.TryApply() && !first.TryApply() && file.Text == "later disk",
            "较旧结果不能覆盖已经接纳的新版本");
        File.WriteAllText(path, "rename race");
        result = await new ExternalFileRefresh(file).ReadAsync();
        string renamed = Path.Combine(root, "renamed-core.txt");
        file.Save(allowOverwrite: true);
        file.Rename(Path.GetFileName(renamed));
        Check(!result.TryApply() && file.Path == renamed, "重命名后的文档拒绝旧路径结果");
        path = renamed;

        var unchanged = file.CaptureSnapshot();
        File.Delete(path);
        result = await new ExternalFileRefresh(file).ReadAsync();
        Check(result.Status == ExternalRefreshStatus.Unavailable && !result.TryApply()
            && file.CaptureSnapshot() == unchanged, "外部删除不清空正文或改变基线");
        Throws<FileConflictException>(() => file.Save(), "外部删除后原位保存仍要求明确覆盖");
        File.WriteAllText(path, "recreated");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await new ExternalFileRefresh(file).ReadAsync();
            Check(result.Status == ExternalRefreshStatus.Unavailable && !result.TryApply()
                && file.CaptureSnapshot() == unchanged, "独占占用失败不污染正文基线");
        }
        result = await new ExternalFileRefresh(file).ReadAsync();
        Check(result.TryApply() && file.Text == "recreated", "删除重建及解除占用后可重试成功");
        unchanged = file.CaptureSnapshot();
        File.WriteAllBytes(path, [0, 1, 2]);
        result = await new ExternalFileRefresh(file).ReadAsync();
        Check(result.Status == ExternalRefreshStatus.Unavailable && !result.TryApply()
            && file.CaptureSnapshot() == unchanged, "外部二进制拒绝刷新且不抹正文");
        using (var large = new FileStream(path, FileMode.Create))
            large.SetLength(DocumentFile.MaximumSize + 1L);
        result = await new ExternalFileRefresh(file).ReadAsync();
        Check(result.Status == ExternalRefreshStatus.Unavailable && !result.TryApply()
            && file.CaptureSnapshot() == unchanged, "外部超限文件复用50MiB安全读取上限");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await new ExternalFileRefresh(file).ReadAsync(cancellation.Token);
            throw new Exception("取消请求未被取消");
        }
        catch (OperationCanceledException) { Check(true, "已取消的异步检查不会接纳磁盘结果"); }
        result = await new ExternalFileRefresh(new DocumentFile("draft")).ReadAsync();
        Check(result.Status == ExternalRefreshStatus.Skipped, "无已保存路径的草稿不访问磁盘");
    }

    private static async Task EncodingSafety(string root)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (var (codec, encoding) in new (TextCodec, Encoding)[]
        {
            (TextCodec.Utf8, new UTF8Encoding(false, true)),
            (TextCodec.Utf8Bom, new UTF8Encoding(true, true)),
            (TextCodec.Utf16LE, new UnicodeEncoding(false, true, true)),
            (TextCodec.Utf16BE, new UnicodeEncoding(true, true, true)),
            (TextCodec.Utf16LE, new UnicodeEncoding(false, false, true)),
            (TextCodec.Utf16BE, new UnicodeEncoding(true, false, true)),
            (TextCodec.Gb18030, Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback))
        })
        {
            string path = Path.Combine(root, "codec-" + Guid.NewGuid() + ".txt");
            File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes("甲\r\n旧")).ToArray());
            var file = DocumentFile.Open(path, codec);
            byte[] changed = encoding.GetPreamble().Concat(encoding.GetBytes("乙\r\n新\r尾\n")).ToArray();
            File.WriteAllBytes(path, changed);
            var result = await new ExternalFileRefresh(file).ReadAsync();
            Check(result.TryApply() && file.Codec == codec && file.LineEnding == LineEnding.Mixed
                && file.Text == "乙\n新\n尾\n" && !file.IsDirty, $"保留{codec}及BOM={encoding.GetPreamble().Length > 0}读取语义");
            file.Save();
            Check(File.ReadAllBytes(path).SequenceEqual(changed), "混合换行原字节与BOM无损保存");
            File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes("another\r\nversion")).ToArray());
            Throws<FileConflictException>(() => file.Save(), "混合换行刷新后仍保留后续磁盘冲突检查");
        }
        string bomPath = Path.Combine(root, "remove-bom.txt");
        File.WriteAllText(bomPath, "before", new UTF8Encoding(true));
        var bomFile = DocumentFile.Open(bomPath);
        File.WriteAllText(bomPath, "after", new UTF8Encoding(false));
        var bomResult = await new ExternalFileRefresh(bomFile).ReadAsync();
        Check(bomResult.TryApply() && bomFile.Codec == TextCodec.Utf8, "外部移除UTF8 BOM不会在保存时意外重新添加");
        bomFile.Save();
        Check(File.ReadAllBytes(bomPath).SequenceEqual("after"u8.ToArray()), "无BOM新基线可逐字节保存");
    }

    private static async Task ViewState(MainWindow window, string root)
    {
        string path = Path.Combine(root, "view.txt");
        string text = string.Join("\n", Enumerable.Range(0, 260).Select(i => $"{i:0000} " + new string('x', 220)));
        File.WriteAllText(path, text);
        await window.OpenPathsAsync([path]);
        var tab = window.CurrentTab!;
        var file = tab.File;
        var editor = tab.Editor;
        var id = file.Id;
        tab.Editor.TextWrapping = TextWrapping.NoWrap;
        tab.Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        await Idle(window);
        tab.Editor.Select(1500, 12);
        tab.Editor.ScrollToVerticalOffset(380);
        tab.Editor.ScrollToHorizontalOffset(100);
        await Idle(window);
        double vertical = tab.Editor.VerticalOffset, horizontal = tab.Editor.HorizontalOffset;
        File.WriteAllText(path, text.Replace("x", "y"));
        window.QueueExternalRefresh(tab);
        await WaitFor(() => tab.File.Text.Contains('y'), "活动编辑标签刷新正文");
        await Idle(window);
        Check(ReferenceEquals(tab.File, file) && ReferenceEquals(tab.Editor, editor) && tab.File.Id == id,
            "保留同一DocumentFile、Editor和ID");
        Check(tab.Editor.SelectionStart == 1500 && tab.Editor.SelectionLength == 12,
            "编辑选区保持原UTF16偏移");
        Check(Math.Abs(tab.Editor.VerticalOffset - vertical) < 2 && Math.Abs(tab.Editor.HorizontalOffset - horizontal) < 2,
            "编辑水平和垂直滚动位置保留");
        Check(!tab.Editor.CanUndo && !tab.File.IsDirty, "外部接纳清除旧撤销栈且不会标脏");
        tab.Editor.AppendText("\nedit");
        Check(tab.File.IsDirty, "外部接纳之后正常输入仍然标脏");
        window.SaveTabToPath(tab, path);
        Check(!tab.File.IsDirty, "外部接纳之后窗口保存正常");
        if (Environment.GetCommandLineArgs().Contains("--without-md")) return;

        path = Path.Combine(root, "reading.md");
        text = "# Old title\n\n" + string.Join("\n\n", Enumerable.Repeat("正文 **中文** text", 80));
        File.WriteAllText(path, text);
        await window.OpenPathsAsync([path]);
        tab = window.CurrentTab!;
        tab.Editor.Select(30, 4);
        if (!tab.IsReading) window.ToggleReading();
        await Idle(window);
        var pointer = tab.Rendered!.PointerAt(35);
        var end = tab.Rendered.PointerAt(40);
        tab.Preview.Selection.Select(pointer, end);
        int startSource = tab.Reading!.DisplayToSource(tab.Rendered.OffsetAt(pointer));
        int endSource = tab.Reading.DisplayToSource(tab.Rendered.OffsetAt(end));
        tab.Preview.ScrollToVerticalOffset(240);
        await Idle(window);
        vertical = tab.Preview.VerticalOffset;
        var outline = (ListBox)window.FindName("OutlineList");
        Check(outline.SelectedIndex == 0, "刷新前滚动已自动选中目录，覆盖重绑选中项的回归条件");
        var returnAnchor = tab.ReturnAnchor;
        double returnDelta = tab.ReturnDelta;
        File.WriteAllText(path, text.Replace("Old", "New"));
        window.QueueExternalRefresh(tab);
        await WaitFor(() => tab.Reading?.Headings.FirstOrDefault()?.Title == "New title", "阅读预览和目录以新磁盘内容重建");
        await Idle(window);
        Check(tab.IsReading && tab.Preview.IsReadOnly && !tab.File.IsDirty, "外部刷新保留只读阅读模式");
        int actualStartSource = tab.Reading!.DisplayToSource(tab.Rendered!.OffsetAt(tab.Preview.Selection.Start));
        int actualEndSource = tab.Reading.DisplayToSource(tab.Rendered.OffsetAt(tab.Preview.Selection.End));
        Check(actualStartSource == startSource && actualEndSource == endSource,
            $"阅读选区按源文偏移保留 (expected={startSource}:{endSource}, actual={actualStartSource}:{actualEndSource}, " +
            $"roundtrip={tab.Reading.DisplayToSource(tab.Reading.SourceToDisplay(startSource))}:" +
            $"{tab.Reading.DisplayToSource(tab.Reading.SourceToDisplay(endSource))})");
        Check(Math.Abs(tab.Preview.VerticalOffset - vertical) < 2, "阅读滚动位置保留");
        Check(tab.ReturnAnchor == returnAnchor && tab.ReturnDelta == returnDelta,
            "目录重绑不会被当作用户导航记录返回位置");
        outline.SelectedItem = null;
        outline.SelectedItem = tab.Reading.Headings[0];
        Check(tab.Rendered.OffsetAt(tab.Preview.Selection.Start) == 0
            && tab.Preview.Selection.IsEmpty && tab.ReturnAnchor is not null,
            "外部刷新结束后用户仍可通过目录正常导航");
        window.ToggleReading();
        Check(!tab.IsReading && !tab.Editor.IsReadOnly && tab.Editor.Text.Contains("New title"), "刷新后可正常返回编辑");

        typeof(MainWindow).GetMethod("ToggleMarkdownPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);
        await Idle(window);
        File.WriteAllText(path, text.Replace("Old", "MD!"));
        window.QueueExternalRefresh(tab);
        await WaitFor(() => tab.File.Text.Contains("MD!"), "Markdown分屏接纳新正文");
        await WaitFor(() =>
        {
            var pane = (MarkdownPreview?)typeof(MainWindow).GetField("markdownPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
            return pane is not null && new TextRange(pane.Document.ContentStart, pane.Document.ContentEnd).Text.Contains("MD!");
        }, "Markdown分屏预览重建且继续显示");
    }

    private static async Task ReadingAnchorFollowsInsertedPrefix(MainWindow window, string root)
    {
        string path = Path.Combine(root, "anchor.md");
        string before = "# 开始\n\n目标段落\n第二行\n\n结尾";
        File.WriteAllText(path, before);
        await window.OpenPathsAsync([path]);
        var tab = window.CurrentTab!;
        if (!tab.IsReading) window.ToggleReading();
        await Idle(window);
        int sourceTarget = before.IndexOf("目标段落", StringComparison.Ordinal);
        var pointer = tab.Rendered!.PointerAt(tab.Reading!.SourceToDisplay(sourceTarget));
        tab.Preview.Selection.Select(pointer, pointer);
        File.WriteAllText(path, "前置插入\n\n" + before);
        window.QueueExternalRefresh(tab);
        await WaitFor(() => tab.File.Text.StartsWith("前置插入", StringComparison.Ordinal),
            "外部前置插入刷新正文");
        int expected = tab.File.Text.IndexOf("目标段落", StringComparison.Ordinal);
        int actual = tab.Reading!.DisplayToSource(tab.Rendered!.OffsetAt(tab.Preview.Selection.Start));
        Check(Math.Abs(actual - expected) <= 1, $"外部前置插入后阅读选区跟随原段落 (expected={expected}, actual={actual})");
    }

    private static async Task SearchFocusDuringRefresh(MainWindow window, string root)
    {
        string path = Path.Combine(root, "search-focus.md");
        File.WriteAllText(path, "# Before\n\nalpha needle omega");
        window.Activate();
        await window.OpenPathsAsync([path]);
        var tab = window.CurrentTab!;
        await Idle(window);
        Check(tab.IsReading && tab.Rendered is not null, "焦点回归使用真实Markdown阅读视图");

        var query = (TextBox)window.FindName("SearchQuery");
        var panel = (FrameworkElement)window.FindName("SearchPanel");
        var count = (TextBlock)window.FindName("SearchCount");
        try
        {
            typeof(MainWindow).GetMethod("ShowSearch", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [false]);
            await Idle(window);
            query.Focus();
            query.Text = "needle";
            await WaitFor(() => count.Text.Contains(" / "), "真实搜索框完成阅读内容查找");
            query.Select(1, 3);
            Check(ReferenceEquals(Keyboard.FocusedElement, query),
                $"磁盘刷新前搜索框确实持有键盘焦点 (active={window.IsActive}, focused={Keyboard.FocusedElement})");

            File.WriteAllText(path, "# After\n\nalpha needle omega");
            window.QueueExternalRefresh(tab);
            await WaitFor(() => tab.Reading?.Headings.FirstOrDefault()?.Title == "After",
                "搜索输入期间外部刷新仍重建阅读视图");
            await WaitFor(() => count.Text.Contains(" / "), "外部刷新后搜索结果重新生成");
            await Idle(window);
            Check(ReferenceEquals(Keyboard.FocusedElement, query) && query.IsKeyboardFocusWithin,
                "阅读模式磁盘刷新不会抢走真实搜索框键盘焦点");
            Check(query.Text == "needle" && query.SelectionStart == 1 && query.SelectionLength == 3,
                "外部刷新保留搜索框输入与选区");
            // Keep the preview loaded while checking the optional parameter's
            // default; initial visibility/layout timing is outside this regression.
            typeof(MainWindow).GetMethod("EnterReading", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [tab, null, Type.Missing, Type.Missing]);
            Check(tab.Preview.IsKeyboardFocusWithin, "EnterReading默认调用仍聚焦阅读视图");
        }
        finally
        {
            query.Text = "";
            panel.Visibility = Visibility.Collapsed;
        }
    }

    private static async Task NoticesAndRetry(MainWindow window, string root)
    {
        string path = Path.Combine(root, "notices.txt");
        File.WriteAllText(path, "base");
        await window.OpenPathsAsync([path]);
        var tab = window.CurrentTab!;
        await Idle(window);
        tab.Editor.Select(tab.Editor.Text.Length, 0);
        tab.Editor.BeginChange();
        try { tab.Editor.SelectedText = " local"; }
        finally { tab.Editor.EndChange(); }
        var before = tab.File.CaptureSnapshot();
        bool couldUndo = tab.Editor.CanUndo;
        Check(couldUndo, "脏标签测试包含真实可撤销编辑");
        File.WriteAllText(path, "external");
        window.QueueExternalRefresh(tab);
        var status = (TextBlock)window.FindName("StatusText");
        await WaitFor(() => status.Text.Contains("外部") && status.Text.Contains("保留"), "脏标签收到非模态外部变更提示");
        Check(tab.File.CaptureSnapshot() == before && tab.Editor.Text == "base local" && tab.Editor.CanUndo == couldUndo,
            $"脏标签的正文基线与撤销栈不变 (snapshot={tab.File.CaptureSnapshot() == before}, text={tab.Editor.Text}, undo={couldUndo}/{tab.Editor.CanUndo})");
        Throws<FileConflictException>(() => window.SaveTabToPath(tab, path), "窗口保存仍执行磁盘冲突检查");
        tab.Editor.Undo();
        Check(tab.File.Text == "base" && !tab.File.IsDirty, "外部冲突提示后仍可撤销本地编辑");
        path = Path.Combine(root, "retry.txt");
        File.WriteAllText(path, "keep");
        await window.OpenPathsAsync([path]);
        tab = window.CurrentTab!;
        before = tab.File.CaptureSnapshot();
        File.Delete(path);
        window.QueueExternalRefresh(tab);
        await WaitFor(() => status.Text.Contains("保留") && status.Text.Contains("重试"), "删除或占用使用非模态可重试提示");
        Check(tab.File.CaptureSnapshot() == before && tab.Editor.Text == "keep", "删除后保留标签正文和基线");
        File.WriteAllText(path, "back");
        window.QueueExternalRefresh(tab);
        await WaitFor(() => tab.File.Text == "back", "失败后的下次切换或激活可重试");
    }

    private static async Task ActivationStorm(MainWindow window, string root)
    {
        string path = Path.Combine(root, "activate.txt");
        File.WriteAllText(path, "before");
        await window.OpenPathsAsync([path]);
        var tab = window.CurrentTab!;
        File.WriteAllText(path, "after");
        var activated = typeof(Window).GetMethod("OnActivated", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) activated.Invoke(window, [EventArgs.Empty]);
        Check(watch.ElapsedMilliseconds < 500 && tab.File.Text == "before",
            "100次激活只排队而不在UI同步读盘");
        await WaitFor(() => tab.File.Text == "after", "窗口Activated实际触发异步刷新且激活风暴不饿死请求");
        File.WriteAllText(path, "next!");
        window.QueueExternalRefresh(tab);
        await WaitFor(() => tab.File.Text == "next!", "节流期间的新激活仍保留尾随检查");

        var other = window.NewDocument("stay current");
        File.WriteAllText(path, "background");
        window.QueueExternalRefresh(tab);
        await Task.Delay(1100);
        Check(ReferenceEquals(window.CurrentTab, other) && tab.File.Text == "next!",
            "后台标签的过期请求不跳转或覆盖当前标签");
        window.SelectTab(tab);
        window.QueueExternalRefresh(tab);
        window.SelectTab(other);
        await Task.Delay(1100);
        Check(tab.File.Text == "next!" && ReferenceEquals(window.CurrentTab, other),
            "排队后立即切走的标签结果被丢弃");
        window.SelectTab(tab);
        await WaitFor(() => tab.File.Text == "background", "再次切回仍可获取磁盘新版本");
    }

    private static async Task Idle(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, name); return; }
        throw new Exception(name);
    }
}
