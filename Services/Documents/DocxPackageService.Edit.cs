using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using static Athena.UI.Services.Documents.WordprocessingSchema;

namespace Athena.UI.Services.Documents;

public sealed partial class DocxPackageService
{
    private const int MaxOperations = 2_000;

    /// <summary>
    /// Applies surgical edits to an existing document. Every target is resolved against the input
    /// first and only then mutated, so all paragraph and table indexes in one call refer to the
    /// document as it was read - inserting at paragraph 3 never shifts the meaning of paragraph 9.
    /// </summary>
    public object Edit(string inputPath, string outputPath, string operationsJson, bool overwrite)
    {
        EnsureDistinctDocumentPaths(inputPath, outputPath);
        EnsureCanWrite(outputPath, overwrite);

        using var json = JsonDocument.Parse(operationsJson, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip });
        var operationsElement = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement
            : json.RootElement.TryGetProperty("operations", out var nested) ? nested : default;
        if (operationsElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("operationsJson must be an array or an object containing an 'operations' array.");
        var operations = operationsElement.EnumerateArray().ToList();
        if (operations.Count is < 1 or > MaxOperations)
            throw new ArgumentException($"Provide 1-{MaxOperations} operations per call.");

        using (var checkedArchive = OpenChecked(inputPath, ZipArchiveMode.Read))
            _ = RequiredEntry(checkedArchive, DocumentPart);

        var summary = new EditSummary();

        AtomicWrite(outputPath, overwrite, temporaryPath =>
        {
            File.Copy(inputPath, temporaryPath, overwrite: true);
            using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false, Encoding.UTF8);

            var document = ReadXml(RequiredEntry(archive, DocumentPart), preserveWhitespace: true);
            var body = document.Root?.Element(W + "body") ?? throw new InvalidDataException("Document has no body.");

            var stylesEntry = archive.GetEntry(StylesPart);
            var styles = new DocxStyleLibrary(stylesEntry is null
                ? DocxStyleLibrary.CreateDefault("Calibri", "等线", 11).Document
                : ReadXml(stylesEntry, preserveWhitespace: true));
            var stylesChanged = stylesEntry is null;

            var media = new PackageMediaAllocator(archive);
            var page = ReadPageSetup(body);
            var builder = new BodyBuilder(styles, page, media);

            var paragraphs = body.Elements(W + "p").ToList();
            var tables = body.Elements(W + "tbl").ToList();
            var actions = new List<Action>();

            foreach (var operation in operations)
            {
                if (operation.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each operation must be an object.");

                if (operation.TryGetProperty("defineStyle", out var styleSpec))
                {
                    styles.RegisterStyle(styleSpec);
                    stylesChanged = true;
                    continue;
                }

                if (operation.TryGetProperty("appendBlock", out var appendBlock))
                {
                    var fragment = BuildFragment(builder, appendBlock);
                    actions.Add(() =>
                    {
                        var sectionProperties = body.Elements(W + "sectPr").LastOrDefault();
                        foreach (var element in fragment)
                        {
                            if (sectionProperties is null) body.Add(element);
                            else sectionProperties.AddBeforeSelf(element);
                        }
                        summary.Inserted += fragment.Count;
                    });
                    continue;
                }

                if (operation.TryGetProperty("find", out var findElement))
                {
                    actions.Add(BuildReplaceAction(body, operation, findElement, summary));
                    continue;
                }

                if (operation.TryGetProperty("table", out var tableElement))
                {
                    actions.Add(BuildTableAction(tables, operation, tableElement, summary));
                    continue;
                }

                var paragraph = ResolveParagraph(paragraphs, operation);
                actions.Add(BuildParagraphAction(builder, styles, paragraph, operation, summary));
            }

            foreach (var action in actions) action();

            ReplaceXmlEntry(archive, DocumentPart, document);
            if (stylesChanged) WriteOrReplaceStyles(archive, styles);
            media.Commit();
        });

        return new
        {
            inputPath,
            outputPath,
            paragraphsChanged = summary.Modified,
            paragraphsInserted = summary.Inserted,
            paragraphsDeleted = summary.Deleted,
            textReplacements = summary.Replacements,
            tableCellsChanged = summary.TableCells,
            nextStep = "Run validate_document. Field results such as a table of contents are only recalculated when Word or LibreOffice opens the file."
        };
    }

    private sealed class EditSummary
    {
        public int Modified;
        public int Inserted;
        public int Deleted;
        public int Replacements;
        public int TableCells;
    }

