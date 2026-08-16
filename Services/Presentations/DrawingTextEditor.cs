using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using static Athena.UI.Services.Presentations.PresentationSchema;

namespace Athena.UI.Services.Presentations;

/// <summary>
/// Text edits for DrawingML paragraphs. PowerPoint splits visible words across arbitrary runs, so
/// replacements operate on the paragraph's logical text while preserving the formatting of the
/// run where each match begins.
/// </summary>
internal static class DrawingTextEditor
{
    private readonly record struct TextSpan(XElement Element, int Start);

    public static int Replace(XElement paragraph, string search, string replacement, bool replaceAll)
    {
        if (search.Length == 0) return 0;
        var spans = CollectSpans(paragraph);
        if (spans.Count == 0) return 0;

        var logicalText = string.Concat(spans.Select(span => span.Element.Value));
        var matches = new List<int>();
        for (var cursor = 0; cursor <= logicalText.Length - search.Length;)
        {
            var found = logicalText.IndexOf(search, cursor, StringComparison.Ordinal);
            if (found < 0) break;
            matches.Add(found);
            cursor = found + search.Length;
            if (!replaceAll) break;
        }

        for (var index = matches.Count - 1; index >= 0; index--)
            ApplyReplacement(spans, matches[index], search.Length, replacement);

        Cleanup(spans);
        return matches.Count;
    }

    public static void SetText(XElement textBody, string text)
    {
        var firstParagraph = textBody.Elements(A + "p").FirstOrDefault();
        var paragraphProperties = firstParagraph?.Element(A + "pPr");
        var runProperties = firstParagraph?.Descendants(A + "rPr").FirstOrDefault();
        var endProperties = firstParagraph?.Element(A + "endParaRPr");

        textBody.Elements(A + "p").Remove();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var paragraph = new XElement(A + "p",
                paragraphProperties is null ? null : new XElement(paragraphProperties));
            if (line.Length > 0)
            {
                var textElement = new XElement(A + "t", line);
                PreserveSpace(textElement);
                paragraph.Add(new XElement(A + "r",
                    runProperties is null ? null : new XElement(runProperties),
                    textElement));
            }
            paragraph.Add(endProperties is null
                ? new XElement(A + "endParaRPr", new XAttribute("lang", "en-US"))
                : new XElement(endProperties));
            textBody.Add(paragraph);
        }
    }

    public static string ParagraphText(XElement paragraph) =>
        string.Concat(paragraph.Descendants(A + "t").Select(text => text.Value));

    public static string TextBodyText(XElement textBody) =>
        string.Join("\n", textBody.Elements(A + "p").Select(ParagraphText));

    private static List<TextSpan> CollectSpans(XElement paragraph)
    {
        var spans = new List<TextSpan>();
        var offset = 0;
        foreach (var text in paragraph.Descendants(A + "t"))
        {
            spans.Add(new TextSpan(text, offset));
            offset += text.Value.Length;
        }
        return spans;
    }

    private static void ApplyReplacement(IReadOnlyList<TextSpan> spans, int start, int length, string replacement)
    {
        var end = start + length;
        var written = false;
        foreach (var span in spans)
        {
            var spanStart = span.Start;
            var spanEnd = span.Start + span.Element.Value.Length;
            if (spanEnd <= start || spanStart >= end) continue;

            var localStart = Math.Max(0, start - spanStart);
            var localEnd = Math.Min(span.Element.Value.Length, end - spanStart);
            var value = span.Element.Value;
            span.Element.Value = value[..localStart] + (written ? string.Empty : replacement) + value[localEnd..];
            written = true;
        }
    }

    private static void Cleanup(IEnumerable<TextSpan> spans)
    {
        foreach (var span in spans)
        {
            if (span.Element.Value.Length > 0)
            {
                PreserveSpace(span.Element);
                continue;
            }

            var run = span.Element.Parent;
            span.Element.Remove();
            if (run is not null && run.Name == A + "r" && !run.Elements().Any(child => child.Name != A + "rPr"))
                run.Remove();
        }
    }

    private static void PreserveSpace(XElement text)
    {
        if (text.Value.Length != text.Value.Trim().Length) text.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        else text.Attribute(XNamespace.Xml + "space")?.Remove();
    }
}
