using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Athena.UI.Services.Ooxml;
using static Athena.UI.Services.Presentations.PresentationSchema;

namespace Athena.UI.Services.Presentations;

/// <summary>
/// Dependency-free PresentationML operations for Athena's native pptx skill. The service owns the
/// PresentationML vocabulary while <see cref="OoxmlPackageService"/> owns hostile-package guards,
/// relationship path normalization and atomic writes.
/// </summary>
public sealed partial class PptxPackageService : OoxmlPackageService
{
    private const string PresentationPart = "ppt/presentation.xml";
    private const string ContentTypesPart = "[Content_Types].xml";
    private const int MaxSlides = 500;
    private const int MaxElementsPerSlide = 2_000;
    private const int TextPreviewLimit = 4_000;

    internal sealed record RelationshipRecord(string Id, string Type, string Target, string? TargetMode, string ResolvedTarget)
    {
        public bool IsExternal => TargetMode?.Equals("External", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal sealed record SlidePart(uint Id, string RelationshipId, string PartPath, XElement SlideIdElement);

    private sealed record ShapeInfo(int Id, string Name, string Kind, string? PlaceholderType, uint? PlaceholderIndex,
        long? X, long? Y, long? Width, long? Height, string Text, bool IsGrouped);

    /// <summary>Returns an addressable, bounded view of a presentation without rendering it.</summary>
    public object Inspect(string path, int startSlide, int maxSlides, bool includeShapes, bool includeNotes)
    {
        startSlide = Math.Max(1, startSlide);
        maxSlides = Math.Clamp(maxSlides, 1, 200);

        using var archive = OpenChecked(path, ZipArchiveMode.Read);
        var presentation = ReadXml(RequiredEntry(archive, PresentationPart));
        var slides = ReadSlides(archive, presentation);
        var (width, height) = ReadSlideSize(presentation);
        var layouts = ReadLayouts(archive);
        var endExclusive = Math.Min(slides.Count, startSlide - 1 + maxSlides);
        var inspectedSlides = new List<object>();

        for (var index = startSlide - 1; index < endExclusive; index++)
        {
            var slidePart = slides[index];
            var slide = ReadXml(RequiredEntry(archive, slidePart.PartPath));
            var shapeInfos = ReadShapes(slide).ToList();
            var text = shapeInfos.Where(shape => shape.Text.Length > 0).Select(shape => shape.Text).ToArray();
            var titleShape = shapeInfos.FirstOrDefault(shape =>
                    shape.PlaceholderType is "title" or "ctrTitle" && shape.Text.Length > 0)
                ?? shapeInfos.FirstOrDefault(shape =>
                    shape.Name.Contains("title", StringComparison.OrdinalIgnoreCase) && shape.Text.Length > 0)
                ?? shapeInfos.FirstOrDefault(shape => shape.Kind == "text" && shape.Text.Length > 0);

            var slideRelationships = ReadRelationships(archive, slidePart.PartPath);
            var layoutTarget = slideRelationships.FirstOrDefault(relationship => relationship.Type == SlideLayoutRelationship)?.ResolvedTarget;
            var layoutName = layoutTarget is null
                ? null
                : layouts.FirstOrDefault(layout => string.Equals(layout.PartPath, layoutTarget, StringComparison.OrdinalIgnoreCase))?.Name;
            var notes = includeNotes ? ReadNotes(archive, slideRelationships) : null;

            inspectedSlides.Add(new
            {
                number = index + 1,
                slideId = slidePart.Id,
                relationshipId = slidePart.RelationshipId,
                part = slidePart.PartPath,
                name = (string?)slide.Root?.Element(P + "cSld")?.Attribute("name"),
                layout = layoutName,
                layoutPart = layoutTarget,
                title = titleShape is null ? null : Truncate(titleShape.Text, 500),
                text = text.Select(item => Truncate(item, TextPreviewLimit)).ToArray(),
                notes,
                shapeCount = shapeInfos.Count,
                pictureCount = shapeInfos.Count(shape => shape.Kind == "picture"),
                tableCount = shapeInfos.Count(shape => shape.Kind == "table"),
                chartCount = shapeInfos.Count(shape => shape.Kind == "chart"),
                groupedShapeCount = shapeInfos.Count(shape => shape.Kind == "group"),
                shapes = includeShapes ? shapeInfos.Select(ToShapeResult).ToArray() : null
            });
        }

        return new
        {
            path,
            slideCount = slides.Count,
            slideSize = new
            {
                widthEmu = width,
                heightEmu = height,
                widthInches = Math.Round(width / (double)EmusPerInch, 3),
                heightInches = Math.Round(height / (double)EmusPerInch, 3),
                aspectRatio = height == 0 ? (double?)null : Math.Round(width / (double)height, 4)
            },
            window = new { startSlide, endSlide = endExclusive },
            hasMoreSlides = endExclusive < slides.Count,
            nextStartSlide = endExclusive < slides.Count ? endExclusive + 1 : (int?)null,
            layouts = layouts.Select(layout => new { layout.Name, part = layout.PartPath, layout.Type, layout.MasterPart }).ToArray(),
            features = DetectFeatures(archive),
            slides = inspectedSlides,
            pagingHint = "Slide numbers are 1-based and follow p:sldIdLst order. Shape ids and names are the stable targets accepted by edit_presentation."
        };
    }

    private static object ToShapeResult(ShapeInfo shape) => new
    {
        id = shape.Id,
        name = shape.Name,
        kind = shape.Kind,
        placeholderType = shape.PlaceholderType,
        placeholderIndex = shape.PlaceholderIndex,
        bounds = shape.X is null ? null : new
        {
            xEmu = shape.X,
            yEmu = shape.Y,
            widthEmu = shape.Width,
            heightEmu = shape.Height,
            x = Math.Round(shape.X.Value / (double)EmusPerInch, 3),
            y = Math.Round(shape.Y!.Value / (double)EmusPerInch, 3),
            width = Math.Round(shape.Width!.Value / (double)EmusPerInch, 3),
            height = Math.Round(shape.Height!.Value / (double)EmusPerInch, 3)
        },
        text = shape.Text.Length == 0 ? null : Truncate(shape.Text, TextPreviewLimit),
        groupedCoordinates = shape.IsGrouped ? true : (bool?)null
    };

    private static IEnumerable<ShapeInfo> ReadShapes(XDocument slide)
    {
        var shapeNames = new HashSet<XName> { P + "sp", P + "pic", P + "graphicFrame", P + "grpSp", P + "cxnSp" };
        var tree = slide.Root?.Element(P + "cSld")?.Element(P + "spTree");
        if (tree is null) yield break;

        foreach (var shape in tree.DescendantsAndSelf().Where(element => shapeNames.Contains(element.Name)))
        {
            var nonVisual = shape.Name switch
            {
                var name when name == P + "sp" => shape.Element(P + "nvSpPr")?.Element(P + "cNvPr"),
                var name when name == P + "pic" => shape.Element(P + "nvPicPr")?.Element(P + "cNvPr"),
                var name when name == P + "graphicFrame" => shape.Element(P + "nvGraphicFramePr")?.Element(P + "cNvPr"),
                var name when name == P + "grpSp" => shape.Element(P + "nvGrpSpPr")?.Element(P + "cNvPr"),
                _ => shape.Element(P + "nvCxnSpPr")?.Element(P + "cNvPr")
            };
            var id = int.TryParse((string?)nonVisual?.Attribute("id"), out var parsedId) ? parsedId : 0;
            var nameValue = (string?)nonVisual?.Attribute("name") ?? $"Shape {id}";
            var placeholder = shape.Descendants(P + "ph").FirstOrDefault();
            var placeholderIndex = uint.TryParse((string?)placeholder?.Attribute("idx"), out var parsedIndex) ? parsedIndex : (uint?)null;
            var textBody = shape.Element(P + "txBody");
            var kind = ShapeKind(shape, textBody is not null);
            var (x, y, width, height) = ReadBounds(shape);
            var grouped = shape.Ancestors(P + "grpSp").Any();
            var text = textBody is null ? string.Empty : DrawingTextEditor.TextBodyText(textBody);
            yield return new ShapeInfo(id, nameValue, kind, (string?)placeholder?.Attribute("type"), placeholderIndex,
                x, y, width, height, text, grouped);
        }
    }

    private static string ShapeKind(XElement shape, bool hasText)
    {
        if (shape.Name == P + "pic") return "picture";
        if (shape.Name == P + "grpSp") return "group";
        if (shape.Name == P + "cxnSp") return "connector";
        if (shape.Name == P + "graphicFrame")
        {
            if (shape.Descendants(A + "tbl").Any()) return "table";
            if (shape.Descendants(C + "chart").Any()) return "chart";
            if (shape.Descendants().Any(element => element.Name.NamespaceName.Contains("diagram", StringComparison.OrdinalIgnoreCase))) return "smartArt";
            return "graphicFrame";
        }
        var geometry = (string?)shape.Descendants(A + "prstGeom").FirstOrDefault()?.Attribute("prst");
        return hasText && (geometry is null or "rect") ? "text" : geometry ?? "shape";
    }

    internal static (long? X, long? Y, long? Width, long? Height) ReadBounds(XElement shape)
    {
        XElement? transform = shape.Name switch
        {
            var name when name == P + "sp" || name == P + "pic" || name == P + "cxnSp" => shape.Element(P + "spPr")?.Element(A + "xfrm"),
            var name when name == P + "graphicFrame" => shape.Element(P + "xfrm"),
            var name when name == P + "grpSp" => shape.Element(P + "grpSpPr")?.Element(A + "xfrm"),
            _ => null
        };
        var offset = transform?.Element(A + "off");
        var extent = transform?.Element(A + "ext");
        if (!long.TryParse((string?)offset?.Attribute("x"), out var x)
            || !long.TryParse((string?)offset?.Attribute("y"), out var y)
            || !long.TryParse((string?)extent?.Attribute("cx"), out var width)
            || !long.TryParse((string?)extent?.Attribute("cy"), out var height))
            return (null, null, null, null);
        return (x, y, width, height);
    }

    private static string[]? ReadNotes(ZipArchive archive, IReadOnlyList<RelationshipRecord> slideRelationships)
    {
        var notesPart = slideRelationships.FirstOrDefault(relationship => relationship.Type == NotesSlideRelationship && !relationship.IsExternal)?.ResolvedTarget;
        if (notesPart is null || archive.GetEntry(notesPart) is not { } entry) return null;
        var notes = ReadXml(entry);
        var bodyShapes = notes.Descendants(P + "sp").Where(shape =>
        {
            var placeholderType = (string?)shape.Descendants(P + "ph").FirstOrDefault()?.Attribute("type");
            return placeholderType is null or "body";
        });
        var paragraphs = bodyShapes.SelectMany(shape => shape.Descendants(A + "p"))
            .Select(DrawingTextEditor.ParagraphText)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => Truncate(text, TextPreviewLimit))
            .ToArray();
        return paragraphs.Length == 0 ? null : paragraphs;
    }

