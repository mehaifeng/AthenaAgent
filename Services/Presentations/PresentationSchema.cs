using System;
using System.Globalization;
using System.Xml.Linq;

namespace Athena.UI.Services.Presentations;

internal static class PresentationSchema
{
    public static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    public static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    public const long EmusPerInch = 914_400;
    public const long EmusPerPoint = 12_700;

    public const string OfficeDocumentRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    public const string SlideRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide";
    public const string SlideLayoutRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout";
    public const string SlideMasterRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster";
    public const string ThemeRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme";
    public const string ImageRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
    public const string NotesSlideRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide";
    public const string NotesMasterRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesMaster";
    public const string ChartRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart";

    public const string PresentationContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
    public const string MacroPresentationContentType = "application/vnd.ms-powerpoint.presentation.macroEnabled.main+xml";
    public const string TemplateContentType = "application/vnd.openxmlformats-officedocument.presentationml.template.main+xml";
    public const string MacroTemplateContentType = "application/vnd.ms-powerpoint.template.macroEnabled.main+xml";
    public const string SlideContentType = "application/vnd.openxmlformats-officedocument.presentationml.slide+xml";
    public const string SlideLayoutContentType = "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml";
    public const string SlideMasterContentType = "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml";
    public const string ThemeContentType = "application/vnd.openxmlformats-officedocument.theme+xml";
    public const string NotesSlideContentType = "application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml";

    public static long InchesToEmus(double inches, string property)
    {
        if (!double.IsFinite(inches) || inches is < -100 or > 100)
            throw new ArgumentException($"'{property}' must be a finite number between -100 and 100 inches.");
        return checked((long)Math.Round(inches * EmusPerInch, MidpointRounding.AwayFromZero));
    }

    public static int PointsToHundredths(double points, string property, double minimum = 1, double maximum = 400)
    {
        if (!double.IsFinite(points) || points < minimum || points > maximum)
            throw new ArgumentException($"'{property}' must be between {minimum.ToString(CultureInfo.InvariantCulture)} and {maximum.ToString(CultureInfo.InvariantCulture)} points.");
        return checked((int)Math.Round(points * 100, MidpointRounding.AwayFromZero));
    }

    public static string NormalizeColor(string? value, string fallback)
    {
        var color = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (color.StartsWith('#') || (color.Length != 6 && color.Length != 8))
            throw new ArgumentException($"Invalid colour '{color}'. Use 6 or 8 hexadecimal digits without '#'.");
        foreach (var character in color)
        {
            if (!Uri.IsHexDigit(character))
                throw new ArgumentException($"Invalid colour '{color}'. Use hexadecimal digits only.");
        }
        return color.Length == 8 ? color[2..].ToUpperInvariant() : color.ToUpperInvariant();
    }

    public static string Serialize(XElement root) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + root.ToString(SaveOptions.DisableFormatting);
}
