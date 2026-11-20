using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Qingyue.Core;

internal static partial class Program
{
    // Shares the same entry point with the isolated WPF navigation regression host.
    private static async Task RunMarkdownNavigationChecks(Window window)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = window.GetType();
        object? Invoke(string name, params object?[] args) =>
            (type.GetMethod(name, instance) ?? throw new Exception("缺少导航方法：" + name))
                .Invoke(window, args);
        bool Locate(string name) => (bool)Invoke(name)!;

        async Task<(object Tab, TextBox Editor, RichTextBox Preview)> Open(string source)
        {
            var tab = type.GetMethod("NewDocument")!.Invoke(window, [source])!;
            tab.GetType().GetProperty("AutomaticKind")!.SetValue(tab, false);
            tab.GetType().GetProperty("Kind")!.SetValue(tab, TextKind.Markdown);
            type.GetMethod("SelectTab")!.Invoke(window, [tab]);
            Invoke("ToggleMarkdownPreview");
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var editor = (TextBox)tab.GetType().GetProperty("Editor")!.GetValue(tab)!;
            var preview = (RichTextBox)type.GetField("markdownPreview", instance)!.GetValue(window)!;
            return (tab, editor, preview);
        }

        void ExpectPreviewTarget(TextBox editor, RichTextBox preview, Table table)
        {
            editor.Select(editor.Text.IndexOf("| B |", StringComparison.Ordinal), 0);
            int selectionStart = editor.SelectionStart, selectionLength = editor.SelectionLength;
            var previewStart = preview.Selection.Start;
            var previewEnd = preview.Selection.End;
            DependencyObject? requested = null;
            RequestBringIntoViewEventHandler handler = (_, e) =>
            {
                requested = e.TargetObject;
                e.Handled = true;
            };
            preview.AddHandler(FrameworkElement.RequestBringIntoViewEvent, handler, handledEventsToo: true);
            try
            {
                Equal(Locate("LocateMarkdownPreview"), true);
                if (!ReferenceEquals(requested, table))
                    throw new Exception("源码表格应定位 Table，实际：" + requested?.GetType().Name);
                Equal(editor.SelectionStart, selectionStart);
                Equal(editor.SelectionLength, selectionLength);
                Equal(preview.Selection.Start.CompareTo(previewStart), 0);
                Equal(preview.Selection.End.CompareTo(previewEnd), 0);
            }
            finally { preview.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, handler); }
        }

        void ExpectEditorTarget(TextBox editor, RichTextBox preview, Table table, int sourceStart)
        {
            editor.Select(editor.Text.Length, 0);
            var paragraph = table.RowGroups[0].Rows[0].Cells[0].Blocks.OfType<Paragraph>().First();
            var pointer = paragraph.Inlines.FirstInline!.ContentStart;
            preview.Selection.Select(pointer, pointer);
            Equal(Locate("LocateMarkdownEditor"), true);
            Equal(editor.SelectionStart, sourceStart);
            Equal(editor.SelectionLength, 0);
            Equal(preview.Selection.Start.CompareTo(pointer), 0);
        }

        var adjacent = await Open("# 头\n\n| A |\n|---|\n| B |\n\n尾");
        var adjacentTable = adjacent.Preview.Document.Blocks.OfType<Table>().Single();
        Test("WPF源码表格定位到表格容器且保留双方选区", () =>
            ExpectPreviewTarget(adjacent.Editor, adjacent.Preview, adjacentTable));
        Test("WPF预览单元格沿祖先定位表格源码而非后续正文", () =>
            ExpectEditorTarget(adjacent.Editor, adjacent.Preview, adjacentTable, 5));
        Test("WPF表格导航保留重入保护", () =>
        {
            var field = type.GetField("markdownLocating", instance)!;
            bool before = (bool)field.GetValue(window)!;
            field.SetValue(window, true);
            try
            {
                Equal(Locate("LocateMarkdownPreview"), false);
                Equal(Locate("LocateMarkdownEditor"), false);
            }
            finally { field.SetValue(window, before); }
        });
        Test("WPF过时表格预览拒绝双向定位且不改选区", () =>
        {
            adjacent.Editor.AppendText("\n变更");
            int selectionStart = adjacent.Editor.SelectionStart;
            int selectionLength = adjacent.Editor.SelectionLength;
            var previewStart = adjacent.Preview.Selection.Start;
            Equal(Locate("LocateMarkdownPreview"), false);
            Equal(Locate("LocateMarkdownEditor"), false);
            Equal(adjacent.Editor.SelectionStart, selectionStart);
            Equal(adjacent.Editor.SelectionLength, selectionLength);
            Equal(adjacent.Preview.Selection.Start.CompareTo(previewStart), 0);
        });

        // A new preview document must invalidate the navigation cache, including a table-only document.
        var only = await Open("| A |\n|---|\n| B |");
        var onlyTable = only.Preview.Document.Blocks.OfType<Table>().Single();
        Test("WPF仅表格文档也能从源码定位预览", () =>
            ExpectPreviewTarget(only.Editor, only.Preview, onlyTable));
        Test("WPF仅表格文档也能从单元格定位源码", () =>
            ExpectEditorTarget(only.Editor, only.Preview, onlyTable, 0));
        Test("WPF预览滚动定位不改变源码选区", () =>
        {
            only.Editor.Select(2, 3);
            Invoke("LocateMarkdownEditorViewport");
            Equal(only.Editor.SelectionStart, 2);
            Equal(only.Editor.SelectionLength, 3);
        });
    }
}
