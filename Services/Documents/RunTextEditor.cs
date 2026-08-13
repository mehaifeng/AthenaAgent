using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using static Athena.UI.Services.Documents.WordprocessingSchema;

namespace Athena.UI.Services.Documents;

/// <summary>
/// Find-and-replace across the runs of a paragraph. Word splits a single sentence into arbitrarily
/// many runs (spell-check state, revision ids, formatting), so a match routinely straddles several
/// w:t elements; naive per-element replacement would silently miss those.
/// </summary>
internal static class RunTextEditor
{
    private readonly record struct TextSpan(XElement Element, int Start, int Length);

    /// <summary>
    /// Replaces occurrences of <paramref name="search"/> and returns how many were rewritten.
    /// Replacement text inherits the formatting of the run where the match starts.
    /// </summary>
    public static int Replace(XElement paragraph, string search, string replacement, bool replaceAll)
    {
        if (search.Length == 0) return 0;

        var spans = CollectSpans(paragraph);
        if (spans.Count == 0) return 0;

        var text = string.Concat(spans.Select(span => span.Element.Value));
        var matches = new List<int>();
        var cursor = 0;
        while (cursor <= text.Length - search.Length)
        {
            var index = text.IndexOf(search, cursor, StringComparison.Ordinal);
            if (index < 0) break;
            matches.Add(index);
            cursor = index + search.Length;
            if (!replaceAll) break;
        }
        if (matches.Count == 0) return 0;

        // Apply from the end so earlier offsets stay valid.
        for (var index = matches.Count - 1; index >= 0; index--)
            ApplyReplacement(spans, matches[index], search.Length, replacement);

        foreach (var span in spans)
        {
            if (span.Element.Value.Length == 0)
            {
                // An emptied w:t keeps its run alive but adds nothing; drop the run when it holds no other content.
                var run = span.Element.Parent;
                span.Element.Remove();
                if (run is not null && !run.Elements().Any(child => child.Name != W + "rPr")) run.Remove();
            }
            else
            {
                PreserveSpace(span.Element);
            }
        }

        return matches.Count;
    }

    private static void ApplyReplacement(List<TextSpan> spans, int start, int length, string replacement)
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
            var head = value[..localStart];
            var tail = value[localEnd..];

            span.Element.Value = written ? head + tail : head + replacement + tail;
            written = true;
        }
    }

    /// <summary>
    /// Text-bearing elements in document order with their offset in the paragraph's visible text.
    /// Field instructions and deleted (tracked-change) text are deliberately excluded.
    /// </summary>
    private static List<TextSpan> CollectSpans(XElement paragraph)
    {
        var spans = new List<TextSpan>();
        var offset = 0;
        foreach (var element in paragraph.Descendants(W + "t"))
        {
            if (element.Ancestors(W + "del").Any() || element.Ancestors(W + "instrText").Any()) continue;
            spans.Add(new TextSpan(element, offset, element.Value.Length));
            offset += element.Value.Length;
        }

        // Offsets must reflect the concatenated text, so rebuild them after filtering.
        var rebuilt = new List<TextSpan>(spans.Count);
        var running = 0;
        foreach (var span in spans)
        {
            rebuilt.Add(span with { Start = running });
            running += span.Element.Value.Length;
        }
        return rebuilt;
    }

    private static void PreserveSpace(XElement element)
    {
        var value = element.Value;
        if (value.Length != value.Trim().Length) element.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        else element.Attribute(XNamespace.Xml + "space")?.Remove();
    }

    /// <summary>
    /// Replaces every run of a paragraph with one run carrying the new text, keeping the paragraph's
    /// own properties and the formatting of the run that used to come first.
    /// </summary>
    public static void SetParagraphText(XElement paragraph, string text)
    {
        var firstRun = paragraph.Elements(W + "r").FirstOrDefault();
        var runProperties = firstRun?.Element(W + "rPr");
        paragraph.Elements().Where(child => child.Name != W + "pPr").Remove();
        if (text.Length > 0) paragraph.Add(TextRun(text, runProperties));
    }

    /// <summary>Applies run formatting to every run of a paragraph without touching its text.</summary>
    public static void ApplyRunFormatting(XElement paragraph, XElement formatting)
    {
        foreach (var run in paragraph.Elements(W + "r"))
        {
            var properties = EnsureRunProperties(run);
            foreach (var property in formatting.Elements()) SetProperty(properties, RunPropertyOrder, new XElement(property));
        }
    }
}