    private sealed record LayoutInfo(string Name, string PartPath, string? Type, string? MasterPart);

    private static List<LayoutInfo> ReadLayouts(ZipArchive archive)
    {
        var layouts = new List<LayoutInfo>();
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("ppt/slideLayouts/", StringComparison.OrdinalIgnoreCase)
                                                             && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            var layout = ReadXml(entry);
            var master = ReadRelationships(archive, entry.FullName)
                .FirstOrDefault(relationship => relationship.Type == SlideMasterRelationship && !relationship.IsExternal)?.ResolvedTarget;
            layouts.Add(new LayoutInfo(
                (string?)layout.Root?.Element(P + "cSld")?.Attribute("name") ?? Path.GetFileNameWithoutExtension(entry.FullName),
                entry.FullName,
                (string?)layout.Root?.Attribute("type"),
                master));
        }
        return layouts.OrderBy(layout => layout.PartPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static List<SlidePart> ReadSlides(ZipArchive archive, XDocument presentation)
    {
        var relationships = ReadRelationships(archive, PresentationPart)
            .Where(relationship => !relationship.IsExternal)
            .ToDictionary(relationship => relationship.Id, StringComparer.Ordinal);
        var list = presentation.Root?.Element(P + "sldIdLst");
        if (list is null) return [];
        var slides = new List<SlidePart>();
        foreach (var slideId in list.Elements(P + "sldId"))
        {
            if (!uint.TryParse((string?)slideId.Attribute("id"), out var id))
                throw new InvalidDataException("A p:sldId has no valid numeric id.");
            var relationshipId = (string?)slideId.Attribute(R + "id")
                ?? throw new InvalidDataException("A p:sldId has no r:id.");
            if (!relationships.TryGetValue(relationshipId, out var relationship) || relationship.Type != SlideRelationship)
                throw new InvalidDataException($"Slide relationship '{relationshipId}' is missing or is not a slide relationship.");
            slides.Add(new SlidePart(id, relationshipId, relationship.ResolvedTarget, slideId));
        }
        return slides;
    }

    internal static List<RelationshipRecord> ReadRelationships(ZipArchive archive, string partPath)
    {
        var entry = archive.GetEntry(RelationshipsPathFor(partPath));
        if (entry is null) return [];
        var document = ReadXml(entry);
        var records = new List<RelationshipRecord>();
        foreach (var relationship in document.Root?.Elements(PackageRelationshipNs + "Relationship") ?? [])
        {
            var id = (string?)relationship.Attribute("Id");
            var type = (string?)relationship.Attribute("Type");
            var target = (string?)relationship.Attribute("Target");
            var targetMode = (string?)relationship.Attribute("TargetMode");
            if (id is null || type is null || target is null) continue;
            var resolved = targetMode?.Equals("External", StringComparison.OrdinalIgnoreCase) == true
                ? target
                : ResolvePartPath(partPath, target);
            records.Add(new RelationshipRecord(id, type, target, targetMode, resolved));
        }
        return records;
    }

    internal static (long Width, long Height) ReadSlideSize(XDocument presentation)
    {
        var size = presentation.Root?.Element(P + "sldSz");
        if (!long.TryParse((string?)size?.Attribute("cx"), out var width) || width <= 0) width = 12_192_000;
        if (!long.TryParse((string?)size?.Attribute("cy"), out var height) || height <= 0) height = 6_858_000;
        return (width, height);
    }

    internal static ZipArchive OpenChecked(string path, ZipArchiveMode mode)
    {
        EnsurePresentationExtension(path);
        return OpenPackage(path, mode);
    }

    internal static void EnsurePresentationExtension(string path)
    {
        var extension = Path.GetExtension(path);
        if (!new[] { ".pptx", ".pptm", ".potx", ".potm" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Only .pptx, .pptm, .potx and .potm OOXML presentations are supported. Convert legacy .ppt first.");
    }

    internal static void EnsureDistinctPresentationPaths(string inputPath, string outputPath)
    {
        EnsurePresentationExtension(inputPath);
        EnsurePresentationExtension(outputPath);
        EnsureDistinctPaths(inputPath, outputPath);
    }

    internal static string[] DetectFeatures(ZipArchive archive)
    {
        var features = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in archive.Entries.Select(entry => entry.FullName))
        {
            if (name.Equals("ppt/vbaProject.bin", StringComparison.OrdinalIgnoreCase)) features.Add("macros");
            else if (name.StartsWith("ppt/charts/", StringComparison.OrdinalIgnoreCase)) features.Add("charts");
            else if (name.StartsWith("ppt/diagrams/", StringComparison.OrdinalIgnoreCase)) features.Add("SmartArt");
            else if (name.StartsWith("ppt/embeddings/", StringComparison.OrdinalIgnoreCase)) features.Add("embedded objects/workbooks");
            else if (name.StartsWith("ppt/media/", StringComparison.OrdinalIgnoreCase)) features.Add("media/images");
            else if (name.StartsWith("ppt/notesSlides/", StringComparison.OrdinalIgnoreCase)) features.Add("speaker notes");
            else if (name.Contains("comment", StringComparison.OrdinalIgnoreCase)) features.Add("comments");
            else if (name.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)) features.Add("digital signatures");
        }
        return features.ToArray();
    }

    internal static string Truncate(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
}
