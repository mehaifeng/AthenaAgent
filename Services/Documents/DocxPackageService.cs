using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Athena.UI.Services.Ooxml;
using static Athena.UI.Services.Documents.WordprocessingSchema;

namespace Athena.UI.Services.Documents;

/// <summary>
/// Dependency-free WordprocessingML operations behind Athena's built-in docx skill. Reading and
/// creating are complete; editing is deliberately surgical - it rewrites the paragraphs, runs and
/// table cells it is asked to and leaves every other package part untouched.
/// </summary>
public sealed partial class DocxPackageService : OoxmlPackageService
{
    private const string DocumentPart = "word/document.xml";
    private const string StylesPart = "word/styles.xml";
    private const string NumberingPart = "word/numbering.xml";
    private const string SettingsPart = "word/settings.xml";

    /// <summary>styles, numbering and settings occupy rId1-rId3; embedded media continues from rId4.</summary>
    private const int FixedRelationshipCount = 3;
    private const int BulletNumberingId = 1;
    private const int OrderedNumberingId = 2;
    private const int PreviewTextLimit = 500;

    /// <summary>
    /// Reports the structure of a document: an outline of every heading, a windowed list of
    /// body paragraphs with their index, style and heading path, plus tables and package features.
    /// </summary>
    public object Inspect(string path, int startParagraph, int maxParagraphs, bool includeTableText)
    {
        startParagraph = Math.Max(1, startParagraph);
        maxParagraphs = Math.Clamp(maxParagraphs, 1, 300);
        var endParagraph = startParagraph + maxParagraphs - 1;

        using var archive = OpenChecked(path, ZipArchiveMode.Read);
        var document = ReadXml(RequiredEntry(archive, DocumentPart));
        var body = document.Root?.Element(W + "body") ?? throw new InvalidDataException("Document has no body.");
        var outlineLevels = ReadStyleOutlineLevels(archive);

        var outline = new List<object>();
        var paragraphs = new List<object>();
        var headingStack = new List<(int Level, string Text)>();
        var paragraphIndex = 0;
        var wordCount = 0;

        foreach (var paragraph in body.Elements(W + "p"))
        {
            paragraphIndex++;
            var text = ParagraphText(paragraph);
            wordCount += CountWords(text);
            var level = HeadingLevel(paragraph, outlineLevels);

            if (level is int headingLevel)
            {
                while (headingStack.Count > 0 && headingStack[^1].Level >= headingLevel) headingStack.RemoveAt(headingStack.Count - 1);
                headingStack.Add((headingLevel, text));
                outline.Add(new { paragraph = paragraphIndex, level = headingLevel, text = Truncate(text, 200) });
            }

            if (paragraphIndex < startParagraph || paragraphIndex > endParagraph) continue;

            paragraphs.Add(new
            {
                index = paragraphIndex,
                style = ParagraphStyle(paragraph),
                headingLevel = level,
                headingPath = level is null && headingStack.Count > 0 ? HeadingPath(headingStack) : null,
                listLevel = IsListParagraph(paragraph) ? ListLevel(paragraph) : (int?)null,
                text = Truncate(text, PreviewTextLimit),
                truncated = text.Length > PreviewTextLimit ? true : (bool?)null,
                empty = text.Length == 0 ? true : (bool?)null
            });
        }

        var tables = new List<object>();
        var tableIndex = 0;
        foreach (var table in body.Elements(W + "tbl"))
        {
            tableIndex++;
            var rows = table.Elements(W + "tr").ToList();
            var columns = rows.Count == 0 ? 0 : rows.Max(row => row.Elements(W + "tc").Count());
            tables.Add(new
            {
                index = tableIndex,
                rows = rows.Count,
                columns,
                firstRow = rows.Count == 0
                    ? []
                    : rows[0].Elements(W + "tc").Select(cell => Truncate(CellText(cell), 80)).ToArray(),
                cells = includeTableText
                    ? rows.Select(row => row.Elements(W + "tc").Select(cell => Truncate(CellText(cell), 200)).ToArray()).ToArray()
                    : null
            });
        }

        return new
        {
            path,
            paragraphCount = paragraphIndex,
            tableCount = tableIndex,
            wordCount,
            outline,
            window = new { startParagraph, endParagraph = Math.Min(endParagraph, paragraphIndex) },
            hasMoreParagraphs = paragraphIndex > endParagraph,
            nextStartParagraph = paragraphIndex > endParagraph ? endParagraph + 1 : (int?)null,
            paragraphs,
            tables,
            features = DetectFeatures(archive),
            pagingHint = "Paragraph indexes address body paragraphs in document order and are what edit_document expects. Page with startParagraph, or use convert_document to read the whole document as Markdown."
        };
    }

