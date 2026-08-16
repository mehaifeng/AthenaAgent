using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using static Athena.UI.Services.Presentations.PresentationSchema;

namespace Athena.UI.Services.Presentations;

public sealed partial class PptxPackageService
{
    public object Validate(string path)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var overflow = new List<PresentationTextLayoutAnalyzer.Finding>();
        var features = Array.Empty<string>();

        try
        {
            using var archive = OpenChecked(path, ZipArchiveMode.Read);
            features = DetectFeatures(archive);
            foreach (var required in new[] { ContentTypesPart, "_rels/.rels", PresentationPart })
                if (archive.GetEntry(required) is null) errors.Add($"Missing required OOXML part: {required}");
            if (errors.Count > 0) return ValidationReport(false, errors, warnings, overflow, features);

            var parsedParts = ParseAllXml(archive, errors);
            if (!parsedParts.TryGetValue(ContentTypesPart, out var contentTypes)
                || !parsedParts.TryGetValue(PresentationPart, out var presentation))
                return ValidationReport(false, errors, warnings, overflow, features);

            ValidateRelationships(archive, parsedParts, errors, warnings);
            ValidateRootRelationship(parsedParts, errors);
            ValidateContentTypes(archive, contentTypes, errors, warnings);
            ValidatePresentationOrder(presentation, errors);

            List<SlidePart> slides;
            try { slides = ReadSlides(archive, presentation); }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                return ValidationReport(false, errors, warnings, overflow, features);
            }

