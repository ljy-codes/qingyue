using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Qingyue.Core;

namespace Qingyue.Windows;

public sealed record AnnotationEditorResult(
    string? Note,
    IReadOnlyList<string> Tags,
    AnnotationColor Color);

public static class AnnotationEditorDialog
{
    public static IReadOnlyList<string> ParseTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
    }

    public static AnnotationEditorResult? Show(
        Window owner,
        string title,
        string? note = null,
        IReadOnlyList<string>? tags = null,
        AnnotationColor color = AnnotationColor.Amber)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var dialog = Dialogs.Shell(owner, title, 480);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "可填写备注、标签和高亮颜色。原文不会被修改。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = owner.FindResource("MutedBrush") as Brush,
            Margin = new Thickness(0, 0, 0, 16)
        });

        panel.Children.Add(new TextBlock { Text = "备注", Margin = new Thickness(0, 0, 0, 5) });
        var noteInput = new TextBox
        {
            Text = note ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 86,
            MaxLength = 4000
        };
        panel.Children.Add(noteInput);

        panel.Children.Add(new TextBlock { Text = "标签", Margin = new Thickness(0, 14, 0, 5) });
        var tagsInput = new TextBox
        {
            Text = string.Join(", ", tags ?? []),
            ToolTip = "用半角逗号分隔，最多 10 个",
            MaxLength = 4000
        };
        panel.Children.Add(tagsInput);

        panel.Children.Add(new TextBlock { Text = "颜色", Margin = new Thickness(0, 14, 0, 5) });
        var colorInput = new ComboBox { ItemsSource = ColorOptions, SelectedValuePath = nameof(ColorOption.Value) };
        colorInput.DisplayMemberPath = nameof(ColorOption.Label);
        colorInput.SelectedValue = color;
        panel.Children.Add(colorInput);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "保存", IsDefault = true };
        AnnotationEditorResult? result = null;
        confirm.Click += (_, _) =>
        {
            try
            {
                var parsed = ParseTags(tagsInput.Text);
                if (parsed.Count > 10 || parsed.Any(tag => tag.Length > 32))
                {
                    Dialogs.Message(dialog, "标签最多 10 个，每个最多 32 个字符。", "批注标签");
                    return;
                }
                result = new AnnotationEditorResult(
                    string.IsNullOrWhiteSpace(noteInput.Text) ? null : noteInput.Text.Trim(),
                    parsed,
                    colorInput.SelectedValue is AnnotationColor selected ? selected : AnnotationColor.Amber);
                dialog.DialogResult = true;
            }
            catch (Exception ex) { Dialogs.Message(dialog, ex.Message, "批注未保存"); }
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) =>
        {
            noteInput.Focus();
            noteInput.CaretIndex = noteInput.Text.Length;
        };
        return dialog.ShowDialog() == true ? result : null;
    }

    private static IReadOnlyList<ColorOption> ColorOptions { get; } =
    [
        new(AnnotationColor.Amber, "琥珀"),
        new(AnnotationColor.Blue, "蓝色"),
        new(AnnotationColor.Green, "绿色"),
        new(AnnotationColor.Rose, "玫瑰"),
        new(AnnotationColor.Violet, "紫色")
    ];

    private sealed record ColorOption(AnnotationColor Value, string Label);
}
