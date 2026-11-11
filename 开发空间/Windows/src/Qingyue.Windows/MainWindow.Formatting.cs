using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private readonly ConditionalWeakTable<EditorTab, JsonSyntaxLayer> syntaxLayers = new();
    private EditorTab? structureTab;
    private JsonStructureView? structureView;
    private string? structureText;

    private void ReleaseClosedPresentations()
    {
        ReleaseClosedMarkdownPreview();
        if (structureTab is null || tabs.Contains(structureTab)) return;
        structureTab = null;
        structureView = null;
        structureText = null;
    }

    private void ExitStructureForEditing(EditorTab tab)
    {
        if (structureTab != tab) return;
        structureTab = null; structureView = null; structureText = null;
        RefreshCurrent();
    }

    private void AttachSyntax(EditorTab tab)
    {
        var layer = new JsonSyntaxLayer(tab);
        Grid.SetColumn(layer, 1);
        Panel.SetZIndex(layer, 1);
        tab.EditorSurface.Children.Add(layer);
        syntaxLayers.Add(tab, layer);
        layer.Refresh();
    }

    private void RefreshSyntax(EditorTab tab)
    {
        if (syntaxLayers.TryGetValue(tab, out var layer)) layer.Refresh();
    }

    private void RefreshPresentation(EditorTab tab)
    {
        RefreshSyntax(tab);
        bool reading = tab.IsReading && tab.Rendered is not null;
        MainMenu.Visibility = reading || focusMode ? Visibility.Collapsed : Visibility.Visible;
        BrandHeader.Visibility = reading ? Visibility.Collapsed : Visibility.Visible;
        ReadingToolbar.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
        EditOnlyTools.Visibility = reading ? Visibility.Collapsed : Visibility.Visible;
        EditorStatusControls.Visibility = reading ? Visibility.Collapsed : Visibility.Visible;
        ReadingCopyButton.Visibility = reading && tab.Kind == TextKind.Markdown
            ? Visibility.Visible : Visibility.Collapsed;
        if (structureTab == tab && tab.Kind is not TextKind.Json and not TextKind.Jsonc)
        {
            structureTab = null; structureView = null; structureText = null;
            DocumentHost.Content = tab.IsReading && tab.Rendered is not null ? tab.Preview : tab.EditorSurface;
        }
        bool structured = structureTab == tab && !tab.IsReading;
        if (structured && structureText != tab.File.Text)
        {
            structureTab = null; structureView = null; structureText = null;
            structured = false;
            DocumentHost.Content = tab.EditorSurface;
        }
        if (structured && structureView is not null) DocumentHost.Content = structureView;
        FormatButton.Content = tab.Kind == TextKind.Plain ? "整理 TXT" : LanguageCatalog.FormatActionLabel(tab.Kind);
        FormatButton.ToolTip = tab.Kind switch
        {
            TextKind.Plain => "整理行尾空格、多余空行和缩进；保留分段 · Ctrl+Alt+F",
            TextKind.Json => "格式化 JSON，保留字段顺序、重复键和数字精度 · Ctrl+Alt+F",
            TextKind.Markdown => "整理 Markdown 标题、列表与空行；保留代码和手动换行 · Ctrl+Alt+F",
            _ => LanguageCatalog.Get(tab.Kind).FormattingDescription + " · Ctrl+Alt+F"
        };
        StructureButton.IsEnabled = tab.Kind is TextKind.Json or TextKind.Jsonc;
        StructureButton.Visibility = !reading && StructureButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        StructureButton.Content = structured ? "返回编辑" : "JSON 结构";
        FormatButton.Visibility = reading ? Visibility.Collapsed : Visibility.Visible;
        MarkdownButton.Visibility = !reading && tab.Kind == TextKind.Markdown ? Visibility.Visible : Visibility.Collapsed;
        bool markdown = RefreshMarkdownPresentation(tab);
        MarkdownButton.Content = markdown ? "关闭分屏" : "分屏编辑";
        CopyReadingButton.Visibility = tab.Kind == TextKind.Markdown &&
            (tab.IsReading && tab.Rendered is not null || markdown) ? Visibility.Visible : Visibility.Collapsed;
        CopyOptionsMenu.Visibility = tab.Kind == TextKind.Markdown ? Visibility.Visible : Visibility.Collapsed;
        PresentationHint.Text = structured ? "只读结构 · 点击箭头折叠层级，返回编辑修改原文"
            : markdown ? "左侧编辑 · 右侧预览 · 橙色未保存 / 绿色已保存"
            : tab.Kind == TextKind.Markdown ? tab.IsReading
                ? "Markdown 预览 · Ctrl+C 复制选中内容 · Ctrl+F 查找"
                : "Markdown 编辑 · 点击“返回预览”阅读 · 可选分屏编辑"
            : tab.Kind is TextKind.Json or TextKind.Jsonc ? "JSON · 字段 / 字符串 / 数值分色"
            : "双击选片段 · 三击选整行";
    }

    private void ToggleStructure()
    {
        if (CurrentTab is not { } tab) return;
        if (structureTab == tab)
        {
            structureTab = null; structureView = null; structureText = null;
            RefreshCurrent(); tab.Editor.Focus(); return;
        }
        if (tab.Kind is not TextKind.Json and not TextKind.Jsonc) return;
        if (tab.File.Text.Length > 2_000_000)
        {
            Notify("当前文档较大，请使用编辑视图查看完整原文。结构视图支持 200 万字符以内的 JSON。");
            return;
        }
        var view = new JsonStructureView(tab.File.Text, tab.Kind);
        if (tab.IsReading) ToggleReading();
        structureTab = tab; structureView = view; structureText = tab.File.Text;
        RefreshCurrent(); view.Focus();
    }

    private void FormatCurrentDocument(EditorTab tab)
    {
        string original = tab.File.Text;
        bool formatSupported = LanguageCatalog.CanFormat(tab.Kind);
        string formatted = formatSupported ? TextServices.Format(original, tab.Kind)
            : TextServices.NormalizeWhitespace(original);
        if (formatted == original)
        {
            StatusText.Text = "当前格式已整齐，无需修改。";
            return;
        }
        if (!formatSupported && Modal(() => Dialogs.PreviewTextChanges(this, original, formatted)) != true) return;
        if (tab.IsReading) ToggleReading();
        structureTab = null; structureView = null; structureText = null;
        int caret = tab.Editor.CaretIndex;
        tab.Editor.BeginChange();
        try
        {
            tab.Editor.SelectAll();
            tab.Editor.SelectedText = formatted;
            tab.Editor.Select(Math.Min(caret, tab.Editor.Text.Length), 0);
        }
        finally { tab.Editor.EndChange(); }
        RefreshCurrent(); tab.Editor.Focus();
        StatusText.Text = "已格式化 · Ctrl+Z 可一次撤销 · Ctrl+S 保存原文件";
    }
}

public sealed class DocumentLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        TextKind kind => KindLabel(kind),
        TextCodec codec => codec switch
        {
            TextCodec.Utf8 => "UTF-8", TextCodec.Utf8Bom => "UTF-8（BOM）",
            TextCodec.Utf16LE => "UTF-16 LE", TextCodec.Utf16BE => "UTF-16 BE", _ => "GB18030（中文）"
        },
        LineEnding ending => ending switch
        {
            LineEnding.Lf => "Linux / macOS（LF）", LineEnding.CrLf => "Windows（CRLF）",
            LineEnding.Cr => "旧 Mac（CR）", _ => "混合换行（需选择）"
        },
        _ => value?.ToString() ?? ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    private static string KindLabel(TextKind kind) => LanguageCatalog.DisplayName(kind);
}