    public object Validate(string path)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        try
        {
            using var archive = OpenChecked(path, ZipArchiveMode.Read);
            foreach (var required in new[] { "[Content_Types].xml", "_rels/.rels", DocumentPart })
            {
                if (archive.GetEntry(required) is null) errors.Add($"Missing required OOXML part: {required}");
            }
            if (errors.Count > 0) return Report(false, errors, warnings, []);

            foreach (var entry in archive.Entries.Where(entry =>
                entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                XDocument part;
                try
                {
                    part = ReadXml(entry);
                }
                catch (Exception ex)
                {
                    errors.Add($"Invalid XML in {entry.FullName}: {ex.Message}");
                    continue;
                }

                if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    var sourcePart = SourcePartForRelationships(entry.FullName);
                    foreach (var relationship in part.Root?.Elements(PackageRelationshipNs + "Relationship") ?? [])
                    {
                        if (string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
                        var target = (string?)relationship.Attribute("Target");
                        if (string.IsNullOrWhiteSpace(target)) continue;
                        var resolved = ResolvePartPath(sourcePart, target);
                        if (archive.GetEntry(resolved) is null) errors.Add($"Broken relationship in {entry.FullName}: {target} -> {resolved}");
                    }
                }
            }

            var document = ReadXml(RequiredEntry(archive, DocumentPart));
            var body = document.Root?.Element(W + "body");
            if (body is null)
            {
                errors.Add("word/document.xml has no w:body element.");
                return Report(false, errors, warnings, DetectFeatures(archive));
            }

            errors.AddRange(FindStructuralProblems(body));
            errors.AddRange(FindPropertyOrderProblems(document));
            errors.AddRange(FindDanglingReferences(archive, document));

            // Styles carry the same ordered property models, and a bad style breaks every
            // paragraph that uses it rather than just one.
            if (archive.GetEntry(StylesPart) is { } stylesEntry)
                errors.AddRange(FindPropertyOrderProblems(ReadXml(stylesEntry)).Select(problem => $"word/styles.xml: {problem}"));

            if (body.Elements(W + "sectPr").LastOrDefault() is null)
                warnings.Add("The body has no final w:sectPr; Word will apply default page setup.");

            var features = DetectFeatures(archive);
            if (features.Count > 0)
                warnings.Add("Document contains features Athena preserves but does not edit: " + string.Join(", ", features) + ". Verify them in Word or LibreOffice.");
            warnings.Add("Validation is structural and static; it does not paginate, repaginate fields or render the document.");

            return Report(errors.Count == 0, errors, warnings, features);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            return Report(false, errors, warnings, []);
        }
    }

    private static object Report(bool valid, List<string> errors, List<string> warnings, IReadOnlyList<string> features) => new
    {
        valid,
        errors,
        warnings,
        features,
        dynamicValidationPerformed = false
    };

    /// <summary>Structural rules Word enforces on open, checked here so a repair prompt never surprises the user.</summary>
    private static IEnumerable<string> FindStructuralProblems(XElement body)
    {
        foreach (var cell in body.Descendants(W + "tc"))
        {
            if (!cell.Elements(W + "p").Any())
                yield return $"Table cell without a paragraph: every w:tc must contain at least one w:p ({DescribeCell(cell)}).";
        }

        foreach (var paragraph in body.Descendants(W + "p"))
        {
            var properties = paragraph.Element(W + "pPr");
            if (properties is not null && paragraph.Elements().First() != properties)
                yield return "w:pPr must be the first child of its w:p.";
        }

        foreach (var run in body.Descendants(W + "r"))
        {
            var properties = run.Element(W + "rPr");
            if (properties is not null && run.Elements().First() != properties)
                yield return "w:rPr must be the first child of its w:r.";
        }

        foreach (var row in body.Descendants(W + "tr"))
        {
            if (!row.Elements(W + "tc").Any()) yield return "Table row without cells.";
        }
    }

    private static string DescribeCell(XElement cell)
    {
        var row = cell.Parent;
        var table = row?.Parent;
        var rowIndex = row is null || table is null ? 0 : table.Elements(W + "tr").ToList().IndexOf(row) + 1;
        var columnIndex = row is null ? 0 : row.Elements(W + "tc").ToList().IndexOf(cell) + 1;
        return $"row {rowIndex}, column {columnIndex}";
    }

    /// <summary>
    /// Property elements must appear in schema order. Out-of-order children are the single most
    /// common way a hand-built docx becomes "unreadable content" in Word.
    /// </summary>
    private static IEnumerable<string> FindPropertyOrderProblems(XDocument document)
    {
        foreach (var problem in CheckOrder(document, "pPr", ParagraphPropertyOrder)) yield return problem;
        foreach (var problem in CheckOrder(document, "rPr", RunPropertyOrder)) yield return problem;
    }

