using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using static Athena.UI.Services.Documents.WordprocessingSchema;

namespace Athena.UI.Services.Documents;

/// <summary>
/// Builds and extends word/styles.xml. New documents get a complete, Word-recognised style set
/// (Normal, Title, Heading 1-6, List Paragraph, Quote, Table Grid) so headings drive the navigation
/// pane and a table of contents without any further setup; custom styles are appended on top.
/// </summary>
internal sealed class DocxStyleLibrary
{
    private static readonly (string Id, string Name, int Level, int HalfPoints, bool Bold)[] HeadingDefinitions =
    [
        ("Heading1", "heading 1", 0, 32, true),
        ("Heading2", "heading 2", 1, 28, true),
        ("Heading3", "heading 3", 2, 24, true),
        ("Heading4", "heading 4", 3, 22, true),
        ("Heading5", "heading 5", 4, 22, false),
        ("Heading6", "heading 6", 5, 21, false)
    ];

    private static readonly HashSet<string> Alignments = new(StringComparer.OrdinalIgnoreCase)
        { "left", "center", "right", "both", "distribute" };

    private readonly XDocument _document;
    private readonly Dictionary<string, string> _customStyles = new(StringComparer.OrdinalIgnoreCase);

    public DocxStyleLibrary(XDocument document) => _document = document;

    public XDocument Document => _document;

