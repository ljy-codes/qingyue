using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Qingyue.Core;

namespace Qingyue.Windows;

public sealed record ReadingAppearanceSelection(
    string FontFamily,
    double LineHeight,
    double ParagraphSpacing,
    ReaderThemeKind Theme,
    AppIconThemeKind IconTheme,
    double? LineSpacing = null,
    string? ThemeId = null,
    bool? Dark = null,
    bool? FollowSystem = null,
    bool? LineNumbers = null,
    bool? Wrap = null,
    double? FontSize = null,
    double? ReadingWidth = null)
{
    public static ReadingAppearanceSelection FromSettings(ReaderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(
            settings.FontFamily,
            settings.LineHeight,
            settings.ParagraphSpacing,
            settings.ThemeKind,
            AppIconTheme.Parse(settings.IconTheme),
            settings.LineSpacing,
            settings.Theme,
            settings.EffectiveDark,
            settings.FollowSystem,
            settings.LineNumbers,
            settings.Wrap,
            settings.FontSize,
            settings.ReadingWidth);
    }

    public void ApplyTo(ReaderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.FontFamily = ReadingFontCatalog.Normalize(FontFamily);
        settings.FontSize = double.IsFinite(FontSize ?? settings.FontSize)
            ? Math.Clamp(FontSize ?? settings.FontSize, 10, 30)
            : 14;
        double spacing = LineSpacing ?? (double.IsFinite(LineHeight) && LineHeight > 0
            ? LineHeight > 5
                ? LineHeight
                : Math.Max(0, (LineHeight - ReadingPresentation.NaturalLineHeightFactor) * settings.FontSize)
            : 6);
        settings.LineSpacing = double.IsFinite(spacing) ? Math.Clamp(spacing, 0, 48) : 6;
        settings.LineHeight = ReadingPresentation.NaturalLineHeightFactor
            + settings.LineSpacing / Math.Max(10, settings.FontSize);
        settings.ParagraphSpacing = double.IsFinite(ParagraphSpacing)
            ? Math.Clamp(ParagraphSpacing, 0, 48)
            : 13;

        ReaderThemeKind theme = string.IsNullOrWhiteSpace(ThemeId)
            ? Theme
            : ReaderTheme.Parse(ThemeId, Dark ?? settings.Dark);
        settings.Theme = ReaderTheme.NormalizeName(theme.ToString(), Dark ?? settings.Dark);
        settings.FollowSystem = FollowSystem ?? settings.FollowSystem;
        settings.Dark = settings.FollowSystem
            ? ReaderTheme.SystemIsDark()
            : Dark ?? (theme is ReaderThemeKind.Dark or ReaderThemeKind.InkBlue);
        if (LineNumbers is { } lineNumbers) settings.LineNumbers = lineNumbers;
        if (Wrap is { } wrap) settings.Wrap = wrap;
        settings.ReadingWidth = ReaderSettings.NormalizeReadingWidth(
            ReadingWidth ?? settings.ReadingWidth);
        settings.IconTheme = AppIconTheme.NormalizeName(IconTheme.ToString());
    }
}

/// <summary>
/// Seven-theme, live-applying parity window for the macOS preferences panel.
/// There is deliberately no cancel/apply transaction: every control change is
/// applied immediately, matching PreferencesWindowController.settingsChanged.
/// </summary>
public sealed record ReadingAppearanceWindow(Window Window, Action<ReaderSettings> SyncFromSettings);

public static class ReadingAppearanceDialog
{
    private sealed record NumberChoice(double Value, string Label);
    private sealed record ThemeChoice(ReaderThemeKind Theme, string Label);
    private sealed record IconThemeChoice(AppIconThemeKind Theme, string Label);
    private sealed record TextChoice(string Value, string Label);
    private sealed record Preview(
        Border Container,
        StackPanel ReadingContent,
        TextBlock ReadingTitle,
        TextBlock CodeText,
        ScrollViewer ReadingScroll,
        ScrollViewer CodeScroll);

    private static readonly NumberChoice[] FontSizeChoices =
        Enumerable.Range(10, 21).Select(value => new NumberChoice(value, value.ToString())).ToArray();

    private static readonly NumberChoice[] LineSpacingChoices =
    [
        new(2, "紧凑"),
        new(6, "舒适"),
        new(11, "宽松")
    ];

    private static readonly NumberChoice[] ParagraphSpacingChoices =
    [
        new(0, "无段距"),
        new(6, "紧凑"),
        new(13, "标准"),
        new(24, "宽松")
    ];