    private static XElement ResolveParagraph(IReadOnlyList<XElement> paragraphs, JsonElement operation)
    {
        if (!operation.TryGetProperty("paragraph", out var indexElement))
            throw new ArgumentException("An operation must target 'paragraph', 'table', 'find', 'appendBlock' or 'defineStyle'.");
        if (!indexElement.TryGetInt32(out var index) || index < 1)
            throw new ArgumentException("'paragraph' must be a 1-based integer index from inspect_document.");
        if (index > paragraphs.Count)
            throw new ArgumentException($"Paragraph {index} does not exist; the document has {paragraphs.Count} body paragraphs.");
        return paragraphs[index - 1];
    }

    private static Action BuildParagraphAction(BodyBuilder builder, DocxStyleLibrary styles, XElement paragraph,
        JsonElement operation, EditSummary summary)
    {
        if (operation.TryGetProperty("delete", out var delete) && delete.ValueKind == JsonValueKind.True)
        {
            return () =>
            {
                paragraph.Remove();
                summary.Deleted++;
            };
        }

        if (operation.TryGetProperty("insertBefore", out var before))
        {
            var fragment = BuildFragment(builder, before);
            return () =>
            {
                foreach (var element in fragment) paragraph.AddBeforeSelf(element);
                summary.Inserted += fragment.Count;
            };
        }

        if (operation.TryGetProperty("insertAfter", out var after))
        {
            var fragment = BuildFragment(builder, after);
            return () =>
            {
                // Added in reverse so the fragment keeps its own order directly after the anchor.
                foreach (var element in Enumerable.Reverse(fragment)) paragraph.AddAfterSelf(element);
                summary.Inserted += fragment.Count;
            };
        }

        var mutations = new List<Action>();

        if (operation.TryGetProperty("setStyle", out var styleElement))
        {
            if (styleElement.ValueKind != JsonValueKind.String) throw new ArgumentException("'setStyle' must be a style name.");
            var name = styleElement.GetString()!.Trim();
            if (!styles.TryResolveName(name, out var styleId))
                throw new ArgumentException($"Unknown style '{name}'. Use defineStyle first, or a style the document already defines.");
            mutations.Add(() =>
            {
                var properties = EnsureParagraphProperties(paragraph);
                SetProperty(properties, ParagraphPropertyOrder, Value("pStyle", styleId));
            });
        }

        if (operation.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object)
        {
            var formatting = DocxStyleLibrary.BuildParagraphProperties(format);
            mutations.Add(() =>
            {
                var properties = EnsureParagraphProperties(paragraph);
                foreach (var property in formatting.Elements()) SetProperty(properties, ParagraphPropertyOrder, new XElement(property));
            });
        }

        if (operation.TryGetProperty("setText", out var setText))
        {
            if (setText.ValueKind != JsonValueKind.String) throw new ArgumentException("'setText' must be a string.");
            var text = setText.GetString()!;
            mutations.Add(() => RunTextEditor.SetParagraphText(paragraph, text));
        }

        if (operation.TryGetProperty("font", out var font) && font.ValueKind == JsonValueKind.Object)
        {
            var formatting = DocxStyleLibrary.BuildRunProperties(font);
            mutations.Add(() => RunTextEditor.ApplyRunFormatting(paragraph, formatting));
        }

        if (mutations.Count == 0)
            throw new ArgumentException("A paragraph operation needs setText, setStyle, format, font, delete, insertBefore or insertAfter.");

        return () =>
        {
            foreach (var mutation in mutations) mutation();
            summary.Modified++;
        };
    }

