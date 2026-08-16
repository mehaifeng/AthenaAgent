using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using static Athena.UI.Services.Presentations.PresentationSchema;

namespace Athena.UI.Services.Presentations;

public sealed partial class PptxPackageService
{
    private const int MaxEditOperations = 2_000;

    public object Edit(string inputPath, string outputPath, string operationsJson, bool overwrite)
    {
        EnsureDistinctPresentationPaths(inputPath, outputPath);
        EnsureCanWrite(outputPath, overwrite);
        using var json = JsonDocument.Parse(operationsJson, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip });
        var operationsElement = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement
            : json.RootElement.TryGetProperty("operations", out var nested) ? nested : default;
        if (operationsElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("operationsJson must be an array or an object containing an 'operations' array.");
        var operations = operationsElement.EnumerateArray().ToList();
        if (operations.Count is < 1 or > MaxEditOperations)
            throw new ArgumentException($"Provide 1-{MaxEditOperations} operations per call.");

        using (var checkedArchive = OpenChecked(inputPath, ZipArchiveMode.Read))
        {
            var presentation = ReadXml(RequiredEntry(checkedArchive, PresentationPart));
            _ = ReadSlides(checkedArchive, presentation);
        }

        var summary = new EditSummary();
        AtomicWrite(outputPath, overwrite, temporaryPath =>
        {
            File.Copy(inputPath, temporaryPath, overwrite: true);
            using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false, Encoding.UTF8);
            var presentation = ReadXml(RequiredEntry(archive, PresentationPart), preserveWhitespace: true);
            var relationships = ReadXml(RequiredEntry(archive, RelationshipsPathFor(PresentationPart)), preserveWhitespace: true);
            var contentTypes = ReadXml(RequiredEntry(archive, ContentTypesPart), preserveWhitespace: true);
            var contentEditStarted = false;

            foreach (var operation in operations)
            {
                if (operation.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each presentation edit operation must be an object.");

                if (operation.TryGetProperty("duplicateSlide", out var duplicate))
                {
                    GuardStructureOrder(contentEditStarted);
                    DuplicateSlide(archive, presentation, relationships, contentTypes, ReadSlideNumber(duplicate, "duplicateSlide"),
                        operation.TryGetProperty("after", out var after) ? ReadSlideNumber(after, "after", allowZero: true) : null, summary);
                    continue;
                }
                if (operation.TryGetProperty("deleteSlide", out var delete))
                {
                    GuardStructureOrder(contentEditStarted);
                    DeleteSlide(archive, presentation, relationships, contentTypes, ReadSlideNumber(delete, "deleteSlide"), summary);
                    continue;
                }
                if (operation.TryGetProperty("moveSlide", out var move))
                {
                    GuardStructureOrder(contentEditStarted);
                    var destination = operation.TryGetProperty("to", out var to) ? ReadSlideNumber(to, "to") : throw new ArgumentException("moveSlide requires 'to'.");
                    MoveSlide(presentation, ReadSlideNumber(move, "moveSlide"), destination, summary);
                    continue;
                }
                if (operation.TryGetProperty("reorder", out var reorder))
                {
                    GuardStructureOrder(contentEditStarted);
                    ReorderSlides(presentation, reorder, summary);
                    continue;
                }

                contentEditStarted = true;
                if (operation.TryGetProperty("find", out var find))
                {
                    ApplyFindReplace(archive, presentation, relationships, operation, find, summary);
                    continue;
                }
                ApplySlideContentEdit(archive, presentation, relationships, operation, summary);
            }

            ReplaceXmlEntry(archive, PresentationPart, presentation);
            ReplaceXmlEntry(archive, RelationshipsPathFor(PresentationPart), relationships);
            ReplaceXmlEntry(archive, ContentTypesPart, contentTypes);
        });

        return new
        {
            inputPath,
            outputPath,
            slidesDuplicated = summary.Duplicated,
            slidesDeleted = summary.Deleted,
            slideMoves = summary.Moved,
            textReplacements = summary.Replacements,
            shapesRewritten = summary.ShapesRewritten,
            textBoxesAdded = summary.TextBoxesAdded,
            sharedComplexParts = summary.SharedComplexParts,
            warnings = summary.Warnings,
            nextStep = "Run validate_presentation and inspect_presentation on the output, then render and inspect every slide. The source presentation was left unchanged."
        };
    }

