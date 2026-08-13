using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Athena.UI.Services.Documents;

/// <summary>
/// WordprocessingML vocabulary: namespaces, the ordered content models Word enforces, and the
/// small builders every part of the docx service shares. Word is far stricter than Excel about
/// child order — a legal element in the wrong position is what produces the "unreadable content"
/// repair prompt — so all property elements go through <see cref="SetProperty"/>.
/// </summary>
internal static class WordprocessingSchema
{
    public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    public const int TwipsPerPoint = 20;
    public const long EmusPerPoint = 12_700;

    /// <summary>CT_PPr sequence.</summary>
    public static readonly string[] ParagraphPropertyOrder =
    [
        "pStyle", "keepNext", "keepLines", "pageBreakBefore", "framePr", "widowControl", "numPr",
        "suppressLineNumbers", "pBdr", "shd", "tabs", "suppressAutoHyphens", "kinsoku", "wordWrap",
        "overflowPunct", "topLinePunct", "autoSpaceDE", "autoSpaceDN", "bidi", "adjustRightInd",
        "snapToGrid", "spacing", "ind", "contextualSpacing", "mirrorIndents", "suppressOverlap", "jc",
        "textDirection", "textAlignment", "textboxTightWrap", "outlineLvl", "divId", "cnfStyle",
        "rPr", "sectPr", "pPrChange"
    ];

    /// <summary>CT_RPr sequence.</summary>
    public static readonly string[] RunPropertyOrder =
    [
        "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline",
        "shadow", "emboss", "imprint", "noProof", "snapToGrid", "vanish", "webHidden", "color", "spacing",
        "w", "kern", "position", "sz", "szCs", "highlight", "u", "effect", "bdr", "shd", "fitText",
        "vertAlign", "rtl", "cs", "em", "lang", "eastAsianLayout", "specVanish", "oMath"
    ];

    /// <summary>CT_TcPr sequence (the subset Athena writes).</summary>
    public static readonly string[] TableCellPropertyOrder =
    [
        "cnfStyle", "tcW", "gridSpan", "hMerge", "vMerge", "tcBorders", "shd", "noWrap", "tcMar",
        "textDirection", "tcFitText", "vAlign", "hideMark"
    ];

    /// <summary>
    /// Inserts or replaces a property element at the position its schema demands.
    /// </summary>
    public static void SetProperty(XElement properties, IReadOnlyList<string> order, XElement property)
    {
        var name = property.Name.LocalName;
        var position = IndexOf(order, name);
        properties.Elements(W + name).Remove();

        XElement? following = null;
        for (var index = position + 1; index < order.Count && following is null; index++)
            following = properties.Element(W + order[index]);

        if (following is null) properties.Add(property); else following.AddBeforeSelf(property);
    }

    private static int IndexOf(IReadOnlyList<string> order, string name)
    {
        for (var index = 0; index < order.Count; index++)
        {
            if (string.Equals(order[index], name, StringComparison.Ordinal)) return index;
        }
        throw new ArgumentException($"'{name}' is not part of the declared WordprocessingML content model.");
    }

    public static XElement EnsureParagraphProperties(XElement paragraph)
    {
        var properties = paragraph.Element(W + "pPr");
        if (properties is not null) return properties;
        properties = new XElement(W + "pPr");
        paragraph.AddFirst(properties);
        return properties;
    }

    public static XElement EnsureRunProperties(XElement run)
    {
        var properties = run.Element(W + "rPr");
        if (properties is not null) return properties;
        properties = new XElement(W + "rPr");
        run.AddFirst(properties);
        return properties;
    }

    public static XElement Value(string name, object value) => new(W + name, new XAttribute(W + "val", value));

    public static XElement Toggle(string name, bool enabled) =>
        enabled ? new XElement(W + name) : new XElement(W + name, new XAttribute(W + "val", "0"));

    /// <summary>Builds a run, splitting on newlines so multi-line text does not need manual break markup.</summary>
    public static XElement TextRun(string text, XElement? runProperties)
    {
        var run = new XElement(W + "r");
        if (runProperties is not null && runProperties.HasElements) run.Add(new XElement(runProperties));

        var segments = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < segments.Length; index++)
        {
            if (index > 0) run.Add(new XElement(W + "br"));
            if (segments[index].Length == 0) continue;
            run.Add(TextElement(segments[index]));
        }
        return run;
    }

    public static XElement TextElement(string text)
    {
        var element = new XElement(W + "t", text);
        // Word collapses surrounding whitespace unless it is explicitly preserved.
        if (text.Length != text.Trim().Length) element.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        return element;
    }

    /// <summary>Visible text of a paragraph, with breaks and tabs rendered the way a reader sees them.</summary>
    public static string ParagraphText(XElement paragraph)
    {
        var builder = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node.Name == W + "t") builder.Append(node.Value);
            else if (node.Name == W + "tab") builder.Append('\t');
            else if (node.Name == W + "br" || node.Name == W + "cr") builder.Append('\n');
            else if (node.Name == W + "noBreakHyphen") builder.Append('-');
        }
        return builder.ToString();
    }

    public static string? ParagraphStyle(XElement paragraph) =>
        (string?)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val");

    /// <summary>
    /// Heading level of a paragraph: explicit outlineLvl wins, otherwise the HeadingN style id.
    /// Returns null for body text.
    /// </summary>
    public static int? HeadingLevel(XElement paragraph, IReadOnlyDictionary<string, int>? styleOutlineLevels = null)
    {
        var properties = paragraph.Element(W + "pPr");
        var outline = (string?)properties?.Element(W + "outlineLvl")?.Attribute(W + "val");
        if (outline is not null && int.TryParse(outline, NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level <= 8)
            return level + 1;

        var style = ParagraphStyle(paragraph);
        if (style is null) return null;
        if (styleOutlineLevels is not null && styleOutlineLevels.TryGetValue(style, out var mapped)) return mapped + 1;

        var normalized = style.Replace(" ", string.Empty);
        if (!normalized.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)) return null;
        return int.TryParse(normalized[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) && suffix is >= 1 and <= 9
            ? suffix
            : null;
    }

    public static bool IsListParagraph(XElement paragraph) =>
        paragraph.Element(W + "pPr")?.Element(W + "numPr") is not null;

    public static int ListLevel(XElement paragraph)
    {
        var value = (string?)paragraph.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "ilvl")?.Attribute(W + "val");
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var level) ? level : 0;
    }

    public static string HeadingPath(IReadOnlyList<(int Level, string Text)> ancestors) =>
        string.Join(" > ", ancestors.Select(item => item.Text));

    public static int PointsToTwips(double points) => (int)Math.Round(points * TwipsPerPoint, MidpointRounding.AwayFromZero);

    public static long PointsToEmus(double points) => (long)Math.Round(points * EmusPerPoint, MidpointRounding.AwayFromZero);
}