    private static Action BuildReplaceAction(XElement body, JsonElement operation, JsonElement findElement, EditSummary summary)
    {
        if (findElement.ValueKind != JsonValueKind.String || findElement.GetString()!.Length == 0)
            throw new ArgumentException("'find' must be a non-empty string.");
        if (!operation.TryGetProperty("replace", out var replaceElement) || replaceElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException("A 'find' operation needs a 'replace' string.");

        var search = findElement.GetString()!;
        var replacement = replaceElement.GetString()!;
        var replaceAll = !operation.TryGetProperty("all", out var all) || all.ValueKind != JsonValueKind.False;

        return () =>
        {
            var replaced = 0;
            foreach (var paragraph in body.Descendants(W + "p"))
            {
                if (!replaceAll && replaced > 0) break;
                replaced += RunTextEditor.Replace(paragraph, search, replacement, replaceAll);
            }
            if (replaced == 0) throw new ArgumentException($"Text not found, so nothing was replaced: \"{search}\"");
            summary.Replacements += replaced;
        };
    }

    private static Action BuildTableAction(IReadOnlyList<XElement> tables, JsonElement operation, JsonElement tableElement, EditSummary summary)
    {
        if (!tableElement.TryGetInt32(out var tableIndex) || tableIndex < 1)
            throw new ArgumentException("'table' must be a 1-based integer index from inspect_document.");
        if (tableIndex > tables.Count)
            throw new ArgumentException($"Table {tableIndex} does not exist; the document has {tables.Count} tables.");
        var table = tables[tableIndex - 1];
        var rows = table.Elements(W + "tr").ToList();

        if (operation.TryGetProperty("appendRow", out var appendRow))
        {
            if (appendRow.ValueKind != JsonValueKind.Array) throw new ArgumentException("'appendRow' must be an array of cell values.");
            if (rows.Count == 0) throw new ArgumentException("Cannot append a row to a table that has none.");
            var values = appendRow.EnumerateArray().Select(value => value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty
            }).ToList();

            // The last row is the template: it carries the column widths and cell properties.
            var template = rows[^1];
            return () =>
            {
                var row = new XElement(template);
                row.Elements(W + "trPr").Remove();
                var cells = row.Elements(W + "tc").ToList();
                for (var index = 0; index < cells.Count; index++)
                {
                    var text = index < values.Count ? values[index] : string.Empty;
                    SetCellText(cells[index], text);
                }
                template.AddAfterSelf(row);
                summary.TableCells += cells.Count;
            };
        }

        if (operation.TryGetProperty("deleteRow", out var deleteRow))
        {
            if (!deleteRow.TryGetInt32(out var rowIndex) || rowIndex < 1 || rowIndex > rows.Count)
                throw new ArgumentException($"'deleteRow' must be between 1 and {rows.Count}.");
            var target = rows[rowIndex - 1];
            return () =>
            {
                target.Remove();
                summary.TableCells++;
            };
        }

        if (!operation.TryGetProperty("row", out var rowElement) || !rowElement.TryGetInt32(out var row1)
            || !operation.TryGetProperty("column", out var columnElement) || !columnElement.TryGetInt32(out var column1))
            throw new ArgumentException("A table operation needs 'row' and 'column', or 'appendRow'/'deleteRow'.");
        if (row1 < 1 || row1 > rows.Count) throw new ArgumentException($"'row' must be between 1 and {rows.Count}.");

        var cellsInRow = rows[row1 - 1].Elements(W + "tc").ToList();
        if (column1 < 1 || column1 > cellsInRow.Count) throw new ArgumentException($"'column' must be between 1 and {cellsInRow.Count}.");
        var cell = cellsInRow[column1 - 1];

        if (!operation.TryGetProperty("setText", out var setText) || setText.ValueKind != JsonValueKind.String)
            throw new ArgumentException("A table cell operation needs 'setText'.");
        var value = setText.GetString()!;
        return () =>
        {
            SetCellText(cell, value);
            summary.TableCells++;
        };
    }

    private static void SetCellText(XElement cell, string text)
    {
        var paragraphs = cell.Elements(W + "p").ToList();
        if (paragraphs.Count == 0)
        {
            var created = new XElement(W + "p");
            cell.Add(created);
            paragraphs.Add(created);
        }

        // A cell must keep at least one paragraph; extra ones are collapsed into the first.
        for (var index = paragraphs.Count - 1; index >= 1; index--) paragraphs[index].Remove();
        RunTextEditor.SetParagraphText(paragraphs[0], text);
    }

    private static IReadOnlyList<XElement> BuildFragment(BodyBuilder builder, JsonElement block)
    {
        var blocks = block.ValueKind == JsonValueKind.Array ? block.EnumerateArray().ToList() : [block];
        return builder.BuildFragment(blocks);
    }

    private static PageSetup ReadPageSetup(XElement body)
    {
        var section = body.Elements(W + "sectPr").LastOrDefault();
        var size = section?.Element(W + "pgSz");
        var margins = section?.Element(W + "pgMar");

        var width = ReadTwips(size, "w", PointsToTwips(595.3));
        var height = ReadTwips(size, "h", PointsToTwips(841.9));
        return new PageSetup(
            width,
            height,
            ReadTwips(margins, "top", PointsToTwips(72)),
            ReadTwips(margins, "right", PointsToTwips(72)),
            ReadTwips(margins, "bottom", PointsToTwips(72)),
            ReadTwips(margins, "left", PointsToTwips(72)));
    }