    private static void DuplicateSlide(ZipArchive archive, XDocument presentation, XDocument relationships, XDocument contentTypes,
        int sourceNumber, int? afterNumber, EditSummary summary)
    {
        var slides = ReadCurrentSlides(presentation, relationships);
        var source = ResolveSlide(slides, sourceNumber);
        var insertAfter = afterNumber ?? sourceNumber;
        if (insertAfter < 0 || insertAfter > slides.Count) throw new ArgumentException($"'after' must be from 0 to {slides.Count}.");
        if (slides.Count >= MaxSlides) throw new InvalidOperationException($"A presentation may contain at most {MaxSlides} slides.");

        var newPart = NextSlidePartPath(archive);
        CopyArchiveEntry(archive, source.PartPath, newPart);
        var sourceRels = RelationshipsPathFor(source.PartPath);
        if (archive.GetEntry(sourceRels) is not null)
        {
            var newRels = RelationshipsPathFor(newPart);
            CopyArchiveEntry(archive, sourceRels, newRels);
            CloneNotesForDuplicatedSlide(archive, source, newPart, newRels, contentTypes);
        }

        var newRelationshipId = NextRelationshipId(relationships);
        relationships.Root!.Add(new XElement(PackageRelationshipNs + "Relationship",
            new XAttribute("Id", newRelationshipId), new XAttribute("Type", SlideRelationship),
            new XAttribute("Target", newPart["ppt/".Length..])));
        var newSlideId = Math.Max(256u, slides.Max(slide => slide.Id) + 1);
        var newElement = new XElement(P + "sldId", new XAttribute("id", newSlideId), new XAttribute(R + "id", newRelationshipId));
        var list = presentation.Root!.Element(P + "sldIdLst")!;
        if (insertAfter == 0) list.AddFirst(newElement);
        else list.Elements(P + "sldId").ElementAt(insertAfter - 1).AddAfterSelf(newElement);
        AddContentTypeOverride(contentTypes, newPart, SlideContentType);

        summary.Duplicated++;
        var complex = ReadRelationships(archive, source.PartPath).Where(relationship => relationship.Type is not SlideLayoutRelationship
            && relationship.Type is not ImageRelationship && relationship.Type is not NotesSlideRelationship).Select(relationship => relationship.Type.Split('/').Last()).Distinct().ToArray();
        if (complex.Length > 0)
        {
            summary.SharedComplexParts.Add(new { sourceSlide = sourceNumber, duplicatedPart = newPart, relationships = complex });
            summary.Warnings.Add($"Duplicated slide {sourceNumber} shares complex related parts ({string.Join(", ", complex)}) with its source.");
        }
    }

    private static void DeleteSlide(ZipArchive archive, XDocument presentation, XDocument relationships, XDocument contentTypes,
        int slideNumber, EditSummary summary)
    {
        var slides = ReadCurrentSlides(presentation, relationships);
        if (slides.Count <= 1) throw new InvalidOperationException("Deleting the last slide is not allowed.");
        var slide = ResolveSlide(slides, slideNumber);
        RemoveUnsharedNotesForDeletedSlide(archive, slides, slide, contentTypes);
        slide.SlideIdElement.Remove();
        relationships.Root?.Elements(PackageRelationshipNs + "Relationship")
            .FirstOrDefault(relationship => (string?)relationship.Attribute("Id") == slide.RelationshipId)?.Remove();
        archive.GetEntry(RelationshipsPathFor(slide.PartPath))?.Delete();
        archive.GetEntry(slide.PartPath)?.Delete();
        contentTypes.Root?.Elements(ContentTypesNs + "Override")
            .FirstOrDefault(item => ((string?)item.Attribute("PartName"))?.TrimStart('/').Equals(slide.PartPath, StringComparison.OrdinalIgnoreCase) == true)?.Remove();
        summary.Deleted++;
        summary.Warnings.Add($"Deleted slide {slideNumber}; related media/chart parts were retained because another slide or extension may still reference them.");
    }

