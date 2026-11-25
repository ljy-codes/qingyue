using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Qingyue.Core;

namespace Qingyue.Windows;

public sealed class JsonStructureView : TreeView
{
    internal const int MaximumCharacters = 2_000_000;
    internal const int MaximumNodes = 10_000;
    internal const int MaximumDepth = 128;

    public JsonStructureView(string text, TextKind kind)
    {
        if (text.Length > MaximumCharacters)
            throw new FormatException("JSON 结构视图支持 200 万字符以内，请使用编辑视图查看完整原文。");
        BorderThickness = new Thickness(0);
        Padding = new Thickness(18, 14, 18, 24);
        FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
        FontSize = 14;
        SetResourceReference(BackgroundProperty, "EditorBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(this, "JSON 只读结构，可展开和折叠");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = kind == TextKind.Jsonc,
                CommentHandling = kind == TextKind.Jsonc ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth
            });
        }
        catch (JsonException error)
        {
            throw new FormatException("JSON 结构失败：请先修复语法。 " + error.Message, error);
        }
        using var ownedDocument = document;
        if (CountNodes(document.RootElement) > MaximumNodes)
            throw new FormatException("JSON 结构超过 10000 个值节点，请使用编辑视图查看完整原文。");
        var root = Node("根节点", document.RootElement.Clone());
        Items.Add(root);
        root.IsExpanded = true;
    }

    private static int CountNodes(JsonElement element)
    {
        int count = 1;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                count = checked(count + CountNodes(property.Value));
                if (count > MaximumNodes) return count;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                count = checked(count + CountNodes(child));
                if (count > MaximumNodes) return count;
            }
        }
        return count;
    }

    private static TreeViewItem Node(string key, JsonElement value)
    {
        var label = new TextBlock { Margin = new Thickness(3, 4, 0, 4) };
        var name = new Run(DisplayText(key, 256, quoted: false) + "  ");
        name.SetResourceReference(TextElement.ForegroundProperty, "SyntaxKeyBrush");
        label.Inlines.Add(name);
        bool container = value.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        string display = value.ValueKind switch
        {
            JsonValueKind.Object => $"{{ {value.EnumerateObject().Count()} 个字段 }}",
            JsonValueKind.Array => $"[ {value.GetArrayLength()} 项 ]",
            JsonValueKind.String => DisplayText(value.GetString()!, 2048, quoted: true),
            _ => LimitText(value.GetRawText(), 2048)
        };
        var content = new Run(LimitText(display, 500));
        content.SetResourceReference(TextElement.ForegroundProperty,
            value.ValueKind == JsonValueKind.String ? "SyntaxStringBrush" : container ? "MutedBrush" : "SyntaxValueBrush");
        label.Inlines.Add(content);
        var item = new TreeViewItem { Header = label, ToolTip = container ? "点击箭头展开或折叠，原文保持不变" : display };
        item.SetResourceReference(ForegroundProperty, "TextBrush");
        if (!container) return item;
        item.Items.Add("加载中");
        bool populated = false;
        item.Expanded += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, item) || populated) return;
            populated = true;
            item.Items.Clear();
            int count = 0;
            // Lazy descendants avoid building an entire large JSON tree on the UI thread.
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (count++ >= 500) break;
                    item.Items.Add(Node(property.Name, property.Value));
                }
            }
            else
            {
                foreach (var element in value.EnumerateArray())
                {
                    if (count >= 500) { count++; break; }
                    item.Items.Add(Node($"[{count++}]", element));
                }
            }
            if (count > 500) item.Items.Add(new TreeViewItem { Header = "当前节点仅展示前 500 项；返回编辑可查看全部原文", IsEnabled = false });
        };
        return item;
    }

    // Bound text before creating WPF inlines/tooltips. JSON data and property order stay untouched.
    private static string LimitText(string text, int limit)
    {
        if (text.Length <= limit) return text;
        int end = limit;
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "…";
    }

    private static string DisplayText(string text, int limit, bool quoted)
    {
        var result = new StringBuilder(limit + 1);
        if (quoted) result.Append('"');
        int budget = limit - (quoted ? 1 : 0);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            string part = c switch
            {
                '\\' => "\\\\",
                '"' when quoted => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\u2028' => "\\u2028",
                '\u2029' => "\\u2029",
                _ when char.IsControl(c) => "\\u" + ((int)c).ToString("x4"),
                _ when char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                    => text.Substring(i++, 2),
                _ => c.ToString()
            };
            if (result.Length + part.Length > budget)
            {
                result.Append('…');
                break;
            }
            result.Append(part);
        }
        if (quoted) result.Append('"');
        return result.ToString();
    }
}