    private static readonly NumberChoice[] ReadingWidthChoices =
    [
        new(520, "紧凑"),
        new(680, "标准"),
        new(840, "宽"),
        new(1_000_000, "全宽")
    ];

    private static readonly ThemeChoice[] ThemeChoices =
        ReaderTheme.All.Select(theme => new ThemeChoice(theme, ReaderTheme.Label(theme))).ToArray();

    private static readonly IconThemeChoice[] IconThemeChoices =
        AppIconTheme.Options.Select(item => new IconThemeChoice(item.Kind, item.Label)).ToArray();

    public static ReadingAppearanceWindow Create(
        Window owner,
        ReaderSettings settings,
        Action<ReadingAppearanceSelection> onChanged)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(onChanged);

        var dialog = Dialogs.Shell(owner, "阅读外观与编辑设置", 900);
        dialog.Width = 900;
        dialog.Height = 740;
        dialog.MinWidth = 820;
        dialog.MinHeight = 620;
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.ResizeMode = ResizeMode.CanResize;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.MaxHeight = double.PositiveInfinity;

        ReaderThemeKind selectedTheme = settings.ThemeKind;
        bool dark = settings.EffectiveDark;
        bool followSystem = settings.FollowSystem;
        bool lineNumbers = settings.LineNumbers;
        bool wrap = settings.Wrap;
        double fontSize = Math.Clamp(settings.FontSize, 10, 30);
        string fontFamily = ReadingFontCatalog.Normalize(settings.FontFamily);
        double lineSpacing = settings.LineSpacing;
        double paragraphSpacing = settings.ParagraphSpacing;
        double readingWidth = settings.ReadingWidth;
        AppIconThemeKind iconTheme = AppIconTheme.Parse(settings.IconTheme);
        bool changing = false;

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var root = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
        scroll.Content = root;

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(Heading("外观主题", 19));
        var headingDetail = new TextBlock
        {
            Text = "设置会立即应用到主窗口；阅读与编辑共用 token，但分别预览。",
            FontSize = 11.5,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        headingDetail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        heading.Children.Add(headingDetail);
        root.Children.Add(heading);

        var themeGrid = new UniformGrid
        {
            Columns = 4,
            Name = "ThemeCards",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(themeGrid, "外观主题");
        var themeCards = new List<(
            ReaderThemeKind Theme,
            Button Button,
            TextBlock Label,
            TextBlock Detail,
            Border[] Swatches)>();
        foreach (var choice in ThemeChoices)
        {
            var button = CreateThemeCard(choice.Theme, out var cardLabel, out var cardDetail, out var swatches);
            button.Name = "ThemeCard" + choice.Theme;
            themeCards.Add((choice.Theme, button, cardLabel, cardDetail, swatches));
            themeGrid.Children.Add(button);
        }
        root.Children.Add(themeGrid);

        var previewGrid = new Grid { Margin = new Thickness(0, 16, 0, 18) };
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition());
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        previewGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var readingPreview = CreateReadingPreview();
        var codePreview = CreateCodePreview();
        Grid.SetColumn(readingPreview.Container, 0);
        Grid.SetColumn(codePreview.Container, 2);
        previewGrid.Children.Add(readingPreview.Container);
        previewGrid.Children.Add(codePreview.Container);
        root.Children.Add(previewGrid);

        var checks = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var lineNumbersCheck = Check("显示行号", lineNumbers, "LineNumbersCheck");
        var wrapCheck = Check("自动换行", wrap, "WrapCheck");
        var followSystemCheck = Check("外观跟随系统", followSystem, "FollowSystemCheck");
        var darkCheck = Check("深色外观", dark, "DarkAppearanceCheck");
        foreach (var check in new[] { lineNumbersCheck, wrapCheck, followSystemCheck, darkCheck })
        {
            check.Margin = new Thickness(0, 0, 22, 8);
            checks.Children.Add(check);
        }
        root.Children.Add(checks);

        var themePicker = Combo(ThemeChoices, "Label", "外观主题", "ThemePicker");
        var iconPicker = Combo(IconThemeChoices, "Label", "应用图标", "IconThemePicker");
        var fontPicker = Combo(
            FontChoices(),
            "Label",
            "阅读字体",
            "ReadingFontPicker");
        var fontSizePicker = Combo(FontSizeChoices, "Label", "字号", "FontSizePicker");
        var lineSpacingPicker = Combo(LineSpacingChoices, "Label", "阅读行距", "LineSpacingPicker");
        var paragraphPicker = Combo(ParagraphSpacingChoices, "Label", "段间距", "ParagraphSpacingPicker");
        var widthPicker = Combo(ReadingWidthChoices, "Label", "阅读版心", "ReadingWidthPicker");

        foreach (var card in themeCards)
        {
            card.Button.Click += (_, _) =>
            {
                if (changing) return;
                selectedTheme = card.Theme;
                Refresh();
                Apply();
            };
        }

        themePicker.SelectedItem = ThemeChoices.First(item => item.Theme == selectedTheme);
        iconPicker.SelectedItem = IconThemeChoices.FirstOrDefault(item => item.Theme == iconTheme)
            ?? IconThemeChoices[0];
        fontPicker.SelectedItem = FontChoice(fontFamily);
        fontSizePicker.SelectedItem = Closest(FontSizeChoices, fontSize);
        lineSpacingPicker.SelectedItem = SpacingChoice(lineSpacing);
        paragraphPicker.SelectedItem = ParagraphChoice(paragraphSpacing);
        widthPicker.SelectedItem = WidthChoice(readingWidth);

        root.Children.Add(SettingsRow("外观主题", themePicker, "应用图标", iconPicker));
        root.Children.Add(SettingsRow("字号", fontSizePicker, "阅读字体", fontPicker));
        root.Children.Add(SettingsRow("行距", lineSpacingPicker, "段距", paragraphPicker));
        root.Children.Add(SettingsRow("阅读版心", widthPicker));

        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var restore = new Button
        {
            Content = "恢复默认",
            MinWidth = 92,
            Name = "RestoreDefaultsButton"
        };
        var done = new Button
        {
            Content = "完成",
            IsDefault = true,
            MinWidth = 92,
            Margin = new Thickness(10, 0, 0, 0),
            Name = "DoneButton"
        };
        done.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
        done.SetResourceReference(Control.ForegroundProperty, "AccentForegroundBrush");
        Grid.SetColumn(restore, 1);
        Grid.SetColumn(done, 2);
        footer.Children.Add(restore);
        footer.Children.Add(done);
        root.Children.Add(footer);

        void Apply()
        {
            var selection = new ReadingAppearanceSelection(
                fontFamily,
                ReadingPresentation.NaturalLineHeightFactor + lineSpacing / Math.Max(10, fontSize),
                paragraphSpacing,
                selectedTheme,
                iconTheme,
                lineSpacing,
                ReaderTheme.NormalizeName(selectedTheme.ToString(), dark),
                dark,
                followSystem,
                lineNumbers,
                wrap,
                fontSize,
                readingWidth);
            onChanged(selection);
        }

        void Refresh()
        {
            changing = true;
            try
            {
                var palette = ReaderTheme.GetPalette(selectedTheme, dark);
                foreach (var card in themeCards)
                {
                    bool selected = card.Theme == selectedTheme;
                    card.Button.Background = Brush(selected ? palette.Main : palette.Panel);
                    card.Button.BorderBrush = Brush(selected ? palette.Accent : palette.Border);
                    card.Button.BorderThickness = new Thickness(selected ? 2 : 1);
                    card.Label.Foreground = Brush(palette.Text);
                    card.Detail.Foreground = Brush(palette.Muted);
                    var swatches = ReaderTheme.Swatches(card.Theme);
                    for (int index = 0; index < card.Swatches.Length && index < swatches.Count; index++)
                        card.Swatches[index].Background = Brush(swatches[index]);
                }

                themePicker.SelectedItem = ThemeChoices.First(item => item.Theme == selectedTheme);
                iconPicker.SelectedItem = IconThemeChoices.FirstOrDefault(item => item.Theme == iconTheme)
                    ?? IconThemeChoices[0];
                fontPicker.SelectedItem = FontChoice(fontFamily);
                fontSizePicker.SelectedItem = Closest(FontSizeChoices, fontSize);
                lineSpacingPicker.SelectedItem = SpacingChoice(lineSpacing);
                paragraphPicker.SelectedItem = ParagraphChoice(paragraphSpacing);
                widthPicker.SelectedItem = WidthChoice(readingWidth);
                lineNumbersCheck.IsChecked = lineNumbers;
                wrapCheck.IsChecked = wrap;
                followSystemCheck.IsChecked = followSystem;
                darkCheck.IsChecked = dark;
                darkCheck.IsEnabled = !followSystem;
                UpdatePreviews(
                    readingPreview,
                    codePreview,
                    palette,
                    fontFamily,
                    fontSize,
                    lineSpacing,
                    paragraphSpacing,
                    selectedTheme);
            }
            finally
            {
                changing = false;
            }
        }

        void ReadControls()
        {
            if (changing) return;
            lineNumbers = lineNumbersCheck.IsChecked == true;
            wrap = wrapCheck.IsChecked == true;
            followSystem = followSystemCheck.IsChecked == true;
            dark = followSystem ? ReaderTheme.SystemIsDark() : darkCheck.IsChecked == true;
            selectedTheme = (themePicker.SelectedItem as ThemeChoice)?.Theme ?? selectedTheme;
            iconTheme = (iconPicker.SelectedItem as IconThemeChoice)?.Theme ?? iconTheme;
            fontFamily = (fontPicker.SelectedItem as TextChoice)?.Value ?? fontFamily;
            fontSize = (fontSizePicker.SelectedItem as NumberChoice)?.Value ?? fontSize;
            lineSpacing = (lineSpacingPicker.SelectedItem as NumberChoice)?.Value ?? lineSpacing;
            paragraphSpacing = (paragraphPicker.SelectedItem as NumberChoice)?.Value ?? paragraphSpacing;
            readingWidth = (widthPicker.SelectedItem as NumberChoice)?.Value ?? readingWidth;
            Refresh();
            Apply();
        }

        void SyncFromSettings(ReaderSettings latest)
        {
            selectedTheme = latest.ThemeKind;
            dark = latest.EffectiveDark;
            followSystem = latest.FollowSystem;
            lineNumbers = latest.LineNumbers;
            wrap = latest.Wrap;
            fontSize = Math.Clamp(latest.FontSize, 10, 30);
            fontFamily = ReadingFontCatalog.Normalize(latest.FontFamily);
            lineSpacing = latest.LineSpacing;
            paragraphSpacing = latest.ParagraphSpacing;
            readingWidth = latest.ReadingWidth;
            iconTheme = AppIconTheme.Parse(latest.IconTheme);
            Refresh();
        }

        foreach (var control in new[]
                 {
                     themePicker, iconPicker, fontPicker, fontSizePicker,
                     lineSpacingPicker, paragraphPicker, widthPicker
                 })
            control.SelectionChanged += (_, _) => ReadControls();
        foreach (var check in new[] { lineNumbersCheck, wrapCheck, followSystemCheck, darkCheck })
            check.Click += (_, _) => ReadControls();

        restore.Click += (_, _) =>
        {
            selectedTheme = ReaderThemeKind.Classic;
            followSystem = true;
            dark = ReaderTheme.SystemIsDark();
            lineNumbers = true;
            wrap = true;
            fontSize = 14;
            fontFamily = ReadingFontCatalog.Fallback;
            lineSpacing = 6;
            paragraphSpacing = 13;
            readingWidth = 680;
            iconTheme = AppIconThemeKind.Sky;
            Refresh();
            Apply();
        };
        done.Click += (_, _) => dialog.Close();

        dialog.Content = scroll;
        dialog.Loaded += (_, _) =>
        {
            if (followSystem)
            {
                dark = ReaderTheme.SystemIsDark();
                Refresh();
            }
            done.Focus();
        };
        Refresh();
        return new ReadingAppearanceWindow(dialog, SyncFromSettings);
    }