    private static void CloneNotesForDuplicatedSlide(ZipArchive archive, SlidePart source, string newSlidePart,
        string newSlideRelationshipsPart, XDocument contentTypes)
    {
        var relationships = ReadXml(RequiredEntry(archive, newSlideRelationshipsPart), preserveWhitespace: true);
        var notesRelationship = relationships.Root?.Elements(PackageRelationshipNs + "Relationship")
            .FirstOrDefault(item => (string?)item.Attribute("Type") == NotesSlideRelationship);
        var target = (string?)notesRelationship?.Attribute("Target");
        if (target is null) return;
        var sourceNotesPart = ResolvePartPath(source.PartPath, target);
        if (archive.GetEntry(sourceNotesPart) is null)
        {
            notesRelationship!.Remove();
            ReplaceXmlEntry(archive, newSlideRelationshipsPart, relationships);
            return;
        }

        var newNotesPart = NextNotesPartPath(archive);
        CopyArchiveEntry(archive, sourceNotesPart, newNotesPart);
        var sourceNotesRels = RelationshipsPathFor(sourceNotesPart);
        if (archive.GetEntry(sourceNotesRels) is not null)
        {
            var newNotesRels = RelationshipsPathFor(newNotesPart);
            CopyArchiveEntry(archive, sourceNotesRels, newNotesRels);
            var notesRelationships = ReadXml(RequiredEntry(archive, newNotesRels), preserveWhitespace: true);
            var backReference = notesRelationships.Root?.Elements(PackageRelationshipNs + "Relationship")
                .FirstOrDefault(item => (string?)item.Attribute("Type") == SlideRelationship);
            backReference?.SetAttributeValue("Target", "../slides/" + Path.GetFileName(newSlidePart));
            ReplaceXmlEntry(archive, newNotesRels, notesRelationships);
        }
        notesRelationship!.SetAttributeValue("Target", "../notesSlides/" + Path.GetFileName(newNotesPart));
        ReplaceXmlEntry(archive, newSlideRelationshipsPart, relationships);
        AddContentTypeOverride(contentTypes, newNotesPart, NotesSlideContentType);
    }

    private static void RemoveUnsharedNotesForDeletedSlide(ZipArchive archive, IReadOnlyList<SlidePart> slides,
        SlidePart deleting, XDocument contentTypes)
    {
        var notesPart = ReadRelationships(archive, deleting.PartPath)
            .FirstOrDefault(relationship => relationship.Type == NotesSlideRelationship && !relationship.IsExternal)?.ResolvedTarget;
        if (notesPart is null) return;
        var shared = slides.Where(slide => slide != deleting).Any(slide => ReadRelationships(archive, slide.PartPath)
            .Any(relationship => relationship.Type == NotesSlideRelationship && !relationship.IsExternal
                                 && relationship.ResolvedTarget.Equals(notesPart, StringComparison.OrdinalIgnoreCase)));
        if (shared) return;
        archive.GetEntry(RelationshipsPathFor(notesPart))?.Delete();
        archive.GetEntry(notesPart)?.Delete();
        contentTypes.Root?.Elements(ContentTypesNs + "Override")
            .FirstOrDefault(item => ((string?)item.Attribute("PartName"))?.TrimStart('/').Equals(notesPart, StringComparison.OrdinalIgnoreCase) == true)?.Remove();
    }

    private static void MoveSlide(XDocument presentation, int sourceNumber, int destinationNumber, EditSummary summary)
    {
        var list = presentation.Root?.Element(P + "sldIdLst") ?? throw new InvalidDataException("Presentation has no slide list.");
        var elements = list.Elements(P + "sldId").ToList();
        if (sourceNumber < 1 || sourceNumber > elements.Count || destinationNumber < 1 || destinationNumber > elements.Count)
            throw new ArgumentException($"moveSlide and to must be from 1 to {elements.Count}.");
        var moved = elements[sourceNumber - 1];
        elements.RemoveAt(sourceNumber - 1);
        elements.Insert(destinationNumber - 1, moved);
        list.RemoveNodes();
        list.Add(elements);
        summary.Moved++;
    }