    private static IEnumerable<string> CheckOrder(XDocument document, string containerName, IReadOnlyList<string> order)
    {
        foreach (var container in document.Descendants(W + containerName))
        {
            var highest = -1;
            foreach (var child in container.Elements())
            {
                if (child.Name.Namespace != W) continue;
                var position = -1;
                for (var index = 0; index < order.Count; index++)
                {
                    if (!string.Equals(order[index], child.Name.LocalName, StringComparison.Ordinal)) continue;
                    position = index;
                    break;
                }
                if (position < 0) continue;
                if (position < highest)
                {
                    yield return $"w:{containerName} children are out of schema order at w:{child.Name.LocalName}.";
                    break;
                }
                highest = position;
            }
        }
    }

    private static IEnumerable<string> FindDanglingReferences(ZipArchive archive, XDocument document)
    {
        var styles = ReadStyleIds(archive);
        if (styles.Count > 0)
        {
            foreach (var reference in document.Descendants(W + "pStyle").Concat(document.Descendants(W + "rStyle")))
            {
                var id = (string?)reference.Attribute(W + "val");
                if (id is not null && !styles.Contains(id)) yield return $"Paragraph or run references a style that word/styles.xml does not define: {id}";
            }
        }

        var numbering = ReadNumberingIds(archive);
        foreach (var reference in document.Descendants(W + "numPr").Elements(W + "numId"))
        {
            var id = (string?)reference.Attribute(W + "val");
            if (id is not null && numbering.Count > 0 && !numbering.Contains(id))
                yield return $"List paragraph references numbering id {id}, which word/numbering.xml does not define.";
        }

        var targets = ReadRelationshipTargets(archive, DocumentPart);
        foreach (var blip in document.Descendants(A + "blip"))
        {
            var id = (string?)blip.Attribute(R + "embed");
            if (id is not null && !targets.ContainsKey(id)) yield return $"Image references relationship {id}, which word/_rels/document.xml.rels does not declare.";
        }
    }

