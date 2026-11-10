using System.Windows.Documents;
using System.Windows.Threading;
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

    /// <summary>
    /// Call from the WPF UI thread after parsing the original ReadingModel on a worker.
    /// Builds detached document state in dispatcher batches. The host publishes only
    /// the successfully returned result and owns request identity/progress/cancellation.
    /// </summary>
    public static async Task<RenderedReading> MarkdownAsync(ReadingModel model, string original,
        string? sourcePath, double fontSize, double readingWidth, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(original);
        token.ThrowIfCancellationRequested();
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA ||
            SynchronizationContext.Current is not DispatcherSynchronizationContext)
            throw new InvalidOperationException("MarkdownAsync must be called on the WPF UI dispatcher.");
        var document = await MarkdownPreview.BuildDocumentAsync(model, original, sourcePath, fontSize, readingWidth, token);
        token.ThrowIfCancellationRequested();
        var map = await RichDocumentMap.CreateAsync(document, model, token);
        token.ThrowIfCancellationRequested();
        return new RenderedReading(document, map);
    }

    private RenderedReading(FlowDocument document, RichDocumentMap map)
    {
        Document = document;
        richMap = map;
    }
}