    private static void ReorderSlides(XDocument presentation, JsonElement reorder, EditSummary summary)
    {
        if (reorder.ValueKind != JsonValueKind.Array) throw new ArgumentException("'reorder' must be an array of slide numbers.");
        var list = presentation.Root?.Element(P + "sldIdLst") ?? throw new InvalidDataException("Presentation has no slide list.");
        var elements = list.Elements(P + "sldId").ToList();
        var order = reorder.EnumerateArray().Select(item => ReadSlideNumber(item, "reorder item")).ToList();
        if (order.Count != elements.Count || order.Distinct().Count() != elements.Count || order.Any(item => item > elements.Count))
            throw new ArgumentException("'reorder' must contain every current slide number exactly once.");
        list.RemoveNodes();
        list.Add(order.Select(item => elements[item - 1]));
        summary.Moved++;
    }

    private static void ApplyFindReplace(ZipArchive archive, XDocument presentation, XDocument relationships,
        JsonElement operation, JsonElement findElement, EditSummary summary)
    {
        if (findElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(findElement.GetString()))
            throw new ArgumentException("'find' must be a non-empty string.");
        if (!operation.TryGetProperty("replace", out var replaceElement) || replaceElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException("A find operation requires string 'replace'.");
        var find = findElement.GetString()!;
        var replacement = replaceElement.GetString()!;
        var replaceAll = operation.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.True;
        var includeNotes = operation.TryGetProperty("includeNotes", out var notes) && notes.ValueKind == JsonValueKind.True;
        var slides = ReadCurrentSlides(presentation, relationships);
        var targets = operation.TryGetProperty("slide", out var slideElement)
            ? [ResolveSlide(slides, ReadSlideNumber(slideElement, "slide"))]
            : slides;
        var stop = false;

        foreach (var target in targets)
        {
            var document = ReadXml(RequiredEntry(archive, target.PartPath), preserveWhitespace: true);
            var changed = ReplaceInDocument(document, find, replacement, replaceAll, ref stop);
            if (changed > 0)
            {
                ReplaceXmlEntry(archive, target.PartPath, document);
                summary.Replacements += changed;
            }
            if (stop) break;

            if (!includeNotes) continue;
            var notesPart = ReadRelationships(archive, target.PartPath).FirstOrDefault(relationship => relationship.Type == NotesSlideRelationship && !relationship.IsExternal)?.ResolvedTarget;
            if (notesPart is null || archive.GetEntry(notesPart) is null) continue;
            var notesDocument = ReadXml(RequiredEntry(archive, notesPart), preserveWhitespace: true);
            changed = ReplaceInDocument(notesDocument, find, replacement, replaceAll, ref stop);
            if (changed > 0)
            {
                ReplaceXmlEntry(archive, notesPart, notesDocument);
                summary.Replacements += changed;
            }
            if (stop) break;
        }
    }

    private static int ReplaceInDocument(XDocument document, string find, string replacement, bool replaceAll, ref bool stop)
    {
        var changed = 0;
        foreach (var paragraph in document.Descendants(A + "p"))
        {
            var count = DrawingTextEditor.Replace(paragraph, find, replacement, replaceAll);
            changed += count;
            if (!replaceAll && count > 0) { stop = true; break; }
        }
        return changed;
    }

    private static void ApplySlideContentEdit(ZipArchive archive, XDocument presentation, XDocument relationships,
        JsonElement operation, EditSummary summary)
    {
        if (!operation.TryGetProperty("slide", out var slideElement))
            throw new ArgumentException("A content edit requires 'slide'.");
        var slides = ReadCurrentSlides(presentation, relationships);
        var slide = ResolveSlide(slides, ReadSlideNumber(slideElement, "slide"));
        var document = ReadXml(RequiredEntry(archive, slide.PartPath), preserveWhitespace: true);

        if (operation.TryGetProperty("setTitle", out var setTitle))
        {
            if (setTitle.ValueKind != JsonValueKind.String) throw new ArgumentException("'setTitle' must be a string.");
            var shape = FindTitleShape(document) ?? throw new InvalidOperationException($"Slide {slideElement} has no text shape that can serve as a title.");
            DrawingTextEditor.SetText(shape.Element(P + "txBody")!, setTitle.GetString()!);
            summary.ShapesRewritten++;
        }
        else if (operation.TryGetProperty("setText", out var setText))
        {
            if (setText.ValueKind != JsonValueKind.String) throw new ArgumentException("'setText' must be a string.");
            var shape = FindShape(document, operation);
            var textBody = shape.Element(P + "txBody") ?? throw new InvalidOperationException("The selected shape has no editable text body.");
            DrawingTextEditor.SetText(textBody, setText.GetString()!);
            summary.ShapesRewritten++;
        }
        else if (operation.TryGetProperty("addTextBox", out var addTextBox))
        {
            AddTextBox(document, addTextBox);
            summary.TextBoxesAdded++;
        }
        else throw new ArgumentException("Slide content operation must specify setTitle, setText or addTextBox.");

        ReplaceXmlEntry(archive, slide.PartPath, document);
    }

    private static XElement FindShape(XDocument document, JsonElement operation)
    {
        var shapes = document.Descendants(P + "sp").ToList();
        if (operation.TryGetProperty("shapeId", out var idElement))
        {
            var id = ReadSlideNumber(idElement, "shapeId");
            return shapes.FirstOrDefault(shape => (string?)shape.Element(P + "nvSpPr")?.Element(P + "cNvPr")?.Attribute("id") == id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                ?? throw new ArgumentException($"Shape id {id} was not found.");
        }
        if (operation.TryGetProperty("shapeName", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
        {
            var name = nameElement.GetString();
            return shapes.FirstOrDefault(shape => string.Equals((string?)shape.Element(P + "nvSpPr")?.Element(P + "cNvPr")?.Attribute("name"), name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Shape name '{name}' was not found.");
        }
        throw new ArgumentException("setText requires shapeId or shapeName.");
    }

    private static XElement? FindTitleShape(XDocument document)
    {
        var shapes = document.Descendants(P + "sp").Where(shape => shape.Element(P + "txBody") is not null).ToList();
        return shapes.FirstOrDefault(shape => (string?)shape.Descendants(P + "ph").FirstOrDefault()?.Attribute("type") is "title" or "ctrTitle")
            ?? shapes.FirstOrDefault(shape => ((string?)shape.Element(P + "nvSpPr")?.Element(P + "cNvPr")?.Attribute("name"))?.Contains("title", StringComparison.OrdinalIgnoreCase) == true)
            ?? shapes.FirstOrDefault();
    }

    private static void AddTextBox(XDocument document, JsonElement spec)
    {
        if (spec.ValueKind != JsonValueKind.Object) throw new ArgumentException("'addTextBox' must be an object.");
        var tree = document.Root?.Element(P + "cSld")?.Element(P + "spTree") ?? throw new InvalidDataException("Slide has no shape tree.");
        var id = tree.Descendants(P + "cNvPr").Select(item => int.TryParse((string?)item.Attribute("id"), out var value) ? value : 0).DefaultIfEmpty(0).Max() + 1;
        if (id > MaxElementsPerSlide + 1) throw new InvalidOperationException($"A slide may contain at most {MaxElementsPerSlide} editable elements.");
        var bounds = ReadBoundsSpec(spec);
        var theme = new ThemeSettings("FFFFFF", "203864", "F05A28", "222222", "666666", "Arial", "Microsoft YaHei");
        var style = TextStyle.Parse(spec, theme, 18);
        var paragraphs = SlideBuilder.ReadParagraphs(spec, bulletDefault: false);
        var name = ReadOptionalString(spec, "name") ?? $"Text Box {id}";
        var fill = ReadOptionalString(spec, "fill");
        var line = ReadOptionalString(spec, "line");
        var shape = new XElement(P + "sp",
            new XElement(P + "nvSpPr", new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
                new XElement(P + "cNvSpPr", new XAttribute("txBox", 1)), new XElement(P + "nvPr")),
            new XElement(P + "spPr", SlideBuilder.Transform(bounds),
                new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")),
                fill is null ? new XElement(A + "noFill") : new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", NormalizeColor(fill, theme.Background)))),
                line is null ? new XElement(A + "ln", new XElement(A + "noFill")) : new XElement(A + "ln", new XAttribute("w", PointsToEmus(ReadOptionalDouble(spec, "lineWidth") ?? 1)),
                    new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", NormalizeColor(line, theme.Primary)))))),
            SlideBuilder.TextBody(paragraphs, style, spec));
        var extensionList = tree.Element(P + "extLst");
        if (extensionList is null) tree.Add(shape); else extensionList.AddBeforeSelf(shape);
    }

    private static List<SlidePart> ReadCurrentSlides(XDocument presentation, XDocument relationships)
    {
        var map = relationships.Root?.Elements(PackageRelationshipNs + "Relationship")
            .Where(relationship => (string?)relationship.Attribute("Type") == SlideRelationship)
            .ToDictionary(relationship => (string)relationship.Attribute("Id")!, relationship =>
                ResolvePartPath(PresentationPart, (string)relationship.Attribute("Target")!), StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var list = presentation.Root?.Element(P + "sldIdLst") ?? throw new InvalidDataException("Presentation has no p:sldIdLst.");
        return list.Elements(P + "sldId").Select(element =>
        {
            var relationshipId = (string?)element.Attribute(R + "id") ?? throw new InvalidDataException("A slide id has no relationship id.");
            if (!map.TryGetValue(relationshipId, out var target)) throw new InvalidDataException($"Slide relationship '{relationshipId}' is missing.");
            if (!uint.TryParse((string?)element.Attribute("id"), out var id)) throw new InvalidDataException("A slide id is not numeric.");
            return new SlidePart(id, relationshipId, target, element);
        }).ToList();
    }

    private static SlidePart ResolveSlide(IReadOnlyList<SlidePart> slides, int number)
    {
        if (number < 1 || number > slides.Count) throw new ArgumentException($"Slide number must be from 1 to {slides.Count}.");
        return slides[number - 1];
    }

    private static int ReadSlideNumber(JsonElement element, string property, bool allowZero = false)
    {
        if (!element.TryGetInt32(out var number) || number < (allowZero ? 0 : 1))
            throw new ArgumentException($"'{property}' must be {(allowZero ? "a non-negative" : "a positive")} integer.");
        return number;
    }

    private static void GuardStructureOrder(bool contentEditStarted)
    {
        if (contentEditStarted) throw new ArgumentException("Structural operations must appear before text/content operations in one edit call.");
    }

    private static string NextSlidePartPath(ZipArchive archive)
    {
        var used = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index <= MaxSlides * 4; index++)
        {
            var candidate = $"ppt/slides/slide{index}.xml";
            if (!used.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("Unable to allocate a slide part name.");
    }

    private static string NextNotesPartPath(ZipArchive archive)
    {
        var used = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index <= MaxSlides * 4; index++)
        {
            var candidate = $"ppt/notesSlides/notesSlide{index}.xml";
            if (!used.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("Unable to allocate a notes-slide part name.");
    }

    private static string NextRelationshipId(XDocument relationships)
    {
        var used = relationships.Root?.Elements(PackageRelationshipNs + "Relationship")
            .Select(element => (string?)element.Attribute("Id"))
            .Where(value => value is not null).ToHashSet(StringComparer.Ordinal) ?? [];
        for (var index = 1; index < 100_000; index++) if (!used.Contains($"rId{index}")) return $"rId{index}";
        throw new InvalidOperationException("Unable to allocate a relationship id.");
    }

    private static void CopyArchiveEntry(ZipArchive archive, string sourcePath, string targetPath)
    {
        var source = RequiredEntry(archive, sourcePath);
        byte[] content;
        using (var sourceStream = source.Open())
        using (var memory = new MemoryStream()) { sourceStream.CopyTo(memory); content = memory.ToArray(); }
        var target = archive.CreateEntry(targetPath, CompressionLevel.Optimal);
        using var targetStream = target.Open();
        targetStream.Write(content, 0, content.Length);
    }

    private static void AddContentTypeOverride(XDocument contentTypes, string partPath, string contentType)
    {
        if (contentTypes.Root?.Elements(ContentTypesNs + "Override")
            .Any(item => ((string?)item.Attribute("PartName"))?.TrimStart('/').Equals(partPath, StringComparison.OrdinalIgnoreCase) == true) == true) return;
        contentTypes.Root!.Add(new XElement(ContentTypesNs + "Override", new XAttribute("PartName", "/" + partPath), new XAttribute("ContentType", contentType)));
    }

    private sealed class EditSummary
    {
        public int Duplicated { get; set; }
        public int Deleted { get; set; }
        public int Moved { get; set; }
        public int Replacements { get; set; }
        public int ShapesRewritten { get; set; }
        public int TextBoxesAdded { get; set; }
        public List<object> SharedComplexParts { get; } = [];
        public List<string> Warnings { get; } = [];
    }
}
