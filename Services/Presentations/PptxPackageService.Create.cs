using System;
using System.Collections.Generic;
using System.Globalization;
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
    private const string DefaultTableStyleId = "{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}";

    public object Create(string outputPath, string presentationJson, bool overwrite)
    {
        if (!Path.GetExtension(outputPath).Equals(".pptx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("New presentations must use the .pptx extension.");
        EnsureCanWrite(outputPath, overwrite);

        using var json = JsonDocument.Parse(presentationJson, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("presentationJson must be an object.");
        if (!root.TryGetProperty("slides", out var slideSpecs) || slideSpecs.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("presentationJson must contain a 'slides' array.");
        var slideList = slideSpecs.EnumerateArray().ToList();
        if (slideList.Count is < 1 or > MaxSlides) throw new ArgumentException($"A presentation must contain 1 to {MaxSlides} slides.");

        var size = PresentationSize.Parse(root);
        var theme = ThemeSettings.Parse(root);
        var package = new PresentationBuilder(size, theme);
        for (var index = 0; index < slideList.Count; index++) package.AddSlide(slideList[index], index + 1);

        AtomicWrite(outputPath, overwrite, temporaryPath => package.Write(temporaryPath));
        return new
        {
            outputPath,
            slideCount = slideList.Count,
            shapeCount = package.ShapeCount,
            pictureCount = package.PictureCount,
            tableCount = package.TableCount,
            warnings = package.Warnings,
            nextStep = "Run validate_presentation and inspect_presentation, then inspect every rendered slide before delivery. Static text-fit estimates do not replace visual QA."
        };
    }

    private sealed record PresentationSize(long Width, long Height, string Type)
    {
        public double WidthInches => Width / (double)EmusPerInch;
        public double HeightInches => Height / (double)EmusPerInch;

        public static PresentationSize Parse(JsonElement root)
        {
            var layout = ReadOptionalString(root, "layout")?.ToLowerInvariant() ?? "wide";
            var (width, height, type) = layout switch
            {
                "wide" or "16:9" => (12_192_000L, 6_858_000L, "screen16x9"),
                "standard" or "4:3" => (9_144_000L, 6_858_000L, "screen4x3"),
                "custom" => (0L, 0L, "custom"),
                _ => throw new ArgumentException("'layout' must be wide, standard or custom.")
            };
            if (layout == "custom")
            {
                var widthInches = ReadRequiredDouble(root, "width", 1, 56);
                var heightInches = ReadRequiredDouble(root, "height", 1, 56);
                width = InchesToEmus(widthInches, "width");
                height = InchesToEmus(heightInches, "height");
            }
            return new PresentationSize(width, height, type);
        }
    }

    private sealed record ThemeSettings(string Background, string Primary, string Accent, string Text,
        string Muted, string Font, string EastAsiaFont)
    {
        public static ThemeSettings Parse(JsonElement root)
        {
            var theme = root.TryGetProperty("theme", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
            return new ThemeSettings(
                NormalizeColor(ReadOptionalString(theme, "background"), "FFFFFF"),
                NormalizeColor(ReadOptionalString(theme, "primary"), "203864"),
                NormalizeColor(ReadOptionalString(theme, "accent"), "F05A28"),
                NormalizeColor(ReadOptionalString(theme, "text"), "222222"),
                NormalizeColor(ReadOptionalString(theme, "muted"), "666666"),
                ReadOptionalString(theme, "font") ?? "Arial",
                ReadOptionalString(theme, "eastAsiaFont") ?? "Microsoft YaHei");
        }
    }

    private sealed record MediaPart(string FileName, string ContentType, byte[] Content);
    private sealed record SlideBuild(XElement Document, IReadOnlyList<(string Id, string Type, string Target)> Relationships);

    private sealed class PresentationBuilder
    {
        private readonly PresentationSize _size;
        private readonly ThemeSettings _theme;
        private readonly List<SlideBuild> _slides = [];
        private readonly List<MediaPart> _media = [];
        private readonly List<string> _warnings = [];

        public PresentationBuilder(PresentationSize size, ThemeSettings theme)
        {
            _size = size;
            _theme = theme;
        }

        public int ShapeCount { get; private set; }
        public int PictureCount { get; private set; }
        public int TableCount { get; private set; }
        public IReadOnlyList<string> Warnings => _warnings;

        public void AddSlide(JsonElement spec, int number)
        {
            if (spec.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Slide {number} must be an object.");
            var builder = new SlideBuilder(_size, _theme, _media, _warnings, number);
            _slides.Add(builder.Build(spec));
            ShapeCount += builder.ShapeCount;
            PictureCount += builder.PictureCount;
            TableCount += builder.TableCount;
        }

        public void Write(string path)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
            WriteTextEntry(archive, ContentTypesPart, ContentTypesXml());
            WriteTextEntry(archive, "_rels/.rels", RootRelationshipsXml(PresentationPart, OfficeDocumentRelationship));
            WriteTextEntry(archive, "docProps/core.xml", CorePropertiesXml());
            WriteTextEntry(archive, "docProps/app.xml", AppPropertiesXml(_slides.Count));
            WriteTextEntry(archive, PresentationPart, PresentationXml());
            WriteTextEntry(archive, "ppt/_rels/presentation.xml.rels", PresentationRelationshipsXml());
            WriteTextEntry(archive, "ppt/presProps.xml", PresPropertiesXml);
            WriteTextEntry(archive, "ppt/viewProps.xml", ViewPropertiesXml);
            WriteTextEntry(archive, "ppt/tableStyles.xml", TableStylesXml);
            WriteTextEntry(archive, "ppt/theme/theme1.xml", ThemeXml(_theme));
            WriteTextEntry(archive, "ppt/slideMasters/slideMaster1.xml", SlideMasterXml(_theme));
            WriteTextEntry(archive, "ppt/slideMasters/_rels/slideMaster1.xml.rels", SlideMasterRelationshipsXml);
            WriteTextEntry(archive, "ppt/slideLayouts/slideLayout1.xml", SlideLayoutXml);
            WriteTextEntry(archive, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", SlideLayoutRelationshipsXml);

            for (var index = 0; index < _slides.Count; index++)
            {
                WriteTextEntry(archive, $"ppt/slides/slide{index + 1}.xml", Serialize(_slides[index].Document));
                WriteTextEntry(archive, $"ppt/slides/_rels/slide{index + 1}.xml.rels", RelationshipsXml(_slides[index].Relationships));
            }
            foreach (var media in _media)
            {
                var entry = archive.CreateEntry($"ppt/media/{media.FileName}", CompressionLevel.Optimal);
                using var mediaStream = entry.Open();
                mediaStream.Write(media.Content, 0, media.Content.Length);
            }
        }

        private string PresentationXml()
        {
            var slideIds = Enumerable.Range(0, _slides.Count).Select(index =>
                new XElement(P + "sldId", new XAttribute("id", 256 + index), new XAttribute(R + "id", $"rId{index + 5}")));
            var defaultTextStyle = new XElement(P + "defaultTextStyle",
                new XElement(A + "defPPr",
                    new XElement(A + "defRPr", new XAttribute("lang", "en-US"), new XAttribute("sz", 1800),
                        new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", _theme.Text))),
                        new XElement(A + "latin", new XAttribute("typeface", _theme.Font)),
                        new XElement(A + "ea", new XAttribute("typeface", _theme.EastAsiaFont)))),
                Enumerable.Range(1, 9).Select(level => new XElement(A + $"lvl{level}pPr",
                    new XAttribute("marL", 342_900 * level), new XAttribute("indent", -285_750),
                    new XElement(A + "defRPr", new XAttribute("sz", 1800)))).ToArray());
            var root = new XElement(P + "presentation",
                new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "p", P.NamespaceName),
                new XElement(P + "sldMasterIdLst", new XElement(P + "sldMasterId", new XAttribute("id", 2_147_483_648u), new XAttribute(R + "id", "rId1"))),
                new XElement(P + "sldIdLst", slideIds),
                new XElement(P + "sldSz", new XAttribute("cx", _size.Width), new XAttribute("cy", _size.Height), new XAttribute("type", _size.Type)),
                new XElement(P + "notesSz", new XAttribute("cx", 6_858_000), new XAttribute("cy", 9_144_000)),
                defaultTextStyle);
            return Serialize(root);
        }

        private string PresentationRelationshipsXml()
        {
            var relationships = new List<(string Id, string Type, string Target)>
            {
                ("rId1", SlideMasterRelationship, "slideMasters/slideMaster1.xml"),
                ("rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/presProps", "presProps.xml"),
                ("rId3", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/viewProps", "viewProps.xml"),
                ("rId4", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/tableStyles", "tableStyles.xml")
            };
            relationships.AddRange(Enumerable.Range(0, _slides.Count).Select(index =>
                ($"rId{index + 5}", SlideRelationship, $"slides/slide{index + 1}.xml")));
            return RelationshipsXml(relationships);
        }

        private string ContentTypesXml()
        {
            var defaults = _media.Select(media => (Extension: Path.GetExtension(media.FileName).TrimStart('.').ToLowerInvariant(), media.ContentType))
                .Distinct().OrderBy(item => item.Extension, StringComparer.OrdinalIgnoreCase)
                .Select(item => new XElement(ContentTypesNs + "Default", new XAttribute("Extension", item.Extension), new XAttribute("ContentType", item.ContentType)));
            var root = new XElement(ContentTypesNs + "Types",
                new XElement(ContentTypesNs + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ContentTypesNs + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                defaults,
                Override("/ppt/presentation.xml", PresentationContentType),
                Override("/ppt/presProps.xml", "application/vnd.openxmlformats-officedocument.presentationml.presProps+xml"),
                Override("/ppt/viewProps.xml", "application/vnd.openxmlformats-officedocument.presentationml.viewProps+xml"),
                Override("/ppt/tableStyles.xml", "application/vnd.openxmlformats-officedocument.presentationml.tableStyles+xml"),
                Override("/ppt/theme/theme1.xml", ThemeContentType),
                Override("/ppt/slideMasters/slideMaster1.xml", SlideMasterContentType),
                Override("/ppt/slideLayouts/slideLayout1.xml", SlideLayoutContentType),
                Enumerable.Range(1, _slides.Count).Select(index => Override($"/ppt/slides/slide{index}.xml", SlideContentType)),
                Override("/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml"),
                Override("/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml"));
            return Serialize(root);
        }

        private static XElement Override(string path, string type) => new(ContentTypesNs + "Override", new XAttribute("PartName", path), new XAttribute("ContentType", type));
    }

    private sealed class SlideBuilder
    {
        private readonly PresentationSize _size;
        private readonly ThemeSettings _theme;
        private readonly List<MediaPart> _media;
        private readonly List<string> _warnings;
        private readonly int _slideNumber;
        private readonly List<XElement> _elements = [];
        private readonly List<(string Id, string Type, string Target)> _relationships =
            [("rId1", SlideLayoutRelationship, "../slideLayouts/slideLayout1.xml")];
        private int _shapeId = 1;

        public SlideBuilder(PresentationSize size, ThemeSettings theme, List<MediaPart> media, List<string> warnings, int slideNumber)
        {
            _size = size;
            _theme = theme;
            _media = media;
            _warnings = warnings;
            _slideNumber = slideNumber;
        }

        public int ShapeCount { get; private set; }
        public int PictureCount { get; private set; }
        public int TableCount { get; private set; }

        public SlideBuild Build(JsonElement spec)
        {
            var layout = ReadOptionalString(spec, "layout")?.ToLowerInvariant() ?? "titleandcontent";
            switch (layout)
            {
                case "title": AddTitleLayout(spec); break;
                case "section": AddSectionLayout(spec); break;
                case "titleandcontent" or "title_and_content" or "content": AddContentLayout(spec); break;
                case "blank": break;
                default: throw new ArgumentException($"Slide {_slideNumber}: unsupported layout '{layout}'.");
            }

            if (spec.TryGetProperty("elements", out var elements))
            {
                if (elements.ValueKind != JsonValueKind.Array) throw new ArgumentException($"Slide {_slideNumber}: 'elements' must be an array.");
                var list = elements.EnumerateArray().ToList();
                if (list.Count > MaxElementsPerSlide) throw new ArgumentException($"Slide {_slideNumber} exceeds {MaxElementsPerSlide} custom elements.");
                foreach (var element in list) AddElement(element);
            }

            var background = spec.TryGetProperty("background", out var backgroundElement) && backgroundElement.ValueKind == JsonValueKind.String
                ? NormalizeColor(backgroundElement.GetString(), _theme.Background)
                : _theme.Background;
            var commonSlideData = new XElement(P + "cSld",
                new XAttribute("name", ReadOptionalString(spec, "name") ?? $"Slide {_slideNumber}"),
                new XElement(P + "bg", new XElement(P + "bgPr",
                    new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", background))),
                    new XElement(A + "effectLst"))),
                new XElement(P + "spTree", GroupSkeleton(), GroupTransformSkeleton(), _elements));
            var slide = new XElement(P + "sld",
                new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "p", P.NamespaceName),
                commonSlideData,
                new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping")));
            return new SlideBuild(slide, _relationships);
        }

        private void AddTitleLayout(JsonElement spec)
        {
            var title = ReadOptionalString(spec, "title") ?? string.Empty;
            var subtitle = ReadOptionalString(spec, "subtitle") ?? string.Empty;
            if (title.Length > 0) AddText(title, 0.8, 2.0, _size.WidthInches - 1.6, 1.35,
                new TextStyle(_theme.Font, _theme.EastAsiaFont, 34, _theme.Primary, true, false, "center", "middle", 0.05), "Title");
            if (subtitle.Length > 0) AddText(subtitle, 1.4, 3.5, _size.WidthInches - 2.8, 0.8,
                new TextStyle(_theme.Font, _theme.EastAsiaFont, 20, _theme.Muted, false, false, "center", "middle", 0.04), "Subtitle");
        }

        private void AddSectionLayout(JsonElement spec)
        {
            var title = ReadOptionalString(spec, "title") ?? string.Empty;
            var subtitle = ReadOptionalString(spec, "subtitle") ?? string.Empty;
            if (title.Length > 0) AddText(title, 0.9, 2.25, _size.WidthInches - 1.8, 1.2,
                new TextStyle(_theme.Font, _theme.EastAsiaFont, 30, _theme.Primary, true, false, "left", "middle", 0.05), "Section Title");
            if (subtitle.Length > 0) AddText(subtitle, 0.95, 3.55, _size.WidthInches - 2.0, 0.7,
                new TextStyle(_theme.Font, _theme.EastAsiaFont, 18, _theme.Muted, false, false, "left", "top", 0.03), "Section Subtitle");
        }

        private void AddContentLayout(JsonElement spec)
        {
            var title = ReadOptionalString(spec, "title") ?? string.Empty;
            if (title.Length > 0) AddText(title, 0.65, 0.35, _size.WidthInches - 1.3, 0.8,
                new TextStyle(_theme.Font, _theme.EastAsiaFont, 28, _theme.Primary, true, false, "left", "middle", 0.04), "Title");

            var paragraphs = new List<ParagraphSpec>();
            if (spec.TryGetProperty("bullets", out var bullets))
            {
                if (bullets.ValueKind != JsonValueKind.Array) throw new ArgumentException($"Slide {_slideNumber}: 'bullets' must be an array.");
                foreach (var bullet in bullets.EnumerateArray()) paragraphs.Add(ParseParagraph(bullet, bulletDefault: true));
            }
            else if (ReadOptionalString(spec, "body") is { } body)
            {
                paragraphs.AddRange(body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => new ParagraphSpec(line, 0, false, null)));
            }
            if (paragraphs.Count > 0)
            {
                AddText(paragraphs, 0.8, 1.4, _size.WidthInches - 1.6, _size.HeightInches - 1.9,
                    new TextStyle(_theme.Font, _theme.EastAsiaFont, 18, _theme.Text, false, false, "left", "top", 0.08), "Content");
            }
        }

        private void AddElement(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Slide {_slideNumber}: every element must be an object.");
            var type = (ReadOptionalString(element, "type") ?? "text").ToLowerInvariant();
            switch (type)
            {
                case "text": AddTextElement(element); break;
                case "shape": AddShapeElement(element); break;
                case "image": AddImageElement(element); break;
                case "table": AddTableElement(element); break;
                default: throw new ArgumentException($"Slide {_slideNumber}: unsupported element type '{type}'.");
            }
        }

        private void AddTextElement(JsonElement element)
        {
            var bounds = ReadBoundsSpec(element);
            var style = TextStyle.Parse(element, _theme, 18);
            var paragraphs = ReadParagraphs(element, bulletDefault: false);
            AddText(paragraphs, bounds.X, bounds.Y, bounds.Width, bounds.Height, style,
                ReadOptionalString(element, "name") ?? "Text Box", element);
        }

        private void AddShapeElement(JsonElement element)
        {
            var bounds = ReadBoundsSpec(element);
            var geometry = (ReadOptionalString(element, "shape") ?? "rect").ToLowerInvariant() switch
            {
                "rect" or "rectangle" => "rect",
                "roundrect" or "roundedrectangle" => "roundRect",
                "ellipse" or "circle" => "ellipse",
                "line" => "line",
                var other => throw new ArgumentException($"Slide {_slideNumber}: unsupported shape '{other}'.")
            };
            if (geometry == "line")
            {
                AddConnector(bounds, element);
                return;
            }

            var id = NextShapeId();
            var fill = NormalizeColor(ReadOptionalString(element, "fill"), _theme.Primary);
            var line = NormalizeColor(ReadOptionalString(element, "line"), fill);
            var shapeProperties = ShapeProperties(bounds, geometry, fill, line, ReadOptionalDouble(element, "lineWidth") ?? 1);
            var paragraphs = HasText(element) ? ReadParagraphs(element, bulletDefault: false) : [];
            var shape = new XElement(P + "sp",
                new XElement(P + "nvSpPr",
                    new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", ReadOptionalString(element, "name") ?? $"Shape {id}")),
                    new XElement(P + "cNvSpPr"), new XElement(P + "nvPr")),
                shapeProperties,
                paragraphs.Count == 0 ? null : TextBody(paragraphs, TextStyle.Parse(element, _theme, 18), element));
            _elements.Add(shape);
            ShapeCount++;
        }

        private void AddConnector(BoundsSpec bounds, JsonElement element)
        {
            var id = NextShapeId();
            var line = NormalizeColor(ReadOptionalString(element, "line"), _theme.Primary);
            var width = ReadOptionalDouble(element, "lineWidth") ?? 1.5;
            var connector = new XElement(P + "cxnSp",
                new XElement(P + "nvCxnSpPr",
                    new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", ReadOptionalString(element, "name") ?? $"Line {id}")),
                    new XElement(P + "cNvCxnSpPr"), new XElement(P + "nvPr")),
                new XElement(P + "spPr",
                    Transform(bounds),
                    new XElement(A + "prstGeom", new XAttribute("prst", "line"), new XElement(A + "avLst")),
                    new XElement(A + "ln", new XAttribute("w", PointsToEmus(width)),
                        new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", line))))));
            _elements.Add(connector);
            ShapeCount++;
        }

        private void AddImageElement(JsonElement element)
        {
            var bounds = ReadBoundsSpec(element);
            var path = ReadOptionalString(element, "path") ?? throw new ArgumentException($"Slide {_slideNumber}: image requires 'path'.");
            if (!File.Exists(path)) throw new FileNotFoundException("Presentation image not found.", path);
            var info = new FileInfo(path);
            if (info.Length > MaxEntryBytes) throw new InvalidDataException($"Image is too large: {path}");
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var contentType = extension switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => throw new ArgumentException("Images must be PNG, JPEG, GIF or BMP.")
            };
            var fileName = $"image{_media.Count + 1}{(extension == ".jpeg" ? ".jpg" : extension)}";
            _media.Add(new MediaPart(fileName, contentType, File.ReadAllBytes(path)));
            var relationshipId = $"rId{_relationships.Count + 1}";
            _relationships.Add((relationshipId, ImageRelationship, $"../media/{fileName}"));
            var id = NextShapeId();
            var picture = new XElement(P + "pic",
                new XElement(P + "nvPicPr",
                    new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", ReadOptionalString(element, "name") ?? $"Picture {id}"),
                        new XAttribute("descr", ReadOptionalString(element, "altText") ?? Path.GetFileName(path))),
                    new XElement(P + "cNvPicPr", new XElement(A + "picLocks", new XAttribute("noChangeAspect", 1))),
                    new XElement(P + "nvPr")),
                new XElement(P + "blipFill",
                    new XElement(A + "blip", new XAttribute(R + "embed", relationshipId)),
                    new XElement(A + "stretch", new XElement(A + "fillRect"))),
                new XElement(P + "spPr", Transform(bounds), new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))));
            _elements.Add(picture);
            ShapeCount++;
            PictureCount++;
        }

        private void AddTableElement(JsonElement element)
        {
            var bounds = ReadBoundsSpec(element);
            if (!element.TryGetProperty("rows", out var rowsElement) || rowsElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException($"Slide {_slideNumber}: table requires a 'rows' array.");
            var rows = rowsElement.EnumerateArray().Select(row =>
            {
                if (row.ValueKind != JsonValueKind.Array) throw new ArgumentException($"Slide {_slideNumber}: every table row must be an array.");
                return row.EnumerateArray().Select(CellText).ToList();
            }).ToList();
            if (rows.Count is < 1 or > 200) throw new ArgumentException($"Slide {_slideNumber}: table must contain 1 to 200 rows.");
            var columnCount = rows.Max(row => row.Count);
            if (columnCount is < 1 or > 50) throw new ArgumentException($"Slide {_slideNumber}: table must contain 1 to 50 columns.");
            foreach (var row in rows) while (row.Count < columnCount) row.Add(string.Empty);

            var header = !element.TryGetProperty("header", out var headerElement) || headerElement.ValueKind != JsonValueKind.False;
            var style = TextStyle.Parse(element, _theme, 14);
            var headerFill = NormalizeColor(ReadOptionalString(element, "headerFill"), _theme.Primary);
            var bodyFill = NormalizeColor(ReadOptionalString(element, "bodyFill"), "FFFFFF");
            var columnWidth = InchesToEmus(bounds.Width, "table.width") / columnCount;
            var rowHeight = InchesToEmus(bounds.Height, "table.height") / rows.Count;
            var table = new XElement(A + "tbl",
                new XElement(A + "tblPr", new XAttribute("firstRow", header ? 1 : 0), new XAttribute("bandRow", 1),
                    new XElement(A + "tableStyleId", DefaultTableStyleId)),
                new XElement(A + "tblGrid", Enumerable.Range(0, columnCount).Select(_ => new XElement(A + "gridCol", new XAttribute("w", columnWidth)))),
                rows.Select((row, rowIndex) => new XElement(A + "tr", new XAttribute("h", rowHeight),
                    row.Select(cell => TableCell(cell, style with
                    {
                        Bold = header && rowIndex == 0 || style.Bold,
                        Color = header && rowIndex == 0 ? "FFFFFF" : style.Color
                    }, header && rowIndex == 0 ? headerFill : bodyFill)))));
            var id = NextShapeId();
            var frame = new XElement(P + "graphicFrame",
                new XElement(P + "nvGraphicFramePr",
                    new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", ReadOptionalString(element, "name") ?? $"Table {id}")),
                    new XElement(P + "cNvGraphicFramePr", new XElement(A + "graphicFrameLocks", new XAttribute("noGrp", 1))),
                    new XElement(P + "nvPr")),
                new XElement(P + "xfrm",
                    new XElement(A + "off", new XAttribute("x", InchesToEmus(bounds.X, "table.x")), new XAttribute("y", InchesToEmus(bounds.Y, "table.y"))),
                    new XElement(A + "ext", new XAttribute("cx", InchesToEmus(bounds.Width, "table.width")), new XAttribute("cy", InchesToEmus(bounds.Height, "table.height")))),
                new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/table"), table)));
            _elements.Add(frame);
            ShapeCount++;
            TableCount++;
        }

        // CT_TableCellProperties 是有序 sequence：四条边线在前，填充属于其后的 EG_FillProperties。
        // 把 solidFill 写在 lnL 之前会让 PowerPoint 判定单元格属性乱序并提示修复文件。
        private static XElement TableCell(string text, TextStyle style, string fill) => new(A + "tc",
            new XElement(A + "txBody",
                new XElement(A + "bodyPr", new XAttribute("lIns", 45_720), new XAttribute("rIns", 45_720), new XAttribute("tIns", 22_860), new XAttribute("bIns", 22_860)),
                new XElement(A + "lstStyle"), ParagraphElement(new ParagraphSpec(text, 0, false, null), style)),
            new XElement(A + "tcPr",
                new XElement(A + "lnL", new XAttribute("w", 6_350), new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", "D9D9D9")))),
                new XElement(A + "lnR", new XAttribute("w", 6_350), new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", "D9D9D9")))),
                new XElement(A + "lnT", new XAttribute("w", 6_350), new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", "D9D9D9")))),
                new XElement(A + "lnB", new XAttribute("w", 6_350), new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", "D9D9D9")))),
                new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", fill)))));

        private void AddText(string text, double x, double y, double width, double height, TextStyle style, string name) =>
            AddText(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => new ParagraphSpec(line, 0, false, null)).ToList(),
                x, y, width, height, style, name, default);

        private void AddText(IReadOnlyList<ParagraphSpec> paragraphs, double x, double y, double width, double height,
            TextStyle style, string name, JsonElement element = default)
        {
            var id = NextShapeId();
            var bounds = new BoundsSpec(x, y, width, height);
            var fill = ReadOptionalString(element, "fill");
            var line = ReadOptionalString(element, "line");
            var shape = new XElement(P + "sp",
                new XElement(P + "nvSpPr",
                    new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
                    new XElement(P + "cNvSpPr", new XAttribute("txBox", 1)),
                    new XElement(P + "nvPr")),
                new XElement(P + "spPr",
                    Transform(bounds),
                    new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")),
                    fill is null ? new XElement(A + "noFill") : new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", NormalizeColor(fill, _theme.Background)))),
                    line is null ? new XElement(A + "ln", new XElement(A + "noFill")) : new XElement(A + "ln", new XAttribute("w", PointsToEmus(ReadOptionalDouble(element, "lineWidth") ?? 1)),
                        new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", NormalizeColor(line, _theme.Primary)))))),
                TextBody(paragraphs, style, element));
            _elements.Add(shape);
            ShapeCount++;
        }

        public static XElement TextBody(IReadOnlyList<ParagraphSpec> paragraphs, TextStyle style, JsonElement element)
        {
            var margin = style.Margin;
            var bodyProperties = new XElement(A + "bodyPr",
                new XAttribute("wrap", "square"),
                new XAttribute("lIns", InchesToEmus(margin, "margin")),
                new XAttribute("rIns", InchesToEmus(margin, "margin")),
                new XAttribute("tIns", InchesToEmus(margin, "margin")),
                new XAttribute("bIns", InchesToEmus(margin, "margin")),
                new XAttribute("anchor", style.Valign switch { "middle" or "center" => "ctr", "bottom" => "b", _ => "t" }),
                // noAutofit 而非 spAutoFit：spAutoFit 授权 PowerPoint 按文字重算形状高度，会推翻调用方
                // 给定的 height 并让相邻元素错位，同时 PresentationTextLayoutAnalyzer 会跳过所有
                // spAutoFit 形状——两者叠加的结果是自产文稿完全不受文字溢出检查。
                new XElement(A + "noAutofit"));
            return new XElement(P + "txBody", bodyProperties, new XElement(A + "lstStyle"),
                paragraphs.Count == 0 ? [ParagraphElement(new ParagraphSpec(string.Empty, 0, false, null), style)]
                    : paragraphs.Select(paragraph => ParagraphElement(paragraph, paragraph.Style ?? style)).ToArray());
        }

        private static XElement ParagraphElement(ParagraphSpec paragraph, TextStyle style)
        {
            var level = Math.Clamp(paragraph.Level, 0, 8);
            var properties = new XElement(A + "pPr", new XAttribute("algn", AlignValue(style.Align)),
                paragraph.Bullet ? new XAttribute("lvl", level) : null,
                paragraph.Bullet ? new XAttribute("marL", 342_900 * (level + 1)) : null,
                paragraph.Bullet ? new XAttribute("indent", -285_750) : null,
                paragraph.Bullet ? new XElement(A + "buChar", new XAttribute("char", "•")) : new XElement(A + "buNone"));
            var textElement = new XElement(A + "t", paragraph.Text);
            if (paragraph.Text.Length != paragraph.Text.Trim().Length) textElement.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            return new XElement(A + "p", properties,
                paragraph.Text.Length == 0 ? null : new XElement(A + "r", RunProperties(style), textElement),
                new XElement(A + "endParaRPr", new XAttribute("lang", "en-US"), new XAttribute("sz", PointsToHundredths(style.Size, "font.size"))));
        }

        private static XElement RunProperties(TextStyle style) => new(A + "rPr",
            new XAttribute("lang", "en-US"), new XAttribute("sz", PointsToHundredths(style.Size, "font.size")),
            style.Bold ? new XAttribute("b", 1) : null, style.Italic ? new XAttribute("i", 1) : null,
            new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", style.Color))),
            new XElement(A + "latin", new XAttribute("typeface", style.Font)),
            new XElement(A + "ea", new XAttribute("typeface", style.EastAsiaFont)));

        private static XElement ShapeProperties(BoundsSpec bounds, string geometry, string fill, string line, double lineWidth) =>
            new(P + "spPr", Transform(bounds),
                new XElement(A + "prstGeom", new XAttribute("prst", geometry), new XElement(A + "avLst")),
                new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", fill))),
                new XElement(A + "ln", new XAttribute("w", PointsToEmus(lineWidth)),
                    new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", line)))));

        public static XElement Transform(BoundsSpec bounds) => new(A + "xfrm",
            new XElement(A + "off", new XAttribute("x", InchesToEmus(bounds.X, "x")), new XAttribute("y", InchesToEmus(bounds.Y, "y"))),
            new XElement(A + "ext", new XAttribute("cx", InchesToEmus(bounds.Width, "width")), new XAttribute("cy", InchesToEmus(bounds.Height, "height"))));

        private static XElement GroupSkeleton() => new(P + "nvGrpSpPr",
            new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", string.Empty)),
            new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr"));

        private static XElement GroupTransformSkeleton() => new(P + "grpSpPr",
            new XElement(A + "xfrm",
                new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0)),
                new XElement(A + "chOff", new XAttribute("x", 0), new XAttribute("y", 0)),
                new XElement(A + "chExt", new XAttribute("cx", 0), new XAttribute("cy", 0))));

        private int NextShapeId()
        {
            if (_shapeId >= MaxElementsPerSlide + 1) throw new ArgumentException($"Slide {_slideNumber} exceeds {MaxElementsPerSlide} elements.");
            return ++_shapeId;
        }

        public static List<ParagraphSpec> ReadParagraphs(JsonElement element, bool bulletDefault)
        {
            if (element.TryGetProperty("paragraphs", out var paragraphs))
            {
                if (paragraphs.ValueKind != JsonValueKind.Array) throw new ArgumentException("'paragraphs' must be an array.");
                return paragraphs.EnumerateArray().Select(item => ParseParagraph(item, bulletDefault)).ToList();
            }
            var text = ReadOptionalString(element, "text") ?? string.Empty;
            return text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Select(line => new ParagraphSpec(line, 0, bulletDefault, null)).ToList();
        }

        private static ParagraphSpec ParseParagraph(JsonElement item, bool bulletDefault)
        {
            if (item.ValueKind == JsonValueKind.String) return new ParagraphSpec(item.GetString() ?? string.Empty, 0, bulletDefault, null);
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each paragraph/bullet must be a string or object.");
            var text = ReadOptionalString(item, "text") ?? string.Empty;
            var level = item.TryGetProperty("level", out var levelElement) && levelElement.TryGetInt32(out var parsed) ? parsed : 0;
            if (level is < 0 or > 8) throw new ArgumentException("Paragraph level must be from 0 to 8.");
            var bullet = item.TryGetProperty("bullet", out var bulletElement) ? bulletElement.ValueKind == JsonValueKind.True : bulletDefault;
            return new ParagraphSpec(text, level, bullet, null);
        }

        private static bool HasText(JsonElement element) => ReadOptionalString(element, "text") is not null || element.TryGetProperty("paragraphs", out _);
        private static string CellText(JsonElement cell) => cell.ValueKind switch
        {
            JsonValueKind.String => cell.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => cell.ToString(),
            JsonValueKind.Null => string.Empty,
            _ => throw new ArgumentException("Table cells must be strings, numbers, booleans or null.")
        };
    }

    private sealed record ParagraphSpec(string Text, int Level, bool Bullet, TextStyle? Style);
    private sealed record BoundsSpec(double X, double Y, double Width, double Height);
    private sealed record TextStyle(string Font, string EastAsiaFont, double Size, string Color, bool Bold, bool Italic,
        string Align, string Valign, double Margin)
    {
        public static TextStyle Parse(JsonElement element, ThemeSettings theme, double defaultSize)
        {
            var fontObject = element.TryGetProperty("font", out var font) && font.ValueKind == JsonValueKind.Object ? font : default;
            var size = ReadOptionalDouble(fontObject, "size") ?? ReadOptionalDouble(element, "fontSize") ?? defaultSize;
            _ = PointsToHundredths(size, "font.size");
            var color = NormalizeColor(ReadOptionalString(fontObject, "color") ?? ReadOptionalString(element, "color"), theme.Text);
            var align = (ReadOptionalString(element, "align") ?? "left").ToLowerInvariant();
            if (align is not ("left" or "center" or "right" or "justify")) throw new ArgumentException("Text align must be left, center, right or justify.");
            var valign = (ReadOptionalString(element, "valign") ?? "top").ToLowerInvariant();
            if (valign is not ("top" or "middle" or "center" or "bottom")) throw new ArgumentException("Text valign must be top, middle or bottom.");
            var margin = ReadOptionalDouble(element, "margin") ?? 0.05;
            if (margin is < 0 or > 1) throw new ArgumentException("Text margin must be from 0 to 1 inch.");
            return new TextStyle(
                ReadOptionalString(fontObject, "name") ?? theme.Font,
                ReadOptionalString(fontObject, "eastAsia") ?? theme.EastAsiaFont,
                size, color,
                fontObject.ValueKind == JsonValueKind.Object && fontObject.TryGetProperty("bold", out var bold) && bold.ValueKind == JsonValueKind.True,
                fontObject.ValueKind == JsonValueKind.Object && fontObject.TryGetProperty("italic", out var italic) && italic.ValueKind == JsonValueKind.True,
                align, valign, margin);
        }
    }

    private static BoundsSpec ReadBoundsSpec(JsonElement element)
    {
        var x = ReadRequiredDouble(element, "x", -56, 56);
        var y = ReadRequiredDouble(element, "y", -56, 56);
        var width = ReadRequiredDouble(element, "width", 0.01, 56);
        var height = ReadRequiredDouble(element, "height", 0.01, 56);
        return new BoundsSpec(x, y, width, height);
    }

    private static string AlignValue(string align) => align switch { "center" => "ctr", "right" => "r", "justify" => "just", _ => "l" };
    private static long PointsToEmus(double points) => checked((long)Math.Round(points * EmusPerPoint, MidpointRounding.AwayFromZero));

    internal static string? ReadOptionalString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"'{property}' must be a string.");
        return value.GetString();
    }

    internal static double? ReadOptionalDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number)) throw new ArgumentException($"'{property}' must be a finite number.");
        return number;
    }

    private static double ReadRequiredDouble(JsonElement element, string property, double minimum, double maximum)
    {
        var number = ReadOptionalDouble(element, property) ?? throw new ArgumentException($"Missing required number '{property}'.");
        if (number < minimum || number > maximum) throw new ArgumentException($"'{property}' must be between {minimum.ToString(CultureInfo.InvariantCulture)} and {maximum.ToString(CultureInfo.InvariantCulture)}.");
        return number;
    }

    private static string RelationshipsXml(IEnumerable<(string Id, string Type, string Target)> relationships)
    {
        var root = new XElement(PackageRelationshipNs + "Relationships", relationships.Select(item =>
            new XElement(PackageRelationshipNs + "Relationship", new XAttribute("Id", item.Id), new XAttribute("Type", item.Type), new XAttribute("Target", item.Target))));
        return Serialize(root);
    }

    private static string AppPropertiesXml(int slideCount) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
        "<Application>AthenaAgent</Application><PresentationFormat>On-screen Show (16:9)</PresentationFormat>" +
        $"<Slides>{slideCount}</Slides><Notes>0</Notes><HiddenSlides>0</HiddenSlides><MMClips>0</MMClips><ScaleCrop>false</ScaleCrop>" +
        "<Company></Company><LinksUpToDate>false</LinksUpToDate><SharedDoc>false</SharedDoc><HyperlinksChanged>false</HyperlinksChanged><AppVersion>1.0</AppVersion></Properties>";

    private static string ThemeXml(ThemeSettings theme) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><a:theme xmlns:a=\"{A}\" name=\"Athena Theme\"><a:themeElements><a:clrScheme name=\"Athena\"><a:dk1><a:srgbClr val=\"000000\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"{theme.Primary}\"/></a:dk2><a:lt2><a:srgbClr val=\"{theme.Background}\"/></a:lt2><a:accent1><a:srgbClr val=\"{theme.Primary}\"/></a:accent1><a:accent2><a:srgbClr val=\"{theme.Accent}\"/></a:accent2><a:accent3><a:srgbClr val=\"70AD47\"/></a:accent3><a:accent4><a:srgbClr val=\"5B9BD5\"/></a:accent4><a:accent5><a:srgbClr val=\"A5A5A5\"/></a:accent5><a:accent6><a:srgbClr val=\"FFC000\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme><a:fontScheme name=\"Athena\"><a:majorFont><a:latin typeface=\"{XmlEscape(theme.Font)}\"/><a:ea typeface=\"{XmlEscape(theme.EastAsiaFont)}\"/><a:cs typeface=\"{XmlEscape(theme.Font)}\"/></a:majorFont><a:minorFont><a:latin typeface=\"{XmlEscape(theme.Font)}\"/><a:ea typeface=\"{XmlEscape(theme.EastAsiaFont)}\"/><a:cs typeface=\"{XmlEscape(theme.Font)}\"/></a:minorFont></a:fontScheme><a:fmtScheme name=\"Athena\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"accent1\"/></a:solidFill><a:solidFill><a:schemeClr val=\"accent2\"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"lt1\"/></a:solidFill><a:solidFill><a:schemeClr val=\"lt2\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";

    private static string SlideMasterXml(ThemeSettings theme) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:sldMaster xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:cSld name=\"Athena Blank Master\"><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree></p:cSld><p:clrMap accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" bg1=\"lt1\" bg2=\"lt2\" folHlink=\"folHlink\" hlink=\"hlink\" tx1=\"dk1\" tx2=\"dk2\"/><p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"3200\" b=\"1\"><a:latin typeface=\"{XmlEscape(theme.Font)}\"/><a:ea typeface=\"{XmlEscape(theme.EastAsiaFont)}\"/></a:defRPr></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr marL=\"342900\" indent=\"-285750\"><a:defRPr sz=\"1800\"/></a:lvl1pPr></p:bodyStyle><p:otherStyle><a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr></p:otherStyle></p:txStyles></p:sldMaster>";

    private static string SlideLayoutXml =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:sldLayout xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\" type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\"><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static string SlideMasterRelationshipsXml => RelationshipsXml([
        ("rId1", SlideLayoutRelationship, "../slideLayouts/slideLayout1.xml"), ("rId2", ThemeRelationship, "../theme/theme1.xml")]);
    private static string SlideLayoutRelationshipsXml => RelationshipsXml([("rId1", SlideMasterRelationship, "../slideMasters/slideMaster1.xml")]);
    private static string PresPropertiesXml => $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:presentationPr xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"/>";
    private static string ViewPropertiesXml => $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:viewPr xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"><p:normalViewPr/><p:slideViewPr><p:cSldViewPr><p:cViewPr varScale=\"1\"><p:scale><a:sx n=\"100\" d=\"100\"/><a:sy n=\"100\" d=\"100\"/></p:scale><p:origin x=\"0\" y=\"0\"/></p:cViewPr><p:guideLst/></p:cSldViewPr></p:slideViewPr><p:notesTextViewPr><p:cViewPr><p:scale><a:sx n=\"100\" d=\"100\"/><a:sy n=\"100\" d=\"100\"/></p:scale><p:origin x=\"0\" y=\"0\"/></p:cViewPr></p:notesTextViewPr><p:gridSpacing cx=\"72008\" cy=\"72008\"/></p:viewPr>";
    private static string TableStylesXml => $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><a:tblStyleLst xmlns:a=\"{A}\" def=\"{DefaultTableStyleId}\"/>";
}
