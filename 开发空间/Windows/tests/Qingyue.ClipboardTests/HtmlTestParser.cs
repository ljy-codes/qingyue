using System.Xml.Linq;

internal static class HtmlTestParser
{
    // Exported fragments are HTML5, where <br> is a valid void element.
    // The tests use LINQ to XML for structural assertions, so normalize only
    // this known void element before handing the text to the XML parser.
    internal static XElement ParseElement(string html) => XElement.Parse(Normalize(html));

    internal static XDocument ParseDocument(string html) => XDocument.Parse(Normalize(html));

    private static string Normalize(string html) =>
        html.Replace("<br>", "<br/>", StringComparison.Ordinal);
}
