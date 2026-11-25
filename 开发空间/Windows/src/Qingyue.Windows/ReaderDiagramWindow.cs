using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Qingyue.Windows;

/// <summary>Independent vector viewer for an already parsed Mermaid attachment.</summary>
internal sealed class ReaderDiagramWindow : Window
{
    private ReaderDiagramWindow(MermaidBlockPanel source, Window? owner)
    {
        ReaderWindowTheme.Apply(this, owner);
        Title = "图片 / 图表";
        Width = 1100;
        Height = 760;
        MinWidth = 380;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "EditorBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var diagram = MermaidBlock.Create(source.Source, 16, sourceBelow: true);
        var viewport = new ScrollViewer
        {
            Content = diagram,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(20),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(viewport, "可横向滚动的 Mermaid 图表");
        Content = viewport;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
    }

    internal static void Show(MermaidBlockPanel source, Window? owner)
    {
        var window = new ReaderDiagramWindow(source, owner);
        window.Show();
    }
}