    private static HashSet<string> ReadStyleIds(ZipArchive archive)
    {
        var entry = archive.GetEntry(StylesPart);
        if (entry is null) return [];
        return ReadXml(entry).Root?.Elements(W + "style")
            .Select(style => (string?)style.Attribute(W + "styleId") ?? string.Empty)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal) ?? [];
    }

    private static HashSet<string> ReadNumberingIds(ZipArchive archive)
    {
        var entry = archive.GetEntry(NumberingPart);
        if (entry is null) return [];
        return ReadXml(entry).Root?.Elements(W + "num")
            .Select(num => (string?)num.Attribute(W + "numId") ?? string.Empty)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal) ?? [];
    }

    private static Dictionary<string, int> ReadStyleOutlineLevels(ZipArchive archive)
    {
        var entry = archive.GetEntry(StylesPart);
        return entry is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new DocxStyleLibrary(ReadXml(entry)).OutlineLevels();
    }

    private static List<string> DetectFeatures(ZipArchive archive)
    {
        var features = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in archive.Entries.Select(entry => entry.FullName))
        {
            if (name.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)) features.Add("images");
            else if (name.Equals("word/comments.xml", StringComparison.OrdinalIgnoreCase)) features.Add("comments");
            else if (name.Equals("word/footnotes.xml", StringComparison.OrdinalIgnoreCase)) features.Add("footnotes");
            else if (name.Equals("word/endnotes.xml", StringComparison.OrdinalIgnoreCase)) features.Add("endnotes");
            else if (name.StartsWith("word/header", StringComparison.OrdinalIgnoreCase)) features.Add("headers");
            else if (name.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase)) features.Add("footers");
            else if (name.Equals("word/vbaProject.bin", StringComparison.OrdinalIgnoreCase)) features.Add("macros");
            else if (name.StartsWith("word/embeddings/", StringComparison.OrdinalIgnoreCase)) features.Add("embedded objects");
            else if (name.StartsWith("word/charts/", StringComparison.OrdinalIgnoreCase)) features.Add("charts");
        }

        var documentEntry = archive.GetEntry(DocumentPart);
        if (documentEntry is not null)
        {
            var document = ReadXml(documentEntry);
            if (document.Descendants(W + "ins").Any() || document.Descendants(W + "del").Any()) features.Add("tracked changes");
            if (document.Descendants(W + "sdt").Any()) features.Add("content controls");
            if (document.Descendants(W + "fldChar").Any() || document.Descendants(W + "fldSimple").Any()) features.Add("fields");
            if (document.Descendants(W + "txbxContent").Any()) features.Add("text boxes");
        }
        return features.ToList();
    }

    internal static string CellText(XElement cell) =>
        string.Join("\n", cell.Elements(W + "p").Select(ParagraphText)).Trim();

    private static int CountWords(string text)
    {
        if (text.Length == 0) return 0;
        var words = 0;
        var inWord = false;
        foreach (var character in text)
        {
            // CJK has no spaces; counting each ideograph as a word keeps the estimate meaningful.
            if (character >= 0x4E00 && character <= 0x9FFF)
            {
                words++;
                inWord = false;
                continue;
            }
            if (char.IsWhiteSpace(character)) inWord = false;
            else if (!inWord)
            {
                words++;
                inWord = true;
            }
        }
        return words;
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";

    private static ZipArchive OpenChecked(string path, ZipArchiveMode mode)
    {
        EnsureDocumentExtension(path);
        return OpenPackage(path, mode);
    }

    private static void EnsureDocumentExtension(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".docm", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only .docx and .docm OOXML documents are supported. Convert .doc in Word or LibreOffice first.");
    }

    private static void EnsureDistinctDocumentPaths(string inputPath, string outputPath)
    {
        EnsureDocumentExtension(inputPath);
        EnsureDocumentExtension(outputPath);
        EnsureDistinctPaths(inputPath, outputPath);
    }

    private static string Serialize(XElement root) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + root.ToString(SaveOptions.DisableFormatting);

    private static string DocumentRootRelationshipsXml =>
        RootRelationshipsXml(DocumentPart, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument");

    private static string ContentTypesXml(IReadOnlyList<MediaPart> media)
    {
        var defaults = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in media)
        {
            var extension = Path.GetExtension(part.FileName).TrimStart('.').ToLowerInvariant();
            defaults.Add($"<Default Extension=\"{extension}\" ContentType=\"{part.ContentType}\"/>");
        }

        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<Types xmlns=\"{ContentTypesNs}\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            string.Join(string.Empty, defaults) +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" +
            "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>" +
            "</Types>";
    }

    private static string DocumentRelationshipsXml(IReadOnlyList<MediaPart> media)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append($"<Relationships xmlns=\"{PackageRelationshipNs}\">");
        builder.Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        builder.Append("<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>");
        builder.Append("<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>");
        foreach (var part in media)
        {
            builder.Append($"<Relationship Id=\"{part.RelationshipId}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/{part.FileName}\"/>");
        }
        builder.Append("</Relationships>");
        return builder.ToString();
    }

    private static string AppPropertiesXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" " +
        "xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
        "<Application>AthenaAgent</Application></Properties>";

    private static string SettingsXml(bool updateFields) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        $"<w:settings xmlns:w=\"{W.NamespaceName}\">" +
        "<w:zoom w:percent=\"100\"/>" +
        "<w:defaultTabStop w:val=\"420\"/>" +
        (updateFields ? "<w:updateFields w:val=\"true\"/>" : string.Empty) +
        "<w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat>" +
        "</w:settings>";

    /// <summary>
    /// Two numbering definitions - bullets (numId 1) and decimals (numId 2) - each with nine levels.
    /// Every level restarts its own counter, which is what a Markdown-style nested list expects.
    /// </summary>
    private static string NumberingXml
    {
        get
        {
            var builder = new StringBuilder();
            builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            builder.Append($"<w:numbering xmlns:w=\"{W.NamespaceName}\">");

            string[] bullets = ["•", "◦", "▪"];
            for (var abstractId = 0; abstractId < 2; abstractId++)
            {
                builder.Append($"<w:abstractNum w:abstractNumId=\"{abstractId}\">");
                builder.Append("<w:multiLevelType w:val=\"hybridMultilevel\"/>");
                for (var level = 0; level < 9; level++)
                {
                    var indent = 420 * (level + 1);
                    builder.Append($"<w:lvl w:ilvl=\"{level}\">");
                    builder.Append("<w:start w:val=\"1\"/>");
                    builder.Append(abstractId == 0
                        ? $"<w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"{bullets[level % bullets.Length]}\"/>"
                        : $"<w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%{level + 1}.\"/>");
                    builder.Append("<w:lvlJc w:val=\"left\"/>");
                    builder.Append($"<w:pPr><w:ind w:left=\"{indent}\" w:hanging=\"420\"/></w:pPr>");
                    builder.Append("</w:lvl>");
                }
                builder.Append("</w:abstractNum>");
            }

            builder.Append($"<w:num w:numId=\"{BulletNumberingId}\"><w:abstractNumId w:val=\"0\"/></w:num>");
            builder.Append($"<w:num w:numId=\"{OrderedNumberingId}\"><w:abstractNumId w:val=\"1\"/></w:num>");
            builder.Append("</w:numbering>");
            return builder.ToString();
        }
    }
}