    private static Button CreateThemeCard(
        ReaderThemeKind theme,
        out TextBlock label,
        out TextBlock detail,
        out Border[] swatches)
    {
        var panel = new Grid { Margin = new Thickness(12, 9, 12, 9) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var swatchRow = new UniformGrid { Columns = 5, Height = 18, Margin = new Thickness(0, 0, 0, 7) };
        var localSwatches = new Border[5];
        for (int index = 0; index < localSwatches.Length; index++)
        {
            localSwatches[index] = new Border
            {
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, index == localSwatches.Length - 1 ? 0 : 2, 0)
            };
            swatchRow.Children.Add(localSwatches[index]);
        }
        Grid.SetRow(swatchRow, 0);
        panel.Children.Add(swatchRow);

        label = new TextBlock
        {
            Text = ReaderTheme.Label(theme),
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(label, 2);
        panel.Children.Add(label);

        detail = new TextBlock
        {
            Text = ReaderTheme.Detail(theme),
            FontSize = 10.5,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(detail, 3);
        panel.Children.Add(detail);

        var button = new Button
        {
            Width = 176,
            Height = 78,
            MinHeight = 78,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 10, 10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = panel
        };
        AutomationProperties.SetName(button, ReaderTheme.Label(theme));
        AutomationProperties.SetHelpText(button, ReaderTheme.Detail(theme));
        button.ToolTip = ReaderTheme.Detail(theme);
        swatches = localSwatches;
        return button;
    }

    private static Preview CreateReadingPreview()
    {
        var title = new TextBlock { Text = "阅读模式", FontSize = 11, Margin = new Thickness(12, 10, 12, 6) };
        var reading = new StackPanel
        {
            Margin = new Thickness(8, 4, 8, 4)
        };
        var readingScroll = new ScrollViewer
        {
            Content = reading,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(12, 0, 12, 10)
        };
        AutomationProperties.SetName(readingScroll, "阅读模式预览滚动区域");
        var panel = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);
        panel.Children.Add(readingScroll);
        var border = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            MinHeight = 190,
            Name = "ReadingPreview"
        };
        return new Preview(border, reading, title, null!, readingScroll, null!);
    }