    /// <summary>Style ids that already exist in the part, so edits can validate a style reference.</summary>
    public IReadOnlyCollection<string> StyleIds => _document.Root?
        .Elements(W + "style")
        .Select(style => (string?)style.Attribute(W + "styleId") ?? string.Empty)
        .Where(id => id.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps styleId to its outline level so heading detection works with renamed styles.</summary>
    public Dictionary<string, int> OutlineLevels()
    {
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in _document.Root?.Elements(W + "style") ?? [])
        {
            var id = (string?)style.Attribute(W + "styleId");
            var value = (string?)style.Element(W + "pPr")?.Element(W + "outlineLvl")?.Attribute(W + "val");
            if (id is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var level))
                levels[id] = level;
        }
        return levels;
    }

    public static DocxStyleLibrary CreateDefault(string asciiFont, string eastAsiaFont, double sizePoints)
    {
        var styles = new XElement(W + "styles",
            new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName));

        var halfPoints = (int)Math.Round(sizePoints * 2, MidpointRounding.AwayFromZero);
        styles.Add(new XElement(W + "docDefaults",
            new XElement(W + "rPrDefault",
                new XElement(W + "rPr",
                    new XElement(W + "rFonts",
                        new XAttribute(W + "ascii", asciiFont),
                        new XAttribute(W + "hAnsi", asciiFont),
                        new XAttribute(W + "eastAsia", eastAsiaFont),
                        new XAttribute(W + "cs", asciiFont)),
                    Value("sz", halfPoints),
                    Value("szCs", halfPoints))),
            new XElement(W + "pPrDefault",
                new XElement(W + "pPr",
                    new XElement(W + "spacing",
                        new XAttribute(W + "after", 120),
                        new XAttribute(W + "line", 276),
                        new XAttribute(W + "lineRule", "auto"))))));

        styles.Add(BuildStyle("paragraph", "Normal", "Normal", isDefault: true, quickFormat: true));

        var title = BuildStyle("paragraph", "Title", "Title", basedOn: "Normal", next: "Normal", quickFormat: true);
        title.Add(new XElement(W + "pPr",
            new XElement(W + "spacing", new XAttribute(W + "after", 240)),
            Value("jc", "center")));
        title.Add(new XElement(W + "rPr", new XElement(W + "b"), Value("sz", 56)));
        styles.Add(title);

        foreach (var (id, name, level, size, bold) in HeadingDefinitions)
        {
            var heading = BuildStyle("paragraph", id, name, basedOn: "Normal", next: "Normal", quickFormat: true);
            heading.Add(new XElement(W + "pPr",
                new XElement(W + "keepNext"),
                new XElement(W + "keepLines"),
                new XElement(W + "spacing", new XAttribute(W + "before", 240), new XAttribute(W + "after", 120)),
                Value("outlineLvl", level)));
            var runProperties = new XElement(W + "rPr", Value("sz", size), Value("szCs", size));
            if (bold) runProperties.AddFirst(new XElement(W + "b"));
            heading.Add(runProperties);
            styles.Add(heading);
        }

        var list = BuildStyle("paragraph", "ListParagraph", "List Paragraph", basedOn: "Normal", quickFormat: true);
        list.Add(new XElement(W + "pPr",
            new XElement(W + "ind", new XAttribute(W + "left", 420)),
            new XElement(W + "contextualSpacing")));
        styles.Add(list);

        var quote = BuildStyle("paragraph", "Quote", "Quote", basedOn: "Normal", next: "Normal", quickFormat: true);
        // spacing precedes ind in CT_PPr; writing them the other way round makes Word offer a repair.
        quote.Add(new XElement(W + "pPr",
            new XElement(W + "spacing", new XAttribute(W + "before", 120), new XAttribute(W + "after", 120)),
            new XElement(W + "ind", new XAttribute(W + "left", 480), new XAttribute(W + "right", 480))));
        quote.Add(new XElement(W + "rPr", new XElement(W + "i"), Value("color", "595959")));
        styles.Add(quote);

        var tableGrid = BuildStyle("table", "TableGrid", "Table Grid", basedOn: "TableNormal");
        tableGrid.Add(new XElement(W + "tblPr",
            new XElement(W + "tblBorders",
                Border("top"), Border("left"), Border("bottom"), Border("right"),
                Border("insideH"), Border("insideV"))));
        styles.Add(BuildStyle("table", "TableNormal", "Normal Table", isDefault: true));
        styles.Add(tableGrid);

        return new DocxStyleLibrary(new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), styles));
    }

    private static XElement Border(string side) => new(W + side,
        new XAttribute(W + "val", "single"),
        new XAttribute(W + "sz", 4),
        new XAttribute(W + "space", 0),
        new XAttribute(W + "color", "BFBFBF"));

    private static XElement BuildStyle(string type, string id, string name, string? basedOn = null,
        string? next = null, bool isDefault = false, bool quickFormat = false)
    {
        var style = new XElement(W + "style",
            new XAttribute(W + "type", type),
            new XAttribute(W + "styleId", id));
        if (isDefault) style.SetAttributeValue(W + "default", 1);
        style.Add(Value("name", name));
        if (basedOn is not null) style.Add(Value("basedOn", basedOn));
        if (next is not null) style.Add(Value("next", next));
        if (quickFormat) style.Add(new XElement(W + "qFormat"));
        return style;
    }

    /// <summary>Registers a custom paragraph style and returns its style id.</summary>
    public string RegisterStyle(JsonElement spec)
    {
        if (spec.ValueKind != JsonValueKind.Object) throw new ArgumentException("A style specification must be an object.");
        if (!spec.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Each custom style needs a 'name'.");

        var name = nameElement.GetString()!.Trim();
        if (name.Length is 0 or > 64) throw new ArgumentException("Custom style names must be 1-64 characters.");
        if (_customStyles.TryGetValue(name, out var existing)) return existing;

        var id = MakeStyleId(name);
        var basedOn = spec.TryGetProperty("basedOn", out var basedOnElement) && basedOnElement.ValueKind == JsonValueKind.String
            ? basedOnElement.GetString()!.Trim()
            : "Normal";
        if (!StyleIds.Contains(basedOn)) throw new ArgumentException($"Unknown basedOn style: {basedOn}");

        var style = BuildStyle("paragraph", id, name, basedOn: basedOn, next: "Normal", quickFormat: true);
        var paragraphProperties = BuildParagraphProperties(spec);
        if (paragraphProperties.HasElements) style.Add(paragraphProperties);
        var runProperties = BuildRunProperties(spec.TryGetProperty("font", out var font) ? font : default);
        if (runProperties.HasElements) style.Add(runProperties);

        _document.Root!.Add(style);
        _customStyles[name] = id;
        return id;
    }

    public bool TryResolveName(string name, out string styleId)
    {
        if (_customStyles.TryGetValue(name, out var custom))
        {
            styleId = custom;
            return true;
        }
        var match = StyleIds.FirstOrDefault(id => id.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? StyleIds.FirstOrDefault(id => id.Equals(name.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase));
        styleId = match ?? string.Empty;
        return match is not null;
    }

    private string MakeStyleId(string name)
    {
        var candidate = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (candidate.Length == 0) candidate = "Custom";
        if (char.IsDigit(candidate[0])) candidate = "S" + candidate;
        var unique = candidate;
        var suffix = 2;
        var taken = StyleIds;
        while (taken.Contains(unique)) unique = candidate + suffix++;
        return unique;
    }

    /// <summary>Shared by custom styles and direct paragraph formatting.</summary>
    public static XElement BuildParagraphProperties(JsonElement spec)
    {
        var properties = new XElement(W + "pPr");

        if (spec.TryGetProperty("align", out var align) && align.ValueKind == JsonValueKind.String)
        {
            var value = NormalizeAlignment(align.GetString());
            SetProperty(properties, ParagraphPropertyOrder, Value("jc", value));
        }

        if (spec.TryGetProperty("spacing", out var spacing) && spacing.ValueKind == JsonValueKind.Object)
        {
            var element = new XElement(W + "spacing");
            if (spacing.TryGetProperty("before", out var before) && before.TryGetDouble(out var beforeValue))
                element.SetAttributeValue(W + "before", PointsToTwips(Clamp(beforeValue, 0, 1584, "spacing.before")));
            if (spacing.TryGetProperty("after", out var after) && after.TryGetDouble(out var afterValue))
                element.SetAttributeValue(W + "after", PointsToTwips(Clamp(afterValue, 0, 1584, "spacing.after")));
            if (spacing.TryGetProperty("line", out var line) && line.TryGetDouble(out var lineValue))
            {
                element.SetAttributeValue(W + "line", (int)Math.Round(Clamp(lineValue, 0.5, 10, "spacing.line") * 240));
                element.SetAttributeValue(W + "lineRule", "auto");
            }
            if (element.HasAttributes) SetProperty(properties, ParagraphPropertyOrder, element);
        }

        if (spec.TryGetProperty("indent", out var indent) && indent.ValueKind == JsonValueKind.Object)
        {
            var element = new XElement(W + "ind");
            if (indent.TryGetProperty("left", out var left) && left.TryGetDouble(out var leftValue))
                element.SetAttributeValue(W + "left", PointsToTwips(Clamp(leftValue, 0, 1584, "indent.left")));
            if (indent.TryGetProperty("right", out var right) && right.TryGetDouble(out var rightValue))
                element.SetAttributeValue(W + "right", PointsToTwips(Clamp(rightValue, 0, 1584, "indent.right")));
            if (indent.TryGetProperty("firstLine", out var firstLine) && firstLine.TryGetDouble(out var firstLineValue))
                element.SetAttributeValue(W + "firstLine", PointsToTwips(Clamp(firstLineValue, 0, 1584, "indent.firstLine")));
            if (element.HasAttributes) SetProperty(properties, ParagraphPropertyOrder, element);
        }

        if (spec.TryGetProperty("keepNext", out var keepNext) && keepNext.ValueKind == JsonValueKind.True)
            SetProperty(properties, ParagraphPropertyOrder, new XElement(W + "keepNext"));
        if (spec.TryGetProperty("pageBreakBefore", out var breakBefore) && breakBefore.ValueKind == JsonValueKind.True)
            SetProperty(properties, ParagraphPropertyOrder, new XElement(W + "pageBreakBefore"));

        return properties;
    }

    public static XElement BuildRunProperties(JsonElement font)
    {
        var properties = new XElement(W + "rPr");
        if (font.ValueKind != JsonValueKind.Object) return properties;

        var ascii = font.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()!.Trim()
            : null;
        var eastAsia = font.TryGetProperty("eastAsia", out var eastAsiaElement) && eastAsiaElement.ValueKind == JsonValueKind.String
            ? eastAsiaElement.GetString()!.Trim()
            : null;
        if (ascii is not null || eastAsia is not null)
        {
            var fonts = new XElement(W + "rFonts");
            if (ascii is not null)
            {
                fonts.SetAttributeValue(W + "ascii", ascii);
                fonts.SetAttributeValue(W + "hAnsi", ascii);
                fonts.SetAttributeValue(W + "cs", ascii);
            }
            if (eastAsia is not null) fonts.SetAttributeValue(W + "eastAsia", eastAsia);
            SetProperty(properties, RunPropertyOrder, fonts);
        }

        if (font.TryGetProperty("bold", out var bold) && bold.ValueKind is JsonValueKind.True or JsonValueKind.False)
            SetProperty(properties, RunPropertyOrder, Toggle("b", bold.GetBoolean()));
        if (font.TryGetProperty("italic", out var italic) && italic.ValueKind is JsonValueKind.True or JsonValueKind.False)
            SetProperty(properties, RunPropertyOrder, Toggle("i", italic.GetBoolean()));
        if (font.TryGetProperty("strike", out var strike) && strike.ValueKind == JsonValueKind.True)
            SetProperty(properties, RunPropertyOrder, new XElement(W + "strike"));
        if (font.TryGetProperty("underline", out var underline) && underline.ValueKind == JsonValueKind.True)
            SetProperty(properties, RunPropertyOrder, Value("u", "single"));
        if (font.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.String)
            SetProperty(properties, RunPropertyOrder, Value("color", NormalizeColor(color.GetString())));
        if (font.TryGetProperty("highlight", out var highlight) && highlight.ValueKind == JsonValueKind.String)
            SetProperty(properties, RunPropertyOrder, Value("highlight", highlight.GetString()!.Trim().ToLowerInvariant()));
        if (font.TryGetProperty("size", out var size))
        {
            if (!size.TryGetDouble(out var points) || points is < 1 or > 400)
                throw new ArgumentException("Font 'size' must be a number of points from 1 to 400.");
            var halfPoints = (int)Math.Round(points * 2, MidpointRounding.AwayFromZero);
            SetProperty(properties, RunPropertyOrder, Value("sz", halfPoints));
            SetProperty(properties, RunPropertyOrder, Value("szCs", halfPoints));
        }

        return properties;
    }

    public static string NormalizeAlignment(string? value)
    {
        var alignment = (value ?? string.Empty).Trim();
        if (alignment.Equals("justify", StringComparison.OrdinalIgnoreCase)) alignment = "both";
        if (!Alignments.Contains(alignment))
            throw new ArgumentException($"Unsupported alignment '{value}'. Use left, center, right, justify or distribute.");
        return alignment.ToLowerInvariant();
    }

    public static string NormalizeColor(string? value)
    {
        var color = (value ?? string.Empty).Trim().TrimStart('#');
        if (color.Length == 8) color = color[2..];
        if (color.Length != 6 || !color.All(Uri.IsHexDigit))
            throw new ArgumentException($"Colour '{value}' must be RGB hex such as '1F3864'.");
        return color.ToUpperInvariant();
    }

    private static double Clamp(double value, double minimum, double maximum, string label) =>
        value >= minimum && value <= maximum
            ? value
            : throw new ArgumentException($"'{label}' must be between {minimum} and {maximum} points.");
}
