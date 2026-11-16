using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;

namespace Qingyue.Windows;

public sealed record ReadingAppearanceSelection(
    string FontFamily,
    double LineHeight,
    double ParagraphSpacing,
    ReaderThemeKind Theme)
{
    public void ApplyTo(ReaderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.FontFamily = ReadingFontCatalog.Normalize(FontFamily);
        settings.LineHeight = double.IsFinite(LineHeight)
            ? Math.Clamp(LineHeight, 1.1, 3.2)
            : 1.75;
        settings.ParagraphSpacing = double.IsFinite(ParagraphSpacing)
            ? Math.Clamp(ParagraphSpacing, 0, 48)
            : 13;
        settings.Theme = ReaderTheme.NormalizeName(Theme.ToString(), Theme == ReaderThemeKind.Dark);
        settings.Dark = Theme == ReaderThemeKind.Dark;
    }
}

public static class ReadingAppearanceDialog
{
    private sealed record NumberChoice(double Value, string Label);
    private sealed record ThemeChoice(ReaderThemeKind Theme, string Label);

    private static readonly NumberChoice[] LineHeightChoices =
    [
        new(1.4, "1.4 倍（紧凑）"),
        new(1.6, "1.6 倍"),
        new(1.75, "1.75 倍（推荐）"),
        new(2.0, "2.0 倍"),
        new(2.25, "2.25 倍"),
        new(2.5, "2.5 倍（宽松）")
    ];

    private static readonly NumberChoice[] ParagraphSpacingChoices =
    [
        new(0, "无段距"),
        new(8, "8 像素"),
        new(13, "13 像素（推荐）"),
        new(18, "18 像素"),
        new(24, "24 像素"),
        new(32, "32 像素")
    ];

    private static readonly ThemeChoice[] ThemeChoices =
    [
        new(ReaderThemeKind.Dark, "深色"),
        new(ReaderThemeKind.Light, "浅色"),
        new(ReaderThemeKind.WarmPaper, "暖纸")
    ];

    public static ReadingAppearanceSelection? Show(Window owner, ReaderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(settings);

        var dialog = Dialogs.Shell(owner, "阅读外观", 520);
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(Heading("阅读外观", 22));
        panel.Children.Add(new TextBlock
        {
            Text = "只调整阅读显示，不会修改原文内容。字号仍可用阅读工具栏上的 A− / A+ 调整。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 20)
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var fonts = new ComboBox
        {
            ItemsSource = FontChoices(settings),
            MinWidth = 260,
            Height = 30
        };
        AutomationProperties.SetName(fonts, "阅读字体");
        fonts.SelectedItem = ReadingFontCatalog.Normalize(settings.FontFamily);
        AddRow(grid, 0, "字体", fonts);

        var lineHeight = new ComboBox
        {
            ItemsSource = LineHeightChoices,
            DisplayMemberPath = nameof(NumberChoice.Label),
            SelectedValuePath = nameof(NumberChoice.Value),
            MinWidth = 260,
            Height = 30
        };
        AutomationProperties.SetName(lineHeight, "阅读行高");
        lineHeight.SelectedItem = Closest(LineHeightChoices, settings.LineHeight);
        AddRow(grid, 1, "行高", lineHeight);

        var paragraphSpacing = new ComboBox
        {
            ItemsSource = ParagraphSpacingChoices,
            DisplayMemberPath = nameof(NumberChoice.Label),
            SelectedValuePath = nameof(NumberChoice.Value),
            MinWidth = 260,
            Height = 30
        };
        AutomationProperties.SetName(paragraphSpacing, "阅读段距");
        paragraphSpacing.SelectedItem = Closest(ParagraphSpacingChoices, settings.ParagraphSpacing);
        AddRow(grid, 2, "段距", paragraphSpacing);

        var theme = new ComboBox
        {
            ItemsSource = ThemeChoices,
            DisplayMemberPath = nameof(ThemeChoice.Label),
            SelectedValuePath = nameof(ThemeChoice.Theme),
            MinWidth = 260,
            Height = 30
        };
        AutomationProperties.SetName(theme, "阅读主题");
        theme.SelectedItem = ThemeChoices.FirstOrDefault(item =>
            item.Theme == ReaderTheme.Parse(settings.Theme, settings.Dark)) ?? ThemeChoices[0];
        AddRow(grid, 3, "主题", theme);
        panel.Children.Add(grid);

        var preview = new TextBlock
        {
            Text = "示例文字：读完之后留下引文、备注和下一步。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 22, 0, 0),
            Padding = new Thickness(16, 12, 16, 12)
        };
        preview.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        preview.SetResourceReference(TextBlock.BackgroundProperty, "ReadingBrush");
        panel.Children.Add(preview);

        void UpdatePreview()
        {
            if (fonts.SelectedItem is string family)
                preview.FontFamily = new FontFamily(family);
            if (lineHeight.SelectedItem is NumberChoice line)
                preview.LineHeight = (settings.FontSize + 1) * line.Value;
            if (paragraphSpacing.SelectedItem is NumberChoice spacing)
                preview.Margin = new Thickness(0, 22 + spacing.Value / 2, 0, 0);
        }

        fonts.SelectionChanged += (_, _) => UpdatePreview();
        lineHeight.SelectionChanged += (_, _) => UpdatePreview();
        paragraphSpacing.SelectionChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        ReadingAppearanceSelection? result = null;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        buttons.Children.Add(new Button
        {
            Content = "取消",
            IsCancel = true,
            Margin = new Thickness(0, 0, 10, 0),
            MinWidth = 92
        });
        var apply = new Button
        {
            Content = "应用",
            IsDefault = true,
            MinWidth = 92
        };
        apply.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
        apply.Foreground = Brushes.White;
        apply.Click += (_, _) =>
        {
            result = new ReadingAppearanceSelection(
                fonts.SelectedItem as string ?? ReadingFontCatalog.Fallback,
                lineHeight.SelectedItem is NumberChoice line ? line.Value : settings.LineHeight,
                paragraphSpacing.SelectedItem is NumberChoice spacing ? spacing.Value : settings.ParagraphSpacing,
                theme.SelectedItem is ThemeChoice selectedTheme ? selectedTheme.Theme : ReaderTheme.Parse(settings.Theme, settings.Dark));
            dialog.DialogResult = true;
        };
        buttons.Children.Add(apply);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => fonts.Focus();
        dialog.ShowDialog();
        return result;
    }

    private static IReadOnlyList<string> FontChoices(ReaderSettings settings)
    {
        var fonts = ReadingFontCatalog.Available.ToList();
        string current = ReadingFontCatalog.Normalize(settings.FontFamily);
        if (!fonts.Contains(current, StringComparer.OrdinalIgnoreCase))
            fonts.Insert(0, current);
        return fonts.Count == 0 ? [ReadingFontCatalog.Fallback] : fonts;
    }

    private static NumberChoice Closest(IEnumerable<NumberChoice> choices, double value) =>
        choices.OrderBy(item => Math.Abs(item.Value - value)).First();

    private static void AddRow(Grid grid, int row, string label, Control control)
    {
        var caption = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 12)
        };
        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, 0);
        grid.Children.Add(caption);

        control.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private static TextBlock Heading(string text, double size)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return label;
    }
}
