using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

namespace Athena.UI.Services.Spreadsheets;

/// <summary>
/// Builds and extends the styles part of a workbook so callers are no longer limited to the
/// fixed alias table. Custom styles are appended to the existing fonts/fills/borders/numFmts
/// tables, which keeps every pre-existing cell style index valid.
/// </summary>
internal sealed class XlsxStyleLibrary
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly string[] StyleSheetOrder =
    [
        "numFmts", "fonts", "fills", "borders", "cellStyleXfs", "cellXfs",
        "cellStyles", "dxfs", "tableStyles", "colors", "extLst"
    ];

    private static readonly HashSet<string> HorizontalAlignments = new(StringComparer.OrdinalIgnoreCase)
        { "general", "left", "center", "right", "fill", "justify", "centerContinuous", "distributed" };

    private static readonly HashSet<string> VerticalAlignments = new(StringComparer.OrdinalIgnoreCase)
        { "top", "center", "bottom", "justify", "distributed" };

    private static readonly HashSet<string> BorderStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "thin", "medium", "thick", "double", "dotted", "dashed", "hair",
        "mediumDashed", "dashDot", "mediumDashDot", "dashDotDot", "mediumDashDotDot", "slantDashDot"
    };

    private readonly XDocument _document;
    private readonly Dictionary<string, int> _namedStyles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _specCache = new(StringComparer.Ordinal);
    private readonly string _defaultFontName;
    private readonly double _defaultFontSize;

    public XlsxStyleLibrary(XDocument document)
    {
        _document = document;
        if (_document.Root is null) throw new InvalidDataException("styles.xml has no root element.");
        var baseFont = _document.Root.Element(Ns + "fonts")?.Elements(Ns + "font").FirstOrDefault();
        _defaultFontName = (string?)baseFont?.Element(Ns + "name")?.Attribute("val") ?? "Calibri";
        _defaultFontSize = double.TryParse((string?)baseFont?.Element(Ns + "sz")?.Attribute("val"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? size : 11d;
    }

    public XDocument Document => _document;

    public int CellFormatCount => Container("cellXfs").Elements(Ns + "xf").Count();

    /// <summary>Registers the workbook-level "styles" array of create_spreadsheet.</summary>
    public void RegisterNamedStyles(JsonElement styles, Func<string, bool> isReservedAlias)
    {
        if (styles.ValueKind != JsonValueKind.Array) throw new ArgumentException("'styles' must be an array of style objects.");
        var specs = styles.EnumerateArray().ToList();
        if (specs.Count > 128) throw new ArgumentException("A workbook may define at most 128 custom styles.");

        foreach (var spec in specs)
        {
            if (spec.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each entry of 'styles' must be an object.");
            if (!spec.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Each custom style needs a 'name'.");
            var name = nameElement.GetString()!.Trim();
            if (name.Length is 0 or > 64) throw new ArgumentException("Custom style names must be 1-64 characters.");
            if (isReservedAlias(name)) throw new ArgumentException($"'{name}' is a built-in style alias and cannot be redefined.");
            if (!_namedStyles.TryAdd(name, Register(spec))) throw new ArgumentException($"Duplicate custom style name: {name}");
        }
    }

    public bool TryResolveName(string name, out int index) => _namedStyles.TryGetValue(name, out index);

    /// <summary>Appends the fonts/fills/borders/number format implied by a style object and returns its cellXfs index.</summary>
    public int Register(JsonElement spec)
    {
        if (spec.ValueKind != JsonValueKind.Object) throw new ArgumentException("A style specification must be an object.");
        var cacheKey = spec.GetRawText();
        if (_specCache.TryGetValue(cacheKey, out var cached)) return cached;

        var format = new XElement(Ns + "xf", new XAttribute("xfId", 0));
        var numberFormatId = 0;
        if (spec.TryGetProperty("numberFormat", out var numberFormat))
        {
            if (numberFormat.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(numberFormat.GetString()))
                throw new ArgumentException("'numberFormat' must be a non-empty format code such as '#,##0.00' or 'yyyy-mm-dd'.");
            var code = numberFormat.GetString()!;
            if (code.Length > 255) throw new ArgumentException("'numberFormat' is longer than 255 characters.");
            numberFormatId = AddNumberFormat(code);
            format.SetAttributeValue("applyNumberFormat", 1);
        }

        var fontId = 0;
        if (spec.TryGetProperty("font", out var font))
        {
            fontId = AddFont(font);
            format.SetAttributeValue("applyFont", 1);
        }

        var fillId = 0;
        if (spec.TryGetProperty("fill", out var fill))
        {
            fillId = AddFill(fill);
            format.SetAttributeValue("applyFill", 1);
        }

        var borderId = 0;
        if (spec.TryGetProperty("border", out var border))
        {
            borderId = AddBorder(border);
            format.SetAttributeValue("applyBorder", 1);
        }

        format.SetAttributeValue("numFmtId", numberFormatId);
        format.SetAttributeValue("fontId", fontId);
        format.SetAttributeValue("fillId", fillId);
        format.SetAttributeValue("borderId", borderId);

        if (spec.TryGetProperty("align", out var align))
        {
            format.Add(BuildAlignment(align));
            format.SetAttributeValue("applyAlignment", 1);
        }

        var container = Container("cellXfs");
        var index = container.Elements(Ns + "xf").Count();
        container.Add(format);
        SetCount(container);
        _specCache[cacheKey] = index;
        return index;
    }

    private XElement BuildAlignment(JsonElement align)
    {
        if (align.ValueKind != JsonValueKind.Object) throw new ArgumentException("'align' must be an object.");
        var alignment = new XElement(Ns + "alignment");

        if (align.TryGetProperty("horizontal", out var horizontal))
        {
            var value = horizontal.GetString() ?? string.Empty;
            if (!HorizontalAlignments.Contains(value)) throw new ArgumentException($"Unsupported horizontal alignment: {value}");
            alignment.SetAttributeValue("horizontal", value);
        }

        if (align.TryGetProperty("vertical", out var vertical))
        {
            var value = vertical.GetString() ?? string.Empty;
            if (!VerticalAlignments.Contains(value)) throw new ArgumentException($"Unsupported vertical alignment: {value}");
            alignment.SetAttributeValue("vertical", value);
        }

        if (align.TryGetProperty("wrap", out var wrap) && wrap.ValueKind is JsonValueKind.True or JsonValueKind.False)
            alignment.SetAttributeValue("wrapText", wrap.GetBoolean() ? 1 : 0);

        if (align.TryGetProperty("indent", out var indent))
        {
            if (!indent.TryGetInt32(out var value) || value is < 0 or > 250) throw new ArgumentException("'indent' must be an integer from 0 to 250.");
            alignment.SetAttributeValue("indent", value);
        }

        if (align.TryGetProperty("rotation", out var rotation))
        {
            if (!rotation.TryGetInt32(out var value) || value is < -90 or > 180) throw new ArgumentException("'rotation' must be an integer from -90 to 180.");
            alignment.SetAttributeValue("textRotation", value < 0 ? 90 - value : value);
        }

        return alignment;
    }

    private int AddNumberFormat(string code)
    {
        var container = Container("numFmts");
        var existing = container.Elements(Ns + "numFmt")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("formatCode"), code, StringComparison.Ordinal));
        if (existing is not null && int.TryParse((string?)existing.Attribute("numFmtId"), out var reused)) return reused;

        var nextId = 164;
        foreach (var element in container.Elements(Ns + "numFmt"))
        {
            if (int.TryParse((string?)element.Attribute("numFmtId"), out var id) && id >= nextId) nextId = id + 1;
        }
        if (nextId > 65_000) throw new InvalidOperationException("The workbook already defines too many custom number formats.");

        container.Add(new XElement(Ns + "numFmt", new XAttribute("numFmtId", nextId), new XAttribute("formatCode", code)));
        SetCount(container);
        return nextId;
    }

    private int AddFont(JsonElement spec)
    {
        if (spec.ValueKind != JsonValueKind.Object) throw new ArgumentException("'font' must be an object.");
        var font = new XElement(Ns + "font");

        if (spec.TryGetProperty("bold", out var bold) && bold.ValueKind == JsonValueKind.True) font.Add(new XElement(Ns + "b"));
        if (spec.TryGetProperty("italic", out var italic) && italic.ValueKind == JsonValueKind.True) font.Add(new XElement(Ns + "i"));
        if (spec.TryGetProperty("strike", out var strike) && strike.ValueKind == JsonValueKind.True) font.Add(new XElement(Ns + "strike"));
        if (spec.TryGetProperty("underline", out var underline) && underline.ValueKind == JsonValueKind.True) font.Add(new XElement(Ns + "u"));

        var size = _defaultFontSize;
        if (spec.TryGetProperty("size", out var sizeElement))
        {
            if (!sizeElement.TryGetDouble(out size) || size is < 1 or > 409) throw new ArgumentException("Font 'size' must be a number from 1 to 409.");
        }
        font.Add(new XElement(Ns + "sz", new XAttribute("val", size.ToString(CultureInfo.InvariantCulture))));

        if (spec.TryGetProperty("color", out var color))
            font.Add(new XElement(Ns + "color", new XAttribute("rgb", NormalizeColor(color, "font color"))));

        var name = _defaultFontName;
        if (spec.TryGetProperty("name", out var nameElement))
        {
            if (nameElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameElement.GetString()))
                throw new ArgumentException("Font 'name' must be a non-empty string.");
            name = nameElement.GetString()!.Trim();
            if (name.Length > 64) throw new ArgumentException("Font 'name' is longer than 64 characters.");
        }
        font.Add(new XElement(Ns + "name", new XAttribute("val", name)));
        font.Add(new XElement(Ns + "family", new XAttribute("val", 2)));

        return AppendUnique("fonts", font);
    }

    private int AddFill(JsonElement spec)
    {
        var color = spec.ValueKind switch
        {
            JsonValueKind.String => NormalizeColor(spec, "fill color"),
            JsonValueKind.Object when spec.TryGetProperty("color", out var nested) => NormalizeColor(nested, "fill color"),
            _ => throw new ArgumentException("'fill' must be an ARGB/RGB hex string or an object with a 'color'.")
        };

        var fill = new XElement(Ns + "fill",
            new XElement(Ns + "patternFill", new XAttribute("patternType", "solid"),
                new XElement(Ns + "fgColor", new XAttribute("rgb", color)),
                new XElement(Ns + "bgColor", new XAttribute("indexed", 64))));
        return AppendUnique("fills", fill);
    }

    private int AddBorder(JsonElement spec)
    {
        string? color = null;
        string top, bottom, left, right;

        if (spec.ValueKind == JsonValueKind.String)
        {
            var style = NormalizeBorderStyle(spec.GetString());
            top = bottom = left = right = style;
        }
        else if (spec.ValueKind == JsonValueKind.Object)
        {
            if (spec.TryGetProperty("color", out var colorElement)) color = NormalizeColor(colorElement, "border color");
            var shared = spec.TryGetProperty("style", out var sharedStyle) ? NormalizeBorderStyle(sharedStyle.GetString()) : "none";
            top = spec.TryGetProperty("top", out var topElement) ? NormalizeBorderStyle(topElement.GetString()) : shared;
            bottom = spec.TryGetProperty("bottom", out var bottomElement) ? NormalizeBorderStyle(bottomElement.GetString()) : shared;
            left = spec.TryGetProperty("left", out var leftElement) ? NormalizeBorderStyle(leftElement.GetString()) : shared;
            right = spec.TryGetProperty("right", out var rightElement) ? NormalizeBorderStyle(rightElement.GetString()) : shared;
        }
        else throw new ArgumentException("'border' must be a style name or an object.");

        var border = new XElement(Ns + "border",
            BorderSide("left", left, color),
            BorderSide("right", right, color),
            BorderSide("top", top, color),
            BorderSide("bottom", bottom, color),
            new XElement(Ns + "diagonal"));
        return AppendUnique("borders", border);
    }

    private static XElement BorderSide(string side, string style, string? color)
    {
        var element = new XElement(Ns + side);
        if (string.Equals(style, "none", StringComparison.OrdinalIgnoreCase)) return element;
        element.SetAttributeValue("style", style);
        if (color is not null) element.Add(new XElement(Ns + "color", new XAttribute("rgb", color)));
        return element;
    }

    private static string NormalizeBorderStyle(string? style)
    {
        var value = (style ?? string.Empty).Trim();
        if (value.Length == 0) return "none";
        if (!BorderStyles.Contains(value)) throw new ArgumentException($"Unsupported border style: {value}");
        return BorderStyles.First(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeColor(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.String) throw new ArgumentException($"{label} must be a hex string such as 'FF1F4E78'.");
        var value = (element.GetString() ?? string.Empty).Trim().TrimStart('#');
        if (value.Length == 6) value = "FF" + value;
        if (value.Length != 8 || !value.All(Uri.IsHexDigit)) throw new ArgumentException($"{label} must be RGB or ARGB hex, for example 'FF1F4E78'.");
        return value.ToUpperInvariant();
    }

    private int AppendUnique(string containerName, XElement candidate)
    {
        var container = Container(containerName);
        var serialized = candidate.ToString(SaveOptions.DisableFormatting);
        var index = 0;
        foreach (var existing in container.Elements())
        {
            if (existing.ToString(SaveOptions.DisableFormatting) == serialized) return index;
            index++;
        }

        container.Add(candidate);
        SetCount(container);
        return index;
    }

    private XElement Container(string name)
    {
        var root = _document.Root!;
        var existing = root.Element(Ns + name);
        if (existing is not null) return existing;

        var created = new XElement(Ns + name);
        var position = Array.IndexOf(StyleSheetOrder, name);
        XElement? following = null;
        for (var index = position + 1; index < StyleSheetOrder.Length && following is null; index++)
            following = root.Element(Ns + StyleSheetOrder[index]);

        if (following is null) root.Add(created); else following.AddBeforeSelf(created);
        return created;
    }

    private static void SetCount(XElement container) => container.SetAttributeValue("count", container.Elements().Count());
}
