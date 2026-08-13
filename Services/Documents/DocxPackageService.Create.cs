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
    private const int MaxBlocks = 20_000;

    /// <summary>
    /// Writes a new .docx from a block specification. The package is assembled in memory and only
    /// moved into place once every block validates, so a bad style or image path never leaves a
    /// half-written document behind.
    /// </summary>
    public object Create(string outputPath, string documentJson, bool overwrite)
    {
        if (!Path.GetExtension(outputPath).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("New documents must use the .docx extension.");
        EnsureCanWrite(outputPath, overwrite);

        using var json = JsonDocument.Parse(documentJson, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("documentJson must be an object.");
        if (!root.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("documentJson must contain a 'blocks' array.");

        var page = PageSetup.Parse(root.TryGetProperty("page", out var pageElement) ? pageElement : default);
        var (asciiFont, eastAsiaFont, fontSize) = ParseDefaultFont(root);
        var styles = DocxStyleLibrary.CreateDefault(asciiFont, eastAsiaFont, fontSize);
        if (root.TryGetProperty("styles", out var styleSpecs))
        {
            if (styleSpecs.ValueKind != JsonValueKind.Array) throw new ArgumentException("'styles' must be an array.");
            foreach (var spec in styleSpecs.EnumerateArray()) styles.RegisterStyle(spec);
        }

        var media = new CollectingMediaAllocator();
        var builder = new BodyBuilder(styles, page, media);
        var blockList = blocks.EnumerateArray().ToList();
        if (blockList.Count > MaxBlocks) throw new ArgumentException($"A document may contain at most {MaxBlocks} blocks.");
        foreach (var block in blockList) builder.Append(block);

        var body = builder.Build();
        var document = new XElement(W + "document",
            new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "wp", Wp.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "pic", Pic.NamespaceName),
            body);

        AtomicWrite(outputPath, overwrite, temporaryPath =>
        {
            using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
            WriteTextEntry(archive, "[Content_Types].xml", ContentTypesXml(media.Parts));
            WriteTextEntry(archive, "_rels/.rels", DocumentRootRelationshipsXml);
            WriteTextEntry(archive, "docProps/core.xml", CorePropertiesXml());
            WriteTextEntry(archive, "docProps/app.xml", AppPropertiesXml());
            WriteTextEntry(archive, "word/document.xml", Serialize(document));
            WriteTextEntry(archive, "word/_rels/document.xml.rels", DocumentRelationshipsXml(media.Parts));
            WriteTextEntry(archive, "word/styles.xml", Serialize(styles.Document.Root!));
            WriteTextEntry(archive, "word/numbering.xml", NumberingXml);
            WriteTextEntry(archive, SettingsPart, SettingsXml(builder.HasFields));

            foreach (var part in media.Parts)
            {
                var entry = archive.CreateEntry($"word/media/{part.FileName}", CompressionLevel.Optimal);
                using var mediaStream = entry.Open();
                mediaStream.Write(part.Content, 0, part.Content.Length);
            }
        });

        return new
        {
            outputPath,
            paragraphCount = builder.ParagraphCount,
            tableCount = builder.TableCount,
            imageCount = media.Parts.Count,
            warnings = builder.Warnings,
            nextStep = "Run validate_document, then open in Word or LibreOffice when pagination or visual layout matters."
        };
    }

    private static (string Ascii, string EastAsia, double Size) ParseDefaultFont(JsonElement root)
    {
        var ascii = "Calibri";
        var eastAsia = "等线";
        var size = 11d;
        if (!root.TryGetProperty("font", out var font) || font.ValueKind != JsonValueKind.Object) return (ascii, eastAsia, size);

        if (font.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
            ascii = name.GetString()!.Trim();
        if (font.TryGetProperty("eastAsia", out var east) && east.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(east.GetString()))
            eastAsia = east.GetString()!.Trim();
        if (font.TryGetProperty("size", out var sizeElement))
        {
            if (!sizeElement.TryGetDouble(out size) || size is < 1 or > 400)
                throw new ArgumentException("Default font 'size' must be a number of points from 1 to 400.");
        }
        return (ascii, eastAsia, size);
    }

    /// <summary>Page geometry in twips, shared by the section properties and table sizing.</summary>
    internal sealed record PageSetup(int Width, int Height, int MarginTop, int MarginRight, int MarginBottom, int MarginLeft)
    {
        public int ContentWidth => Math.Max(720, Width - MarginLeft - MarginRight);

        public static PageSetup Parse(JsonElement spec)
        {
            var width = PointsToTwips(595.3);   // A4 portrait
            var height = PointsToTwips(841.9);
            var top = PointsToTwips(72);
            var right = PointsToTwips(72);
            var bottom = PointsToTwips(72);
            var left = PointsToTwips(72);

            if (spec.ValueKind == JsonValueKind.Object)
            {
                if (spec.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.String)
                {
                    (width, height) = (size.GetString() ?? string.Empty).Trim().ToUpperInvariant() switch
                    {
                        "A4" => (PointsToTwips(595.3), PointsToTwips(841.9)),
                        "A5" => (PointsToTwips(419.5), PointsToTwips(595.3)),
                        "LETTER" => (PointsToTwips(612), PointsToTwips(792)),
                        "LEGAL" => (PointsToTwips(612), PointsToTwips(1008)),
                        var other => throw new ArgumentException($"Unsupported page size '{other}'. Use A4, A5, Letter or Legal.")
                    };
                }

                if (spec.TryGetProperty("orientation", out var orientation) && orientation.ValueKind == JsonValueKind.String
                    && orientation.GetString()!.Trim().Equals("landscape", StringComparison.OrdinalIgnoreCase))
                {
                    (width, height) = (height, width);
                }

                if (spec.TryGetProperty("margins", out var margins) && margins.ValueKind == JsonValueKind.Object)
                {
                    top = ReadMargin(margins, "top", top);
                    right = ReadMargin(margins, "right", right);
                    bottom = ReadMargin(margins, "bottom", bottom);
                    left = ReadMargin(margins, "left", left);
                }
            }

            return new PageSetup(width, height, top, right, bottom, left);
        }

        private static int ReadMargin(JsonElement margins, string name, int fallback)
        {
            if (!margins.TryGetProperty(name, out var element)) return fallback;
            if (!element.TryGetDouble(out var points) || points is < 0 or > 400)
                throw new ArgumentException($"Margin '{name}' must be between 0 and 400 points.");
            return PointsToTwips(points);
        }

        public XElement ToSectionProperties() => new(W + "sectPr",
            new XElement(W + "pgSz", new XAttribute(W + "w", Width), new XAttribute(W + "h", Height),
                Height < Width ? new XAttribute(W + "orient", "landscape") : null),
            new XElement(W + "pgMar",
                new XAttribute(W + "top", MarginTop),
                new XAttribute(W + "right", MarginRight),
                new XAttribute(W + "bottom", MarginBottom),
                new XAttribute(W + "left", MarginLeft),
                new XAttribute(W + "header", 720),
                new XAttribute(W + "footer", 720),
                new XAttribute(W + "gutter", 0)));
    }

    internal sealed record MediaPart(string FileName, string ContentType, byte[] Content, string RelationshipId);

    /// <summary>
    /// Hands out the relationship id for an embedded image. Creating a document collects the parts
    /// and writes them with the package; editing one appends them to the package that already exists.
    /// </summary>
    internal interface IMediaAllocator
    {
        string Allocate(ImageMedia image);
    }

    private sealed class CollectingMediaAllocator : IMediaAllocator
    {
        private readonly List<MediaPart> _parts = [];

        public IReadOnlyList<MediaPart> Parts => _parts;

        public string Allocate(ImageMedia image)
        {
            var relationshipId = $"rId{FixedRelationshipCount + _parts.Count + 1}";
            _parts.Add(new MediaPart(image.FileName, image.ContentType, image.Content, relationshipId));
            return relationshipId;
        }
    }

    /// <summary>Turns the block list into body markup, collecting media and warnings along the way.</summary>
    private sealed class BodyBuilder
    {
        private readonly DocxStyleLibrary _styles;
        private readonly PageSetup _page;
        private readonly IMediaAllocator _media;
        private readonly List<XElement> _content = [];
        private readonly List<string> _warnings = [];
        private int _drawingId = 1;
        private int _imageOrdinal;

        public BodyBuilder(DocxStyleLibrary styles, PageSetup page, IMediaAllocator media)
        {
            _styles = styles;
            _page = page;
            _media = media;
        }

        public IReadOnlyList<string> Warnings => _warnings;
        public int ParagraphCount { get; private set; }
        public int TableCount { get; private set; }
        public bool HasFields { get; private set; }

        public XElement Build()
        {
            // Word needs a paragraph after a trailing table, otherwise the table merges with the
            // section break and the document opens with the cursor trapped inside the table.
            if (_content.Count == 0 || _content[^1].Name == W + "tbl") _content.Add(new XElement(W + "p"));
            var body = new XElement(W + "body", _content);
            body.Add(_page.ToSectionProperties());
            return body;
        }

        /// <summary>Builds standalone body content, used when an edit inserts blocks into an existing document.</summary>
        public IReadOnlyList<XElement> BuildFragment(IReadOnlyList<JsonElement> blocks)
        {
            var start = _content.Count;
            foreach (var block in blocks) Append(block);
            var fragment = _content.Skip(start).ToList();
            _content.RemoveRange(start, _content.Count - start);
            return fragment;
        }

        public void Append(JsonElement block)
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                AddParagraph(block.GetString() ?? string.Empty, null, default);
                return;
            }
            if (block.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each block must be an object or a plain string.");

            var type = (block.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()!
                : "paragraph").Trim().ToLowerInvariant();

            switch (type)
            {
                case "paragraph":
                case "text":
                    AddParagraph(ReadText(block), ResolveStyle(block), block);
                    break;
                case "heading":
                    AddHeading(block);
                    break;
                case "title":
                    AddParagraph(ReadText(block), "Title", block);
                    break;
                case "quote":
                    AddParagraph(ReadText(block), "Quote", block);
                    break;
                case "list":
                    AddList(block);
                    break;
                case "table":
                    AddTable(block);
                    break;
                case "image":
                    AddImage(block);
                    break;
                case "pagebreak":
                    AddPageBreak();
                    break;
                case "toc":
                    AddTableOfContents(block);
                    break;
                default:
                    throw new ArgumentException($"Unsupported block type '{type}'. Use paragraph, heading, title, quote, list, table, image, pageBreak or toc.");
            }
        }

        private static string ReadText(JsonElement block) =>
            block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()!
                : string.Empty;

        private string? ResolveStyle(JsonElement block)
        {
            if (!block.TryGetProperty("style", out var style) || style.ValueKind != JsonValueKind.String) return null;
            var name = style.GetString()!.Trim();
            if (name.Length == 0) return null;
            if (!_styles.TryResolveName(name, out var id))
                throw new ArgumentException($"Unknown style '{name}'. Use a built-in style or declare it in the document-level 'styles' array.");
            return id;
        }

        private XElement CreateParagraph(string? styleId, JsonElement block)
        {
            var paragraph = new XElement(W + "p");
            var properties = block.ValueKind == JsonValueKind.Object
                ? DocxStyleLibrary.BuildParagraphProperties(block)
                : new XElement(W + "pPr");
            if (styleId is not null) SetProperty(properties, ParagraphPropertyOrder, Value("pStyle", styleId));
            if (properties.HasElements) paragraph.Add(properties);
            return paragraph;
        }

        private void AddParagraph(string text, string? styleId, JsonElement block)
        {
            var paragraph = CreateParagraph(styleId, block);
            AddRuns(paragraph, text, block);
            _content.Add(paragraph);
            ParagraphCount++;
        }

        /// <summary>
        /// A block carries either plain text or a runs[] array; runs let one paragraph mix formatting
        /// without needing a style for every variation.
        /// </summary>
        private void AddRuns(XElement paragraph, string text, JsonElement block)
        {
            if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
            {
                foreach (var run in runs.EnumerateArray())
                {
                    if (run.ValueKind == JsonValueKind.String)
                    {
                        paragraph.Add(TextRun(run.GetString() ?? string.Empty, null));
                        continue;
                    }
                    if (run.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each item of 'runs' must be a string or an object.");
                    var runText = run.TryGetProperty("text", out var runTextElement) && runTextElement.ValueKind == JsonValueKind.String
                        ? runTextElement.GetString()!
                        : string.Empty;
                    paragraph.Add(TextRun(runText, DocxStyleLibrary.BuildRunProperties(run)));
                }
                return;
            }

            var formatting = block.ValueKind == JsonValueKind.Object && block.TryGetProperty("font", out var font)
                ? DocxStyleLibrary.BuildRunProperties(font)
                : null;
            if (text.Length > 0) paragraph.Add(TextRun(text, formatting));
        }

        private void AddHeading(JsonElement block)
        {
            var level = 1;
            if (block.TryGetProperty("level", out var levelElement))
            {
                if (!levelElement.TryGetInt32(out level) || level is < 1 or > 6)
                    throw new ArgumentException("Heading 'level' must be an integer from 1 to 6.");
            }
            AddParagraph(ReadText(block), $"Heading{level}", block);
        }

        private void AddList(JsonElement block)
        {
            if (!block.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("A list block needs an 'items' array.");

            var ordered = block.TryGetProperty("ordered", out var orderedElement) && orderedElement.ValueKind == JsonValueKind.True;
            var level = 0;
            if (block.TryGetProperty("level", out var levelElement))
            {
                if (!levelElement.TryGetInt32(out level) || level is < 0 or > 8)
                    throw new ArgumentException("List 'level' must be an integer from 0 to 8.");
            }

            foreach (var item in items.EnumerateArray())
            {
                var paragraph = CreateParagraph("ListParagraph", item.ValueKind == JsonValueKind.Object ? item : default);
                var properties = EnsureParagraphProperties(paragraph);
                SetProperty(properties, ParagraphPropertyOrder, new XElement(W + "numPr",
                    Value("ilvl", level),
                    Value("numId", ordered ? OrderedNumberingId : BulletNumberingId)));
                SetProperty(properties, ParagraphPropertyOrder, new XElement(W + "ind",
                    new XAttribute(W + "left", 420 * (level + 1)),
                    new XAttribute(W + "hanging", 420)));

                var text = item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : ReadText(item);
                AddRuns(paragraph, text, item);
                _content.Add(paragraph);
                ParagraphCount++;
            }
        }

        private void AddTable(JsonElement block)
        {
            var rows = new List<List<JsonElement>>();
            if (block.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Array)
                rows.Add(header.EnumerateArray().ToList());
            if (block.TryGetProperty("rows", out var bodyRows) && bodyRows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in bodyRows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Array) throw new ArgumentException("Each table row must be an array of cells.");
                    rows.Add(row.EnumerateArray().ToList());
                }
            }
            if (rows.Count == 0) throw new ArgumentException("A table block needs 'header' and/or 'rows'.");

            var columnCount = rows.Max(row => row.Count);
            if (columnCount is < 1 or > 63) throw new ArgumentException("A table must have between 1 and 63 columns.");
            var widths = ResolveColumnWidths(block, columnCount);
            var hasHeader = block.TryGetProperty("header", out _);

            var table = new XElement(W + "tbl",
                new XElement(W + "tblPr",
                    Value("tblStyle", "TableGrid"),
                    new XElement(W + "tblW", new XAttribute(W + "w", 5000), new XAttribute(W + "type", "pct")),
                    new XElement(W + "tblLook",
                        new XAttribute(W + "val", "04A0"),
                        new XAttribute(W + "firstRow", 1),
                        new XAttribute(W + "lastRow", 0),
                        new XAttribute(W + "firstColumn", 1),
                        new XAttribute(W + "lastColumn", 0),
                        new XAttribute(W + "noHBand", 0),
                        new XAttribute(W + "noVBand", 1))),
                new XElement(W + "tblGrid", widths.Select(width => new XElement(W + "gridCol", new XAttribute(W + "w", width)))));

            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var isHeaderRow = hasHeader && rowIndex == 0;
                var row = new XElement(W + "tr");
                if (isHeaderRow) row.Add(new XElement(W + "trPr", new XElement(W + "tblHeader")));

                for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    var cellProperties = new XElement(W + "tcPr");
                    SetProperty(cellProperties, TableCellPropertyOrder,
                        new XElement(W + "tcW", new XAttribute(W + "w", widths[columnIndex]), new XAttribute(W + "type", "dxa")));

                    var cell = new XElement(W + "tc", cellProperties);
                    var value = columnIndex < rows[rowIndex].Count ? rows[rowIndex][columnIndex] : default;
                    cell.Add(BuildCellParagraph(value, isHeaderRow));
                    row.Add(cell);
                }
                table.Add(row);
            }

            // Two adjacent tables would be merged into one by Word; a spacer paragraph keeps them apart.
            if (_content.Count > 0 && _content[^1].Name == W + "tbl") _content.Add(new XElement(W + "p"));
            _content.Add(table);
            TableCount++;
        }

        private XElement BuildCellParagraph(JsonElement value, bool isHeaderRow)
        {
            var paragraph = new XElement(W + "p");
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Object => ReadText(value),
                _ => string.Empty
            };

            XElement? runProperties = null;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("font", out var font))
                runProperties = DocxStyleLibrary.BuildRunProperties(font);
            if (isHeaderRow)
            {
                runProperties ??= new XElement(W + "rPr");
                SetProperty(runProperties, RunPropertyOrder, new XElement(W + "b"));
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                var properties = DocxStyleLibrary.BuildParagraphProperties(value);
                if (properties.HasElements) paragraph.Add(properties);
            }
            if (text.Length > 0) paragraph.Add(TextRun(text, runProperties));
            return paragraph;
        }

        private int[] ResolveColumnWidths(JsonElement block, int columnCount)
        {
            var available = _page.ContentWidth;
            if (!block.TryGetProperty("widths", out var widths) || widths.ValueKind != JsonValueKind.Array)
                return Enumerable.Repeat(available / columnCount, columnCount).ToArray();

            var values = widths.EnumerateArray().Select(item =>
                item.TryGetDouble(out var value) && value > 0
                    ? value
                    : throw new ArgumentException("Table 'widths' must be positive numbers (relative shares).")).ToList();
            if (values.Count != columnCount)
                throw new ArgumentException($"Table 'widths' has {values.Count} entries but the table has {columnCount} columns.");

            var total = values.Sum();
            return values.Select(value => Math.Max(240, (int)Math.Round(available * value / total))).ToArray();
        }

        private void AddImage(JsonElement block)
        {
            if (!block.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String)
                throw new ArgumentException("An image block needs a 'path'.");
            var path = pathElement.GetString()!;
            var image = ImageMedia.Load(path, ++_imageOrdinal);

            var maximumWidth = _page.ContentWidth / (double)TwipsPerPoint;
            var widthPoints = image.WidthPoints;
            if (block.TryGetProperty("widthPoints", out var widthElement))
            {
                if (!widthElement.TryGetDouble(out widthPoints) || widthPoints is <= 0 or > 2000)
                    throw new ArgumentException("Image 'widthPoints' must be a positive number of points below 2000.");
            }
            if (widthPoints > maximumWidth)
            {
                if (block.TryGetProperty("widthPoints", out _))
                    _warnings.Add($"Image '{Path.GetFileName(path)}' was scaled down to the {maximumWidth:F0}pt text width.");
                widthPoints = maximumWidth;
            }
            var heightPoints = widthPoints * image.PixelHeight / image.PixelWidth;

            var relationshipId = _media.Allocate(image);

            var paragraph = CreateParagraph(ResolveStyle(block), block);
            if (block.TryGetProperty("align", out _) == false)
            {
                var properties = EnsureParagraphProperties(paragraph);
                SetProperty(properties, ParagraphPropertyOrder, Value("jc", "center"));
            }

            var name = block.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()!
                : Path.GetFileNameWithoutExtension(path);
            paragraph.Add(new XElement(W + "r", BuildDrawing(relationshipId, name, PointsToEmus(widthPoints), PointsToEmus(heightPoints))));
            _content.Add(paragraph);
            ParagraphCount++;

            if (block.TryGetProperty("caption", out var caption) && caption.ValueKind == JsonValueKind.String && caption.GetString()!.Length > 0)
            {
                var captionParagraph = new XElement(W + "p");
                var properties = EnsureParagraphProperties(captionParagraph);
                SetProperty(properties, ParagraphPropertyOrder, Value("jc", "center"));

                // Built through SetProperty so the run properties land in CT_RPr order.
                var captionRun = new XElement(W + "rPr");
                SetProperty(captionRun, RunPropertyOrder, new XElement(W + "i"));
                SetProperty(captionRun, RunPropertyOrder, Value("color", "595959"));
                SetProperty(captionRun, RunPropertyOrder, Value("sz", 18));
                SetProperty(captionRun, RunPropertyOrder, Value("szCs", 18));
                captionParagraph.Add(TextRun(caption.GetString()!, captionRun));
                _content.Add(captionParagraph);
                ParagraphCount++;
            }
        }

        private XElement BuildDrawing(string relationshipId, string name, long widthEmus, long heightEmus)
        {
            var id = _drawingId++;
            return new XElement(W + "drawing",
                new XElement(Wp + "inline",
                    new XAttribute("distT", 0), new XAttribute("distB", 0),
                    new XAttribute("distL", 0), new XAttribute("distR", 0),
                    new XElement(Wp + "extent", new XAttribute("cx", widthEmus), new XAttribute("cy", heightEmus)),
                    new XElement(Wp + "effectExtent",
                        new XAttribute("l", 0), new XAttribute("t", 0), new XAttribute("r", 0), new XAttribute("b", 0)),
                    new XElement(Wp + "docPr", new XAttribute("id", id), new XAttribute("name", name)),
                    new XElement(Wp + "cNvGraphicFramePr",
                        new XElement(A + "graphicFrameLocks", new XAttribute("noChangeAspect", 1))),
                    new XElement(A + "graphic",
                        new XElement(A + "graphicData",
                            new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/picture"),
                            new XElement(Pic + "pic",
                                new XElement(Pic + "nvPicPr",
                                    new XElement(Pic + "cNvPr", new XAttribute("id", 0), new XAttribute("name", name)),
                                    new XElement(Pic + "cNvPicPr")),
                                new XElement(Pic + "blipFill",
                                    new XElement(A + "blip", new XAttribute(R + "embed", relationshipId)),
                                    new XElement(A + "stretch", new XElement(A + "fillRect"))),
                                new XElement(Pic + "spPr",
                                    new XElement(A + "xfrm",
                                        new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                                        new XElement(A + "ext", new XAttribute("cx", widthEmus), new XAttribute("cy", heightEmus))),
                                    new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))))))));
        }

        private void AddPageBreak()
        {
            _content.Add(new XElement(W + "p",
                new XElement(W + "r", new XElement(W + "br", new XAttribute(W + "type", "page")))));
            ParagraphCount++;
        }

        /// <summary>
        /// Writes a TOC field. Word computes the entries itself, so the document also asks for a
        /// field update on open; until then the placeholder text is what a reader sees.
        /// </summary>
        private void AddTableOfContents(JsonElement block)
        {
            var levels = block.TryGetProperty("levels", out var levelsElement) && levelsElement.ValueKind == JsonValueKind.String
                ? levelsElement.GetString()!.Trim()
                : "1-3";
            if (!System.Text.RegularExpressions.Regex.IsMatch(levels, @"^[1-9]-[1-9]$"))
                throw new ArgumentException("TOC 'levels' must look like '1-3'.");

            if (block.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String && title.GetString()!.Length > 0)
                AddParagraph(title.GetString()!, "Heading1", default);

            var placeholder = block.TryGetProperty("placeholder", out var placeholderElement) && placeholderElement.ValueKind == JsonValueKind.String
                ? placeholderElement.GetString()!
                : "Right-click and choose Update Field to build the table of contents.";

            _content.Add(new XElement(W + "p",
                new XElement(W + "r", new XElement(W + "fldChar",
                    new XAttribute(W + "fldCharType", "begin"), new XAttribute(W + "dirty", "true"))),
                new XElement(W + "r", new XElement(W + "instrText",
                    new XAttribute(XNamespace.Xml + "space", "preserve"), $" TOC \\o \"{levels}\" \\h \\z \\u ")),
                new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "separate"))),
                new XElement(W + "r", TextElement(placeholder)),
                new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "end")))));
            ParagraphCount++;
            HasFields = true;
        }
    }

    /// <summary>Intrinsic size of an embedded image, read straight from the file header.</summary>
    internal sealed record ImageMedia(string FileName, string ContentType, byte[] Content, int PixelWidth, int PixelHeight, double Dpi)
    {
        public double WidthPoints => PixelWidth * 72d / Dpi;

        public static ImageMedia Load(string path, int ordinal)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var contentType = extension switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => throw new ArgumentException($"Unsupported image type '{extension}'. Use .png, .jpg, .gif or .bmp.")
            };

            if (!File.Exists(path)) throw new FileNotFoundException("Image not found.", path);
            var info = new FileInfo(path);
            if (info.Length > 32L * 1024 * 1024) throw new InvalidDataException("Images larger than 32 MB are not embedded.");
            var content = File.ReadAllBytes(path);

            var (width, height, dpi) = ImageHeaderReader.Measure(content, contentType);
            return new ImageMedia($"image{ordinal}{(extension == ".jpeg" ? ".jpg" : extension)}", contentType, content, width, height, dpi);
        }
    }
}