    private static Preview CreateCodePreview()
    {
        var title = new TextBlock { Text = "编辑模式", FontSize = 11, Margin = new Thickness(12, 10, 12, 6) };
        var code = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(8, 4, 8, 4),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 18
        };
        AutomationProperties.SetName(code, "编辑模式预览");
        var codeScroll = new ScrollViewer
        {
            Content = code,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(12, 0, 12, 10)
        };
        AutomationProperties.SetName(codeScroll, "编辑模式预览滚动区域");
        var panel = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);
        panel.Children.Add(codeScroll);
        var border = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            MinHeight = 190,
            Name = "CodePreview"
        };
        return new Preview(border, null!, title, code, null!, codeScroll);
    }

    private static void UpdatePreviews(
        Preview readingPreview,
        Preview codePreview,
        ThemePalette palette,
        string fontFamily,
        double fontSize,
        double lineSpacing,
        double paragraphSpacing,
        ReaderThemeKind theme)
    {
        readingPreview.Container.Background = Brush(palette.Reading);
        readingPreview.Container.BorderBrush = Brush(palette.Border);
        readingPreview.ReadingTitle.Foreground = Brush(palette.Muted);
        BuildReadingPreview(
            readingPreview.ReadingContent,
            palette,
            fontFamily,
            fontSize,
            lineSpacing,
            paragraphSpacing);

        codePreview.Container.Background = Brush(palette.Editor);
        codePreview.Container.BorderBrush = Brush(palette.Border);
        codePreview.CodeText.Foreground = Brush(palette.Text);
        codePreview.CodeText.Background = Brush(palette.Editor);
        codePreview.CodeText.FontFamily = new FontFamily("Consolas");
        codePreview.CodeText.FontSize = 11.5;
        codePreview.CodeText.Inlines.Clear();
        codePreview.CodeText.Inlines.Add(new Run("{\n  ") { Foreground = Brush(palette.Text) });
        codePreview.CodeText.Inlines.Add(new Run("\"theme\"") { Foreground = Brush(palette.Key) });
        codePreview.CodeText.Inlines.Add(new Run(": ") { Foreground = Brush(palette.Text) });
        codePreview.CodeText.Inlines.Add(new Run($"\"{ReaderTheme.Label(theme)}\"") { Foreground = Brush(palette.String) });
        codePreview.CodeText.Inlines.Add(new Run(",\n  ") { Foreground = Brush(palette.Text) });
        codePreview.CodeText.Inlines.Add(new Run("\"accent\"") { Foreground = Brush(palette.Key) });
        codePreview.CodeText.Inlines.Add(new Run(": ") { Foreground = Brush(palette.Text) });
        codePreview.CodeText.Inlines.Add(new Run($"\"{palette.Accent}\"") { Foreground = Brush(palette.String) });
        codePreview.CodeText.Inlines.Add(new Run(",\n  ") { Foreground = Brush(palette.Text) });
        codePreview.CodeText.Inlines.Add(new Run("\"reading\"") { Foreground = Brush(palette.Key) });
        codePreview.CodeText.Inlines.Add(new Run(": ") { Foreground = Brush(palette.Text) });
        codePreview.CodeText.Inlines.Add(new Run("true") { Foreground = Brush(palette.Number) });
        codePreview.CodeText.Inlines.Add(new Run("\n}") { Foreground = Brush(palette.Text) });
    }

    private static void BuildReadingPreview(
        StackPanel content,
        ThemePalette palette,
        string fontFamily,
        double fontSize,
        double lineSpacing,
        double paragraphSpacing)
    {
        double readingSize = Math.Max(13, fontSize);
        var resolvedFont = new FontFamily(ReadingFontCatalog.Resolve(fontFamily));
        content.Children.Clear();

        var heading = new TextBlock
        {
            Text = "在山中喝茶",
            FontFamily = resolvedFont,
            FontSize = Math.Max(16, readingSize + 2),
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(palette.Text),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = Math.Max(16, readingSize + 2)
                * ReadingPresentation.NaturalLineHeightFactor + lineSpacing,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0, 0, 0, Math.Max(0, paragraphSpacing))
        };
        AutomationProperties.SetName(heading, "阅读模式预览标题");
        content.Children.Add(heading);

        var body = new TextBlock
        {
            Text = "清晨的雾还没有散，窗外的竹叶被风轻轻翻动。",
            FontFamily = resolvedFont,
            FontSize = readingSize,
            Foreground = Brush(palette.Text),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = readingSize * ReadingPresentation.NaturalLineHeightFactor + lineSpacing,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0, 0, 0, Math.Max(0, paragraphSpacing))
        };
        AutomationProperties.SetName(body, "阅读模式预览正文");
        content.Children.Add(body);

        var quote = new TextBlock
        {
            Text = "阅读不是逃离，而是把注意力还给当下。",
            FontFamily = resolvedFont,
            FontSize = readingSize,
            Foreground = Brush(palette.Muted),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = readingSize * ReadingPresentation.NaturalLineHeightFactor + lineSpacing,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        AutomationProperties.SetName(quote, "阅读模式预览引文");
        content.Children.Add(new Border
        {
            Child = quote,
            BorderBrush = Brush(palette.Accent),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2)
        });
    }

    private static StackPanel SettingsRow(params object[] items)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10)
        };
        for (int index = 0; index < items.Length; index++)
        {
            if (items[index] is string text)
            {
                var label = new TextBlock
                {
                    Text = text,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(index == 0 ? 0 : 18, 0, 7, 0),
                    MinWidth = index == 0 ? 58 : 0
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                row.Children.Add(label);
            }
            else if (items[index] is ComboBox combo)
            {
                combo.Width = 150;
                row.Children.Add(combo);
            }
        }
        return row;
    }

    private static ComboBox Combo<T>(
        IReadOnlyList<T> choices,
        string displayMember,
        string automationName,
        string name)
    {
        var combo = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = displayMember,
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            Name = name
        };
        AutomationProperties.SetName(combo, automationName);
        return combo;
    }

    private static CheckBox Check(string text, bool value, string name)
    {
        var check = new CheckBox
        {
            Content = text,
            IsChecked = value,
            Name = name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(check, text);
        return check;
    }

    private static TextChoice[] FontChoices() =>
        ReadingFontCatalog.Preferences
            .Select(item => new TextChoice(item.Value, item.Label))
            .ToArray();

    private static TextChoice FontChoice(string fontFamily)
    {
        string normalized = ReadingFontCatalog.Normalize(fontFamily);
        var choices = FontChoices();
        return choices.FirstOrDefault(item =>
                string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase))
            ?? choices[0];
    }

    private static NumberChoice Closest(IEnumerable<NumberChoice> choices, double value) =>
        choices.OrderBy(item => Math.Abs(item.Value - value)).First();

    private static NumberChoice SpacingChoice(double value) =>
        value < 4 ? LineSpacingChoices[0] : value > 8 ? LineSpacingChoices[2] : LineSpacingChoices[1];

    private static NumberChoice ParagraphChoice(double value) =>
        value < 1 ? ParagraphSpacingChoices[0]
        : value < 10 ? ParagraphSpacingChoices[1]
        : value > 18 ? ParagraphSpacingChoices[3]
        : ParagraphSpacingChoices[2];

    private static NumberChoice WidthChoice(double value) =>
        value > 1_000 ? ReadingWidthChoices[3]
        : value < 600 ? ReadingWidthChoices[0]
        : value > 750 ? ReadingWidthChoices[2]
        : ReadingWidthChoices[1];

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

    private static SolidColorBrush Brush(string value) =>
        new((Color)ColorConverter.ConvertFromString(value));
}
