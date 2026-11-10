using System.Windows.Documents;
using Qingyue.Core;

namespace Qingyue.Windows;

public sealed partial class RenderedReading
{
    private readonly RichDocumentMap? richMap;

    private RenderedReading(ReadingModel model, FlowDocument document)
    {
        Document = document;
        richMap = new RichDocumentMap(document, model);
    }

    internal static RenderedReading Markdown(ReadingModel model, string text, string? sourcePath,
        double fontSize, double readingWidth)
    {
        var preview = new MarkdownPreview(text, fontSize, readingWidth, sourcePath, includeDiagramSource: true);
        var document = preview.Document;
        preview.Document = new FlowDocument();
        return new RenderedReading(model, document);
    }
}
