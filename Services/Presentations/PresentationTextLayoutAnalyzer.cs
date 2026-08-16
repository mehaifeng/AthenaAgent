using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using static Athena.UI.Services.Presentations.PresentationSchema;

namespace Athena.UI.Services.Presentations;

/// <summary>
/// Conservative DrawingML text-fit estimator. It intentionally reports only obvious overflow and
/// never claims PowerPoint-equivalent layout: theme inheritance, groups and font substitution can
/// change the final result in the real renderer.
/// </summary>
internal static class PresentationTextLayoutAnalyzer
{
    internal sealed record Finding(int Slide, int ShapeId, string ShapeName, double EstimatedHeightPoints,
        double AvailableHeightPoints, string Confidence, string Message);

    public static IReadOnlyList<Finding> Analyze(XDocument slide, int slideNumber)
    {
        var findings = new List<Finding>();
        foreach (var shape in slide.Descendants(P + "sp"))
        {
            var textBody = shape.Element(P + "txBody");
            if (textBody is null) continue;
            var bodyProperties = textBody.Element(A + "bodyPr");
            if (bodyProperties?.Element(A + "spAutoFit") is not null) continue;

            var (_, _, widthEmu, heightEmu) = PptxPackageService.ReadBounds(shape);
            if (widthEmu is not > 0 || heightEmu is not > 0) continue;
            var text = DrawingTextEditor.TextBodyText(textBody);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var left = ReadLong(bodyProperties, "lIns", 91_440);
            var right = ReadLong(bodyProperties, "rIns", 91_440);
            var top = ReadLong(bodyProperties, "tIns", 45_720);
            var bottom = ReadLong(bodyProperties, "bIns", 45_720);
            var availableWidth = Math.Max(1, (widthEmu.Value - left - right) / (double)EmusPerPoint);
            var availableHeight = Math.Max(1, (heightEmu.Value - top - bottom) / (double)EmusPerPoint);

            var fontSize = ResolveFontSize(textBody);
            var normalAutofit = bodyProperties?.Element(A + "normAutofit");
            var fontScale = ReadInt(normalAutofit, "fontScale", 100_000) / 100_000d;
            var lineReduction = ReadInt(normalAutofit, "lnSpcReduction", 0) / 100_000d;
            fontSize *= Math.Clamp(fontScale, 0.1, 1);
            var lineHeight = fontSize * Math.Max(0.7, 1.2 - lineReduction);
            var typefaceName = ResolveTypeface(textBody);
            using var typeface = SKTypeface.FromFamilyName(typefaceName) ?? SKTypeface.Default;
            using var font = new SKFont(typeface, (float)fontSize);

            var estimatedLines = 0;
            foreach (var paragraph in textBody.Elements(A + "p"))
            {
                var paragraphText = DrawingTextEditor.ParagraphText(paragraph);
                estimatedLines += Math.Max(1, CountLines(paragraphText, availableWidth, font));
                estimatedLines += ParagraphSpacingLines(paragraph, fontSize);
            }
            var estimatedHeight = estimatedLines * lineHeight;
            if (estimatedHeight <= availableHeight * 1.04) continue;

            var nonVisual = shape.Element(P + "nvSpPr")?.Element(P + "cNvPr");
            var id = int.TryParse((string?)nonVisual?.Attribute("id"), out var parsed) ? parsed : 0;
            var name = (string?)nonVisual?.Attribute("name") ?? $"Shape {id}";
            var confidence = shape.Ancestors(P + "grpSp").Any()
                || HasThemeTypeface(textBody)
                || (string?)bodyProperties?.Attribute("vert") is { } vertical && vertical != "horz"
                ? "low"
                : "medium";
            findings.Add(new Finding(slideNumber, id, name,
                Math.Round(estimatedHeight, 1), Math.Round(availableHeight, 1), confidence,
                $"Slide {slideNumber}, shape {id} ('{name}') may overflow: estimated {estimatedHeight:F1} pt needs more than {availableHeight:F1} pt."));
        }
        return findings;
    }

    private static int CountLines(string text, double availableWidth, SKFont font)
    {
        if (text.Length == 0) return 1;
        var tokens = Tokenize(text);
        var lines = 1;
        var width = 0d;
        foreach (var token in tokens)
        {
            var tokenWidth = font.MeasureText(token);
            if (width > 0 && width + tokenWidth > availableWidth)
            {
                lines++;
                width = token.TrimStart().Length == 0 ? 0 : font.MeasureText(token.TrimStart());
            }
            else if (tokenWidth > availableWidth)
            {
                foreach (var rune in token.EnumerateRunes())
                {
                    var characterWidth = font.MeasureText(rune.ToString());
                    if (width > 0 && width + characterWidth > availableWidth)
                    {
                        lines++;
                        width = 0;
                    }
                    width += characterWidth;
                }
            }
            else width += tokenWidth;
        }
        return lines;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var buffer = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var isCjk = value is >= 0x2E80 and <= 0x9FFF || value is >= 0xF900 and <= 0xFAFF;
            if (isCjk)
            {
                if (buffer.Length > 0) { yield return buffer.ToString(); buffer.Clear(); }
                yield return rune.ToString();
            }
            else
            {
                buffer.Append(rune.ToString());
                if (Rune.IsWhiteSpace(rune)) { yield return buffer.ToString(); buffer.Clear(); }
            }
        }
        if (buffer.Length > 0) yield return buffer.ToString();
    }

    private static int ParagraphSpacingLines(XElement paragraph, double fontSize)
    {
        var properties = paragraph.Element(A + "pPr");
        var after = properties?.Element(A + "spcAft")?.Element(A + "spcPts");
        return int.TryParse((string?)after?.Attribute("val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Ceiling(value / 100d / Math.Max(fontSize, 1))
            : 0;
    }

    private static double ResolveFontSize(XElement textBody)
    {
        foreach (var properties in textBody.Descendants().Where(element => element.Name is var name
                     && (name == A + "rPr" || name == A + "defRPr" || name == A + "endParaRPr")))
        {
            if (int.TryParse((string?)properties.Attribute("sz"), out var size) && size is >= 100 and <= 40_000)
                return size / 100d;
        }
        var placeholder = textBody.Parent?.Descendants(P + "ph").FirstOrDefault();
        return (string?)placeholder?.Attribute("type") is "title" or "ctrTitle" ? 32 : 18;
    }

    private static string ResolveTypeface(XElement textBody)
    {
        foreach (var latin in textBody.Descendants(A + "latin"))
        {
            var typeface = (string?)latin.Attribute("typeface");
            if (!string.IsNullOrWhiteSpace(typeface) && !typeface.StartsWith('+')) return typeface;
        }
        return "Arial";
    }

    private static bool HasThemeTypeface(XElement textBody) => textBody.Descendants(A + "latin")
        .Select(element => (string?)element.Attribute("typeface"))
        .Any(value => value?.StartsWith('+') == true);

    private static long ReadLong(XElement? element, string attribute, long fallback) =>
        long.TryParse((string?)element?.Attribute(attribute), out var value) && value >= 0 ? value : fallback;

    private static int ReadInt(XElement? element, string attribute, int fallback) =>
        int.TryParse((string?)element?.Attribute(attribute), out var value) ? value : fallback;
}
