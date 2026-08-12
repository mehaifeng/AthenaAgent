using System.IO.Compression;
using System.Xml.Linq;

namespace MiniMaxAIDocx.Core.Validation;

public class GateCheckResult
{
    public bool Passed => Violations.Count == 0;
    public List<string> Violations { get; set; } = new();
}

public class GateCheckValidator
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public GateCheckResult Validate(string outputDocxPath, string templateDocxPath)
    {
        var result = new GateCheckResult();

        var templateStyles = ExtractStyles(templateDocxPath);
        var outputStyles = ExtractStyles(outputDocxPath);
        var templateSections = ExtractSectionProperties(templateDocxPath);
        var outputSections = ExtractSectionProperties(outputDocxPath);

        // All template styles must exist in output
        foreach (var style in templateStyles)
        {
            if (!outputStyles.Contains(style))
                result.Violations.Add($"Missing style: '{style}' defined in template but absent from output");
        }

        if (templateSections.Count == 0)
            result.Violations.Add("Template contains no section properties");
        else if (outputSections.Count == 0)
            result.Violations.Add("Output contains no section properties");
        else if (templateSections.Count != 1 && templateSections.Count != outputSections.Count)
        {
            result.Violations.Add(
                $"Section count is not mappable: template={templateSections.Count}, output={outputSections.Count}. " +
                "A template must have one section or the same number of sections as the output.");
        }
        else
        {
            for (var index = 0; index < outputSections.Count; index++)
            {
                var templateIndex = templateSections.Count == 1 ? 0 : index;
                CompareSection(templateSections[templateIndex], outputSections[index], index + 1, result);
            }
        }

        // Default font must match
        var templateFont = ExtractDefaultFont(templateDocxPath);
        var outputFont = ExtractDefaultFont(outputDocxPath);
        if (templateFont != null && !string.Equals(templateFont, outputFont, StringComparison.OrdinalIgnoreCase))
            result.Violations.Add($"Default font mismatch: template='{templateFont}' output='{outputFont}'");

        // Heading font hierarchy consistency
        ValidateHeadingFontHierarchy(outputDocxPath, result);

        return result;
    }

    private HashSet<string> ExtractStyles(string docxPath)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        var entry = zip.GetEntry("word/styles.xml");
        if (entry == null) return new();

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        return doc.Descendants(W + "style")
            .Select(s => (string?)s.Attribute(W + "styleId"))
            .Where(id => id != null)
            .ToHashSet()!;
    }

    private record SectionProps(
        int PageWidth,
        int PageHeight,
        string? Orientation,
        MarginInfo? Margins,
        string? BreakType,
        bool TitlePage,
        string? PageNumberFormat,
        int? PageNumberStart,
        int ColumnCount);
    private record MarginInfo(int Top, int Bottom, int Left, int Right);

    private List<SectionProps> ExtractSectionProperties(string docxPath)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        var entry = zip.GetEntry("word/document.xml");
        if (entry == null) return [];
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

        var result = new List<SectionProps>();
        foreach (var sectPr in doc.Descendants(W + "sectPr"))
        {
            int.TryParse((string?)sectPr.Element(W + "pgSz")?.Attribute(W + "w"), out var pw);
            int.TryParse((string?)sectPr.Element(W + "pgSz")?.Attribute(W + "h"), out var ph);

            var pgMar = sectPr.Element(W + "pgMar");
            MarginInfo? margins = null;
            if (pgMar != null)
            {
                int.TryParse((string?)pgMar.Attribute(W + "top"), out var t);
                int.TryParse((string?)pgMar.Attribute(W + "bottom"), out var b);
                int.TryParse((string?)pgMar.Attribute(W + "left"), out var l);
                int.TryParse((string?)pgMar.Attribute(W + "right"), out var r);
                margins = new(t, b, l, r);
            }

            var pgNum = sectPr.Element(W + "pgNumType");
            int? pageNumberStart = int.TryParse((string?)pgNum?.Attribute(W + "start"), out var start)
                ? start
                : null;
            var columns = sectPr.Element(W + "cols");
            var columnCount = int.TryParse((string?)columns?.Attribute(W + "num"), out var count)
                ? count
                : 1;

            result.Add(new SectionProps(
                pw,
                ph,
                (string?)sectPr.Element(W + "pgSz")?.Attribute(W + "orient"),
                margins,
                (string?)sectPr.Element(W + "type")?.Attribute(W + "val") ?? "nextPage",
                sectPr.Element(W + "titlePg") != null,
                (string?)pgNum?.Attribute(W + "fmt"),
                pageNumberStart,
                columnCount));
        }
        return result;
    }

    private static void CompareSection(
        SectionProps template,
        SectionProps output,
        int outputIndex,
        GateCheckResult result)
    {
        var prefix = $"Section {outputIndex}";
        if (template.PageWidth != 0
            && (template.PageWidth != output.PageWidth || template.PageHeight != output.PageHeight))
            result.Violations.Add(
                $"{prefix} page size mismatch: template=({template.PageWidth}x{template.PageHeight}) " +
                $"output=({output.PageWidth}x{output.PageHeight})");
        if (!string.Equals(template.Orientation, output.Orientation, StringComparison.OrdinalIgnoreCase))
            result.Violations.Add(
                $"{prefix} orientation mismatch: template='{template.Orientation ?? "portrait"}' " +
                $"output='{output.Orientation ?? "portrait"}'");
        if (template.Margins != null && template.Margins != output.Margins)
            result.Violations.Add(
                $"{prefix} margins mismatch: template={template.Margins} output={output.Margins}");
        if (!string.Equals(template.BreakType, output.BreakType, StringComparison.OrdinalIgnoreCase))
            result.Violations.Add(
                $"{prefix} break type mismatch: template='{template.BreakType}' output='{output.BreakType}'");
        if (template.TitlePage != output.TitlePage)
            result.Violations.Add(
                $"{prefix} title-page setting mismatch: template={template.TitlePage} output={output.TitlePage}");
        if (!string.Equals(template.PageNumberFormat, output.PageNumberFormat, StringComparison.OrdinalIgnoreCase)
            || template.PageNumberStart != output.PageNumberStart)
            result.Violations.Add(
                $"{prefix} page-number settings mismatch: template=({template.PageNumberFormat},{template.PageNumberStart}) " +
                $"output=({output.PageNumberFormat},{output.PageNumberStart})");
        if (template.ColumnCount != output.ColumnCount)
            result.Violations.Add(
                $"{prefix} column count mismatch: template={template.ColumnCount} output={output.ColumnCount}");
    }

    private string? ExtractDefaultFont(string docxPath)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        var entry = zip.GetEntry("word/styles.xml");
        if (entry == null) return null;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

        var defaultStyle = doc.Descendants(W + "style")
            .FirstOrDefault(s => (string?)s.Attribute(W + "type") == "paragraph"
                && (string?)s.Attribute(W + "default") == "1");

        return (string?)defaultStyle?.Descendants(W + "rFonts").FirstOrDefault()?.Attribute(W + "ascii");
    }

    private void ValidateHeadingFontHierarchy(string docxPath, GateCheckResult result)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        var entry = zip.GetEntry("word/styles.xml");
        if (entry == null) return;

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);

        var headingSizes = new SortedDictionary<int, int>();
        foreach (var style in doc.Descendants(W + "style"))
        {
            var id = (string?)style.Attribute(W + "styleId");
            if (id == null) continue;

            var outline = (string?)style.Element(W + "pPr")?.Element(W + "outlineLvl")?.Attribute(W + "val");
            int level;
            if (int.TryParse(outline, out var outlineLevel) && outlineLevel is >= 0 and <= 8)
                level = outlineLevel + 1;
            else if (id.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(id.AsSpan(7), out var namedLevel))
                level = namedLevel;
            else
                continue;

            var sz = (string?)style.Descendants(W + "sz").FirstOrDefault()?.Attribute(W + "val");
            if (sz != null && int.TryParse(sz, out var hps))
                headingSizes[level] = hps;
        }

        int prevSize = int.MaxValue;
        foreach (var (level, size) in headingSizes)
        {
            if (size > prevSize)
                result.Violations.Add($"Heading{level} ({size / 2}pt) is larger than a higher-level heading ({prevSize / 2}pt)");
            prevSize = size;
        }
    }
}