    private static int ReadTwips(XElement? element, string attribute, int fallback) =>
        int.TryParse((string?)element?.Attribute(W + attribute), out var value) && value > 0 ? value : fallback;

    private static void WriteOrReplaceStyles(ZipArchive archive, DocxStyleLibrary styles)
    {
        ReplaceXmlEntry(archive, StylesPart, styles.Document);
        EnsureRelationship(archive, DocumentPart, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
        EnsureContentTypeOverride(archive, "/word/styles.xml",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml");
    }

    /// <summary>Adds a relationship for <paramref name="target"/> unless the part already declares one.</summary>
    private static string EnsureRelationship(ZipArchive archive, string sourcePart, string type, string target)
    {
        var relationshipsPath = RelationshipsPathFor(sourcePart);
        var entry = archive.GetEntry(relationshipsPath);
        var document = entry is null
            ? new XDocument(new XElement(PackageRelationshipNs + "Relationships"))
            : ReadXml(entry, preserveWhitespace: true);
        var root = document.Root!;

        var existing = root.Elements(PackageRelationshipNs + "Relationship")
            .FirstOrDefault(relationship => string.Equals((string?)relationship.Attribute("Target"), target, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return (string)existing.Attribute("Id")!;

        var id = NextRelationshipId(root);
        root.Add(new XElement(PackageRelationshipNs + "Relationship",
            new XAttribute("Id", id),
            new XAttribute("Type", type),
            new XAttribute("Target", target)));
        ReplaceXmlEntry(archive, relationshipsPath, document);
        return id;
    }

    private static string NextRelationshipId(XElement relationships)
    {
        var highest = 0;
        foreach (var relationship in relationships.Elements(PackageRelationshipNs + "Relationship"))
        {
            var id = (string?)relationship.Attribute("Id");
            if (id is null || !id.StartsWith("rId", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(id.AsSpan(3), out var value) && value > highest) highest = value;
        }
        return $"rId{highest + 1}";
    }

    private static void EnsureContentTypeOverride(ZipArchive archive, string partName, string contentType)
    {
        var entry = archive.GetEntry("[Content_Types].xml");
        if (entry is null) return;
        var document = ReadXml(entry, preserveWhitespace: true);
        var root = document.Root!;
        if (root.Elements(ContentTypesNs + "Override").Any(element =>
                string.Equals((string?)element.Attribute("PartName"), partName, StringComparison.OrdinalIgnoreCase)))
            return;

        root.Add(new XElement(ContentTypesNs + "Override",
            new XAttribute("PartName", partName),
            new XAttribute("ContentType", contentType)));
        ReplaceXmlEntry(archive, "[Content_Types].xml", document);
    }

    private static void EnsureContentTypeDefault(ZipArchive archive, string extension, string contentType)
    {
        var entry = archive.GetEntry("[Content_Types].xml");
        if (entry is null) return;
        var document = ReadXml(entry, preserveWhitespace: true);
        var root = document.Root!;
        if (root.Elements(ContentTypesNs + "Default").Any(element =>
                string.Equals((string?)element.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase)))
            return;

        root.AddFirst(new XElement(ContentTypesNs + "Default",
            new XAttribute("Extension", extension),
            new XAttribute("ContentType", contentType)));
        ReplaceXmlEntry(archive, "[Content_Types].xml", document);
    }

    /// <summary>Allocates image relationships inside an existing package and writes the media parts on commit.</summary>
    private sealed class PackageMediaAllocator : IMediaAllocator
    {
        private readonly ZipArchive _archive;
        private readonly List<(string FileName, string ContentType, byte[] Content)> _pending = [];
        private int _sequence;

        public PackageMediaAllocator(ZipArchive archive)
        {
            _archive = archive;
            _sequence = archive.Entries.Count(entry => entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase));
        }

        public string Allocate(ImageMedia image)
        {
            var extension = Path.GetExtension(image.FileName);
            string fileName;
            do
            {
                _sequence++;
                fileName = $"athena-image{_sequence}{extension}";
            }
            while (_archive.GetEntry($"word/media/{fileName}") is not null);

            _pending.Add((fileName, image.ContentType, image.Content));
            return EnsureRelationship(_archive,
                DocumentPart,
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
                $"media/{fileName}");
        }

        public void Commit()
        {
            foreach (var (fileName, contentType, content) in _pending)
            {
                EnsureContentTypeDefault(_archive, Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant(), contentType);
                var entry = _archive.CreateEntry($"word/media/{fileName}", CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
        }
    }
}