            var duplicateIds = slides.GroupBy(slide => slide.Id).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            if (duplicateIds.Length > 0) errors.Add("Duplicate p:sldId values: " + string.Join(", ", duplicateIds));
            var duplicateRids = slides.GroupBy(slide => slide.RelationshipId, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            if (duplicateRids.Length > 0) errors.Add("Duplicate slide relationship ids in p:sldIdLst: " + string.Join(", ", duplicateRids));
            var duplicateTargets = slides.GroupBy(slide => slide.PartPath, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
            if (duplicateTargets.Length > 0) errors.Add("More than one p:sldId points at the same slide part: " + string.Join(", ", duplicateTargets));
            if (slides.Count == 0) warnings.Add("The presentation contains no slides.");

            var listedParts = slides.Select(slide => slide.PartPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                                                                 && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                                                                 && !listedParts.Contains(entry.FullName)))
                warnings.Add($"Orphaned slide part is not listed in p:sldIdLst: {entry.FullName}");

            for (var index = 0; index < slides.Count; index++)
            {
                var slidePart = slides[index];
                if (!parsedParts.TryGetValue(slidePart.PartPath, out var slide))
                {
                    errors.Add($"Missing or unparsable slide part: {slidePart.PartPath}");
                    continue;
                }
                ValidateSlide(slide, slidePart, index + 1, archive, errors, warnings);
                try { overflow.AddRange(PresentationTextLayoutAnalyzer.Analyze(slide, index + 1)); }
                catch (Exception ex) { warnings.Add($"Text-fit analysis skipped for slide {index + 1}: {ex.Message}"); }
            }

            foreach (var chart in parsedParts.Where(pair => pair.Key.StartsWith("ppt/charts/", StringComparison.OrdinalIgnoreCase)
                                                             && pair.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                ValidateChart(chart.Key, chart.Value, errors);

            if (features.Length > 0)
                warnings.Add("Presentation contains features Athena preserves but does not fully edit: " + string.Join(", ", features) + ".");
            warnings.Add("Static validation cannot reproduce PowerPoint layout, animation, font substitution, chart rendering or media playback. Inspect every rendered slide before delivery.");
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        return ValidationReport(errors.Count == 0, errors, warnings, overflow, features);
    }

    private static Dictionary<string, XDocument> ParseAllXml(ZipArchive archive, List<string> errors)
    {
        var parsed = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                                                             || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            try { parsed[entry.FullName] = ReadXml(entry); }
            catch (Exception ex) { errors.Add($"Invalid XML in {entry.FullName}: {ex.Message}"); }
        }
        return parsed;
    }

    private static void ValidateRelationships(ZipArchive archive, IReadOnlyDictionary<string, XDocument> parsed,
        List<string> errors, List<string> warnings)
    {
        foreach (var pair in parsed.Where(pair => pair.Key.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            var sourcePart = SourcePartForRelationships(pair.Key);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relationship in pair.Value.Root?.Elements(PackageRelationshipNs + "Relationship") ?? [])
            {
                var id = (string?)relationship.Attribute("Id");
                var type = (string?)relationship.Attribute("Type");
                var target = (string?)relationship.Attribute("Target");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(target))
                {
                    errors.Add($"Relationship in {pair.Key} is missing Id, Type or Target.");
                    continue;
                }
                if (!seen.Add(id)) errors.Add($"Duplicate relationship id '{id}' in {pair.Key}.");
                if (string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"External relationship is not dereferenced: {pair.Key}#{id}");
                    continue;
                }
                try
                {
                    var resolved = ResolvePartPath(sourcePart, target);
                    if (archive.GetEntry(resolved) is null) errors.Add($"Broken relationship in {pair.Key}: {target} -> {resolved}");
                }
                catch (Exception ex) { errors.Add($"Unsafe relationship in {pair.Key}#{id}: {ex.Message}"); }
            }
        }
    }

    private static void ValidateRootRelationship(IReadOnlyDictionary<string, XDocument> parsed, List<string> errors)
    {
        if (!parsed.TryGetValue("_rels/.rels", out var rootRelationships)) return;
        var officeDocument = rootRelationships.Root?.Elements(PackageRelationshipNs + "Relationship")
            .Where(item => (string?)item.Attribute("Type") == OfficeDocumentRelationship).ToArray() ?? [];
        if (officeDocument.Length != 1)
        {
            errors.Add($"Package root must contain exactly one officeDocument relationship; found {officeDocument.Length}.");
            return;
        }
        var target = (string?)officeDocument[0].Attribute("Target");
        if (string.IsNullOrWhiteSpace(target) || !ResolvePartPath(string.Empty, target).Equals(PresentationPart, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Package officeDocument relationship must target {PresentationPart}.");
    }

    private static void ValidateContentTypes(ZipArchive archive, XDocument contentTypes, List<string> errors, List<string> warnings)
    {
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in contentTypes.Root?.Elements() ?? [])
        {
            if (item.Name == ContentTypesNs + "Default")
            {
                var extension = (string?)item.Attribute("Extension");
                var type = (string?)item.Attribute("ContentType");
                if (extension is null || type is null) errors.Add("A content-type Default is missing Extension or ContentType.");
                else if (!defaults.TryAdd(extension.TrimStart('.'), type)) errors.Add($"Duplicate content-type Default for extension '{extension}'.");
            }
            else if (item.Name == ContentTypesNs + "Override")
            {
                var part = ((string?)item.Attribute("PartName"))?.TrimStart('/');
                var type = (string?)item.Attribute("ContentType");
                if (part is null || type is null) errors.Add("A content-type Override is missing PartName or ContentType.");
                else if (!overrides.TryAdd(part, type)) errors.Add($"Duplicate content-type Override for '{part}'.");
                else if (archive.GetEntry(part) is null) errors.Add($"Content-type Override references a missing part: {part}");
            }
        }

        foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')
                                                             && entry.FullName != ContentTypesPart))
        {
            var extension = Path.GetExtension(entry.FullName).TrimStart('.');
            if (!overrides.ContainsKey(entry.FullName) && !defaults.ContainsKey(extension))
                errors.Add($"No content type is registered for part: {entry.FullName}");
        }
        foreach (var slide in archive.Entries.Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                                                              && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            if (!overrides.TryGetValue(slide.FullName, out var type) || type != SlideContentType)
                errors.Add($"Slide part has no correct slide content-type Override: {slide.FullName}");
        }
        if (!overrides.TryGetValue(PresentationPart, out var presentationType)
            || presentationType is not (PresentationContentType or MacroPresentationContentType or TemplateContentType or MacroTemplateContentType))
            warnings.Add("The main presentation content type is non-standard or missing.");
    }

    private static void ValidatePresentationOrder(XDocument presentation, List<string> errors)
    {
        var root = presentation.Root;
        if (root?.Name != P + "presentation")
        {
            errors.Add("ppt/presentation.xml root must be p:presentation.");
            return;
        }
        CheckRelativeOrder(root, errors, "ppt/presentation.xml", ["sldIdLst", "sldSz", "notesSz", "defaultTextStyle", "extLst"]);
    }

    private static void ValidateSlide(XDocument slide, SlidePart slidePart, int number, ZipArchive archive,
        List<string> errors, List<string> warnings)
    {
        if (slide.Root?.Name != P + "sld")
        {
            errors.Add($"{slidePart.PartPath} root must be p:sld.");
            return;
        }
        CheckRelativeOrder(slide.Root, errors, slidePart.PartPath, ["cSld", "clrMapOvr", "transition", "timing", "extLst"]);
        var tree = slide.Root.Element(P + "cSld")?.Element(P + "spTree");
        if (tree is null)
        {
            errors.Add($"{slidePart.PartPath} has no p:cSld/p:spTree.");
            return;
        }
        if (tree.Element(P + "nvGrpSpPr") is null || tree.Element(P + "grpSpPr") is null)
            errors.Add($"{slidePart.PartPath} spTree is missing nvGrpSpPr or grpSpPr.");

        var ids = new Dictionary<int, string>();
        foreach (var properties in tree.Descendants(P + "cNvPr"))
        {
            if (!int.TryParse((string?)properties.Attribute("id"), out var id) || id < 1)
            {
                errors.Add($"{slidePart.PartPath} contains a shape without a positive cNvPr id.");
                continue;
            }
            var name = (string?)properties.Attribute("name") ?? "unnamed";
            if (!ids.TryAdd(id, name)) errors.Add($"{slidePart.PartPath} has duplicate shape id {id} ('{ids[id]}' and '{name}').");
        }

        var layoutRelationships = ReadRelationships(archive, slidePart.PartPath)
            .Where(relationship => relationship.Type == SlideLayoutRelationship && !relationship.IsExternal).ToArray();
        if (layoutRelationships.Length != 1)
            errors.Add($"{slidePart.PartPath} must have exactly one slideLayout relationship; found {layoutRelationships.Length}.");
        if (tree.Descendants(P + "sp").Count() + tree.Descendants(P + "pic").Count()
            + tree.Descendants(P + "graphicFrame").Count() > MaxElementsPerSlide)
            warnings.Add($"Slide {number} contains more than {MaxElementsPerSlide} elements; editing is intentionally bounded.");
    }

    private static void ValidateChart(string partPath, XDocument chart, List<string> errors)
    {
        var axisDefinitions = chart.Descendants().Where(element => element.Name is var name
                && (name == C + "catAx" || name == C + "valAx" || name == C + "dateAx" || name == C + "serAx"))
            .Select(element => (string?)element.Element(C + "axId")?.Attribute("val"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        foreach (var duplicate in axisDefinitions.GroupBy(value => value, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add($"{partPath} defines axis id {duplicate.Key} more than once.");

        var defined = axisDefinitions.ToHashSet(StringComparer.Ordinal);
        foreach (var plot in chart.Descendants().Where(element => element.Name.LocalName.EndsWith("Chart", StringComparison.Ordinal)
                                                                  && element.Name.Namespace == C))
        {
            foreach (var axis in plot.Elements(C + "axId"))
            {
                var value = (string?)axis.Attribute("val");
                if (!string.IsNullOrWhiteSpace(value) && !defined.Contains(value))
                    errors.Add($"{partPath} plot references undeclared axis id {value}.");
            }
        }

        foreach (var barChart in chart.Descendants(C + "barChart"))
        {
            var grouping = (string?)barChart.Element(C + "grouping")?.Attribute("val");
            if (grouping is not ("stacked" or "percentStacked")) continue;
            if (barChart.Descendants(C + "dLblPos").Any(label => (string?)label.Attribute("val") == "outEnd"))
                errors.Add($"{partPath} uses dLblPos=outEnd on a stacked bar/column chart; PowerPoint can reject this chart.");
        }
    }

    private static void CheckRelativeOrder(XElement parent, List<string> errors, string part, IReadOnlyList<string> order)
    {
        var positions = order.Select((name, index) => (name, index)).ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
        var highest = -1;
        foreach (var child in parent.Elements().Where(child => child.Name.Namespace == P && positions.ContainsKey(child.Name.LocalName)))
        {
            var position = positions[child.Name.LocalName];
            if (position < highest)
            {
                errors.Add($"{part}: p:{child.Name.LocalName} is out of core PresentationML child order.");
                return;
            }
            highest = position;
        }
    }

    private static object ValidationReport(bool valid, List<string> errors, List<string> warnings,
        IReadOnlyList<PresentationTextLayoutAnalyzer.Finding> overflow, IReadOnlyList<string> features) => new
        {
            valid,
            errors,
            warnings,
            overflowWarnings = overflow.Select(item => new
            {
                slide = item.Slide,
                shapeId = item.ShapeId,
                shapeName = item.ShapeName,
                estimatedHeightPoints = item.EstimatedHeightPoints,
                availableHeightPoints = item.AvailableHeightPoints,
                confidence = item.Confidence,
                message = item.Message
            }).ToArray(),
            features,
            dynamicValidationPerformed = false,
            visualValidationRequired = true
        };
}
