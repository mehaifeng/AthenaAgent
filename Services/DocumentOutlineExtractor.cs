using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Athena.UI.Services.Interfaces;
using UglyToad.PdfPig;

namespace Athena.UI.Services;

/// <summary>
/// Local, read-only document/code outline extraction. The extractor is deliberately deterministic:
/// it never invokes a model or uploads content. Legacy binary Office formats are explicitly routed
/// to the configured document parser by <c>FileSystemFunctions</c> instead.
/// </summary>
public static class DocumentOutlineExtractor
{
    public const int MaxEntries = 300;
    private const int MaxPdfPagesForHeadingHeuristics = 200;
    private const int MaxXmlCharacters = 20_000_000;

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".java", ".kt", ".kts", ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hxx",
        ".m", ".mm", ".swift", ".go", ".rs", ".dart", ".scala", ".fs", ".fsx", ".vb",
        ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".vue", ".svelte", ".astro",
        ".py", ".pyi", ".rb", ".php", ".pl", ".pm", ".sh", ".bash", ".zsh", ".ps1",
        ".sql", ".graphql", ".gql", ".proto"
    };

    private static readonly HashSet<string> LegacyRemoteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".ppt", ".xls"
    };

    public static bool RequiresRemoteParser(string path) =>
        LegacyRemoteExtensions.Contains(Path.GetExtension(path));

    public static async Task<DocumentOutline> ExtractLocalAsync(string fullPath)
    {
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (RequiresRemoteParser(fullPath))
        {
            throw new NotSupportedException(
                $"Legacy binary Office format '{extension}' requires the configured remote document parser.");
        }

        return extension switch
        {
            ".md" or ".markdown" or ".mdx" => FromMarkdown(
                await File.ReadAllTextAsync(fullPath).ConfigureAwait(false), extension),
            ".docx" => ExtractDocx(fullPath),
            ".pdf" => ExtractPdf(fullPath),
            ".pptx" => ExtractPptx(fullPath),
            ".xlsx" => ExtractXlsx(fullPath),
            ".json" or ".jsonc" => ExtractJson(
                await File.ReadAllTextAsync(fullPath).ConfigureAwait(false), extension),
            ".yaml" or ".yml" => ExtractYaml(
                await File.ReadAllLinesAsync(fullPath).ConfigureAwait(false), extension),
            ".html" or ".htm" or ".xml" or ".xaml" or ".axaml" => ExtractMarkup(
                await File.ReadAllLinesAsync(fullPath).ConfigureAwait(false), extension),
            ".css" or ".scss" or ".sass" or ".less" => ExtractStylesheet(
                await File.ReadAllLinesAsync(fullPath).ConfigureAwait(false), extension),
            _ when CodeExtensions.Contains(extension) => ExtractCode(
                await File.ReadAllLinesAsync(fullPath).ConfigureAwait(false), extension),
            _ => await ExtractUnknownAsync(fullPath, extension).ConfigureAwait(false)
        };
    }

    private static async Task<DocumentOutline> ExtractUnknownAsync(string fullPath, string extension)
    {
        if (await IsProbablyBinaryAsync(fullPath).ConfigureAwait(false))
        {
            var outline = NewOutline("UnsupportedBinary", extension, "local");
            outline.Warnings.Add("The file appears to be binary and has no registered local outline extractor.");
            return outline;
        }
        return ExtractPlainText(await File.ReadAllLinesAsync(fullPath).ConfigureAwait(false), extension);
    }

    private static async Task<bool> IsProbablyBinaryAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
        if (read == 0) return false;
        var zeroBytes = 0;
        var suspiciousControls = 0;
        for (var i = 0; i < read; i++)
        {
            var value = buffer[i];
            if (value == 0) zeroBytes++;
            else if (value < 0x09 || value is > 0x0D and < 0x20) suspiciousControls++;
        }
        return zeroBytes > 0 || suspiciousControls > read / 20;
    }

    public static DocumentOutline FromMarkdown(string markdown, string format = ".md", string source = "local")
    {
        var outline = NewOutline("Markdown", format, source);
        var lines = SplitLines(markdown);
        for (var i = 0; i < lines.Length; i++)
        {
            var atx = Regex.Match(lines[i], @"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$");
            if (atx.Success)
            {
                Add(outline, atx.Groups[2].Value.Trim(), "heading", atx.Groups[1].Value.Length, i + 1);
                continue;
            }

            if (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                var setext = Regex.Match(lines[i + 1], @"^\s*(=+|-+)\s*$");
                if (setext.Success)
                {
                    Add(outline, lines[i].Trim(), "heading", setext.Groups[1].Value[0] == '=' ? 1 : 2, i + 1);
                    i++;
                }
            }
        }

        Finalize(outline);
        return outline;
    }

    private static DocumentOutline ExtractDocx(string path)
    {
        var outline = NewOutline("Word", ".docx", "local_openxml");
        using var archive = ZipFile.OpenRead(path);
        var documentEntry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("DOCX package does not contain word/document.xml.");
        var styleLevels = ReadDocxStyleLevels(archive);
        var document = LoadXml(documentEntry);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var paragraphIndex = 0;
        string? firstParagraph = null;
        foreach (var paragraph in document.Descendants(w + "p"))
        {
            paragraphIndex++;
            var text = string.Concat(paragraph.Descendants(w + "t").Select(t => t.Value)).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            firstParagraph ??= text;

            var styleId = paragraph.Element(w + "pPr")?.Element(w + "pStyle")?
                .Attribute(w + "val")?.Value;
            var level = ResolveDocxHeadingLevel(styleId, styleLevels);
            if (level.HasValue)
            {
                Add(outline, text, level.Value == 1 && IsTitleStyle(styleId) ? "title" : "heading",
                    level.Value, paragraphIndex);
            }
            else if (TryGetNumberedHeadingLevel(text, out var numberedLevel))
            {
                Add(outline, text, "heading", numberedLevel, paragraphIndex);
            }
        }

        if (outline.Entries.Count == 0 && firstParagraph != null)
        {
            Add(outline, firstParagraph, "document_lead", 1, 1);
            outline.Warnings.Add("No Word heading styles were found; returned the first paragraph as a document lead.");
        }

        Finalize(outline);
        return outline;
    }

    private static Dictionary<string, int> ReadDocxStyleLevels(ZipArchive archive)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stylesEntry = archive.GetEntry("word/styles.xml");
        if (stylesEntry == null) return result;

        var styles = LoadXml(stylesEntry);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        foreach (var style in styles.Descendants(w + "style"))
        {
            var id = style.Attribute(w + "styleId")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var outlineLevel = style.Descendants(w + "outlineLvl").FirstOrDefault()?
                .Attribute(w + "val")?.Value;
            if (int.TryParse(outlineLevel, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zeroBased))
            {
                result[id] = Math.Clamp(zeroBased + 1, 1, 9);
                continue;
            }

            var name = style.Element(w + "name")?.Attribute(w + "val")?.Value ?? id;
            var match = Regex.Match(name, @"(?:heading|标题)\s*([1-9])", RegexOptions.IgnoreCase);
            if (match.Success) result[id] = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            else if (name.Equals("Title", StringComparison.OrdinalIgnoreCase)) result[id] = 1;
        }
        return result;
    }

    private static int? ResolveDocxHeadingLevel(string? styleId, IReadOnlyDictionary<string, int> styles)
    {
        if (string.IsNullOrWhiteSpace(styleId)) return null;
        if (styles.TryGetValue(styleId, out var level)) return level;
        var match = Regex.Match(styleId, @"(?:heading|标题)([1-9])", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static bool IsTitleStyle(string? styleId) =>
        string.Equals(styleId, "Title", StringComparison.OrdinalIgnoreCase);

    private static DocumentOutline ExtractPdf(string path)
    {
        var outline = NewOutline("PDF", ".pdf", "local_pdfpig");
        using var document = PdfDocument.Open(path);
        outline.PageCount = document.NumberOfPages;

        if (!string.IsNullOrWhiteSpace(document.Information?.Title))
        {
            Add(outline, document.Information.Title.Trim(), "title", 1, pageNumber: 1);
        }

        if (document.TryGetBookmarks(out var bookmarks))
        {
            AppendBookmarksReflectively(bookmarks, outline);
        }

        if (outline.Entries.Count <= 1)
        {
            AppendPdfHeadingHeuristics(document, outline);
        }

        if (outline.Entries.Count == 0)
        {
            outline.Warnings.Add("No PDF bookmarks or extractable heading candidates were found; the PDF may be scanned or untagged.");
        }

        Finalize(outline);
        return outline;
    }

    private static void AppendBookmarksReflectively(object bookmarks, DocumentOutline outline)
    {
        var roots = GetEnumerableProperty(bookmarks, "Roots", "Children", "Items", "Bookmarks");
        if (roots == null) return;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots) AppendBookmarkNode(root, 1, outline, visited);
    }

    private static void AppendBookmarkNode(object? node, int level, DocumentOutline outline, HashSet<object> visited)
    {
        if (node == null || !visited.Add(node) || outline.Entries.Count >= MaxEntries) return;
        var title = GetPropertyValue(node, "Title", "Name")?.ToString()?.Trim();
        var pageNumber = ToNullableInt(GetPropertyValue(node, "PageNumber", "Page", "PageIndex"));
        if (pageNumber == 0) pageNumber = 1;
        if (!string.IsNullOrWhiteSpace(title))
            Add(outline, title, "bookmark", Math.Clamp(level, 1, 9), pageNumber: pageNumber);

        var children = GetEnumerableProperty(node, "Children", "Items", "Bookmarks");
        if (children == null) return;
        foreach (var child in children) AppendBookmarkNode(child, level + 1, outline, visited);
    }

    private static void AppendPdfHeadingHeuristics(PdfDocument document, DocumentOutline outline)
    {
        var pageLimit = Math.Min(document.NumberOfPages, MaxPdfPagesForHeadingHeuristics);
        if (document.NumberOfPages > pageLimit)
        {
            outline.IsPartial = true;
            outline.Warnings.Add($"Heading heuristics inspected the first {pageLimit} of {document.NumberOfPages} pages.");
        }

        for (var pageNumber = 1; pageNumber <= pageLimit && outline.Entries.Count < MaxEntries; pageNumber++)
        {
            var letters = document.GetPage(pageNumber).Letters
                .Where(letter => !string.IsNullOrEmpty(letter.Value))
                .ToList();
            var fontSizes = letters.Select(letter => Convert.ToDouble(letter.FontSize, CultureInfo.InvariantCulture))
                .Where(size => size > 0).OrderBy(size => size).ToArray();
            if (fontSizes.Length == 0) continue;
            var median = fontSizes[fontSizes.Length / 2];

            var lines = letters
                .GroupBy(letter => Math.Round(Convert.ToDouble(letter.StartBaseLine.Y, CultureInfo.InvariantCulture) / 2d) * 2d)
                .OrderByDescending(group => group.Key)
                .Select(group => BuildPdfLine(group))
                .Where(line => line.Text.Length is >= 3 and <= 180)
                .Where(line => line.FontSize >= median * 1.18)
                .Where(line => LooksLikeHeading(line.Text))
                .ToList();

            foreach (var line in lines)
            {
                var ratio = line.FontSize / Math.Max(median, 0.1);
                var level = ratio >= 1.8 ? 1 : ratio >= 1.45 ? 2 : 3;
                Add(outline, line.Text, "visual_heading", level, pageNumber: pageNumber);
            }
        }
    }

    private static PdfLine BuildPdfLine(IEnumerable<UglyToad.PdfPig.Content.Letter> letters)
    {
        var ordered = letters.OrderBy(letter => Convert.ToDouble(letter.StartBaseLine.X, CultureInfo.InvariantCulture)).ToList();
        var builder = new StringBuilder();
        double? previousEnd = null;
        var maxFont = 0d;
        foreach (var letter in ordered)
        {
            var start = Convert.ToDouble(letter.StartBaseLine.X, CultureInfo.InvariantCulture);
            var end = Convert.ToDouble(letter.EndBaseLine.X, CultureInfo.InvariantCulture);
            var font = Convert.ToDouble(letter.FontSize, CultureInfo.InvariantCulture);
            if (previousEnd.HasValue && start - previousEnd.Value > Math.Max(1.5, font * 0.22)
                && builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
                builder.Append(' ');
            builder.Append(letter.Value);
            previousEnd = end;
            maxFont = Math.Max(maxFont, font);
        }
        return new PdfLine(Regex.Replace(builder.ToString(), @"\s+", " ").Trim(), maxFont);
    }

    private static DocumentOutline ExtractPptx(string path)
    {
        var outline = NewOutline("PowerPoint", ".pptx", "local_openxml");
        using var archive = ZipFile.OpenRead(path);
        var slides = archive.Entries
            .Where(entry => Regex.IsMatch(entry.FullName, @"^ppt/slides/slide\d+\.xml$", RegexOptions.IgnoreCase))
            .OrderBy(entry => ExtractTrailingNumber(entry.Name))
            .ToList();
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        for (var i = 0; i < slides.Count; i++)
        {
            var xml = LoadXml(slides[i]);
            var title = string.Join(" ", xml.Descendants(a + "t").Select(node => node.Value.Trim())
                .Where(value => value.Length > 0).Take(3));
            if (!string.IsNullOrWhiteSpace(title)) Add(outline, title, "slide", 1, pageNumber: i + 1);
        }
        outline.PageCount = slides.Count;
        Finalize(outline);
        return outline;
    }

    private static DocumentOutline ExtractXlsx(string path)
    {
        var outline = NewOutline("Excel", ".xlsx", "local_openxml");
        using var archive = ZipFile.OpenRead(path);
        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("XLSX package does not contain xl/workbook.xml.");
        var workbook = LoadXml(workbookEntry);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var index = 0;
        foreach (var sheet in workbook.Descendants(main + "sheet"))
        {
            index++;
            var name = sheet.Attribute("name")?.Value;
            if (!string.IsNullOrWhiteSpace(name)) Add(outline, name, "worksheet", 1, pageNumber: index);
        }
        Finalize(outline);
        return outline;
    }

    private static DocumentOutline ExtractJson(string text, string extension)
    {
        var outline = NewOutline("JSON", extension, "local");
        var sanitized = extension.Equals(".jsonc", StringComparison.OrdinalIgnoreCase)
            ? Regex.Replace(text, @"//.*?$|/\*.*?\*/", string.Empty, RegexOptions.Multiline | RegexOptions.Singleline)
            : text;
        using var document = JsonDocument.Parse(sanitized, new JsonDocumentOptions { AllowTrailingCommas = true });
        AppendJson(document.RootElement, outline, "$", 0, 0);
        Finalize(outline);
        return outline;
    }

    private static void AppendJson(JsonElement element, DocumentOutline outline, string path, int depth, int lineNumber)
    {
        if (depth >= 2 || outline.Entries.Count >= MaxEntries) return;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Add(outline, property.Name, "key", depth + 1, lineNumber: lineNumber == 0 ? null : lineNumber);
                AppendJson(property.Value, outline, path + "." + property.Name, depth + 1, lineNumber);
            }
        }
    }

    private static DocumentOutline ExtractYaml(string[] lines, string extension)
    {
        var outline = NewOutline("YAML", extension, "local");
        for (var i = 0; i < lines.Length; i++)
        {
            var match = Regex.Match(lines[i], @"^(\s*)([A-Za-z0-9_.-]+)\s*:\s*(?:#.*)?$");
            if (!match.Success) continue;
            var indent = match.Groups[1].Value.Replace("\t", "  ").Length;
            Add(outline, match.Groups[2].Value, "key", Math.Clamp(indent / 2 + 1, 1, 9), i + 1);
        }
        Finalize(outline);
        return outline;
    }

    private static DocumentOutline ExtractMarkup(string[] lines, string extension)
    {
        var outline = NewOutline("Markup", extension, "local");
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match heading in Regex.Matches(lines[i], @"<h([1-6])\b[^>]*>(.*?)</h\1>", RegexOptions.IgnoreCase))
            {
                var text = Regex.Replace(heading.Groups[2].Value, "<[^>]+>", string.Empty).Trim();
                Add(outline, text, "heading", int.Parse(heading.Groups[1].Value, CultureInfo.InvariantCulture), i + 1);
            }
            var named = Regex.Match(lines[i], @"<([A-Za-z_][\w:.-]*)\b[^>]*(?:x:Name|Name|id)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (named.Success) Add(outline, $"{named.Groups[1].Value} {named.Groups[2].Value}", "named_element", 2, i + 1);
        }
        Finalize(outline);
        return outline;
    }

    private static DocumentOutline ExtractStylesheet(string[] lines, string extension)
    {
        var outline = NewOutline("Stylesheet", extension, "local");
        for (var i = 0; i < lines.Length; i++)
        {
            var match = Regex.Match(lines[i], @"^\s*((?:@media|@supports|@keyframes)\b[^\{]*|[^@/][^\{]{0,160})\s*\{");
            if (match.Success) Add(outline, match.Groups[1].Value.Trim(), "rule", 1, i + 1);
        }
        Finalize(outline);
        return outline;
    }

    private static DocumentOutline ExtractCode(string[] lines, string extension)
    {
        var outline = NewOutline("Code", extension, "local");
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (var candidate in MatchCodeLine(line, extension))
                Add(outline, candidate.Title, candidate.Kind, candidate.Level, i + 1);
        }
        Finalize(outline);
        return outline;
    }

    private static IEnumerable<CodeCandidate> MatchCodeLine(string line, string extension)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith('*')) yield break;

        var patterns = extension switch
        {
            ".py" or ".pyi" => new[]
            {
                (@"^(class)\s+[A-Za-z_]\w*(?:\([^)]*\))?\s*:", "class", 1),
                (@"^(?:async\s+)?(def)\s+[A-Za-z_]\w*\s*\([^)]*\)\s*(?:->\s*[^:]+)?\s*:", "function", 2)
            },
            ".rb" => new[]
            {
                (@"^(module|class)\s+[A-Za-z_:]\w*", "type", 1),
                (@"^(def)\s+(?:self\.)?[A-Za-z_]\w*[!?=]?", "method", 2)
            },
            ".go" => new[]
            {
                (@"^(package)\s+\w+", "package", 1),
                (@"^(type)\s+\w+\s+(?:struct|interface)\b", "type", 1),
                (@"^(func)\s+(?:\([^)]*\)\s*)?\w+\s*\(", "function", 2)
            },
            ".rs" => new[]
            {
                (@"^(?:pub(?:\([^)]*\))?\s+)?(mod|struct|enum|trait|impl)\b", "type", 1),
                (@"^(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?(fn)\s+\w+", "function", 2)
            },
            ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".vue" or ".svelte" or ".astro" => new[]
            {
                (@"^(?:export\s+(?:default\s+)?)?(?:declare\s+)?(class|interface|type|enum|namespace)\s+[$A-Za-z_]\w*", "type", 1),
                (@"^(?:export\s+(?:default\s+)?)?(?:async\s+)?(function)\s+[$A-Za-z_]\w*\s*\(", "function", 2),
                (@"^(?:export\s+)?(?:const|let|var)\s+[$A-Za-z_]\w*\s*=\s*(?:async\s*)?\([^)]*\)\s*=>", "function", 2)
            },
            ".sql" => new[]
            {
                (@"^(?:CREATE|ALTER)\s+(?:OR\s+REPLACE\s+)?(TABLE|VIEW|MATERIALIZED\s+VIEW|PROCEDURE|FUNCTION|TRIGGER|INDEX)\b", "declaration", 1)
            },
            ".graphql" or ".gql" => new[]
            {
                (@"^(type|input|interface|enum|scalar|union|schema|fragment|query|mutation|subscription)\b", "declaration", 1)
            },
            ".proto" => new[]
            {
                (@"^(package|message|enum|service|rpc)\b", "declaration", 1)
            },
            ".php" => new[]
            {
                (@"^(?:(?:final|abstract|readonly)\s+)?(class|interface|trait|enum)\s+\w+", "type", 1),
                (@"^(?:(?:public|protected|private|static|final|abstract)\s+)*(function)\s+&?\w+\s*\(", "function", 2)
            },
            ".sh" or ".bash" or ".zsh" => new[]
            {
                (@"^(?:function\s+)?([A-Za-z_]\w*)\s*\(\)\s*\{?", "function", 1)
            },
            ".ps1" => new[]
            {
                (@"^(function|filter|class|enum)\s+[A-Za-z_][\w-]*", "declaration", 1)
            },
            _ => new[]
            {
                (@"^(?:(?:public|protected|private|internal|static|sealed|abstract|partial|final|open|data|record|readonly|unsafe)\s+)*(namespace|module|class|interface|struct|record|enum|trait)\s+[A-Za-z_]\w*", "type", 1),
                (@"^(?:(?:public|protected|private|internal|static|virtual|override|abstract|async|final|open|synchronized|extern|unsafe|partial|inline|constexpr)\s+)*(?:[\w<>,.?\[\]:*&]+\s+)+(\w+)\s*\([^;]*\)\s*(?:\{|=>|throws\b|where\b|$)", "method", 2)
            }
        };

        foreach (var (pattern, kind, level) in patterns)
        {
            if (Regex.IsMatch(trimmed, pattern, RegexOptions.IgnoreCase))
                yield return new CodeCandidate(TrimSignature(trimmed), kind, level);
        }
    }

    private static DocumentOutline ExtractPlainText(string[] lines, string extension)
    {
        var outline = NewOutline("Text", extension, "local");
        var paragraph = new StringBuilder();
        var paragraphStart = 1;
        for (var i = 0; i <= lines.Length; i++)
        {
            var line = i < lines.Length ? lines[i] : string.Empty;
            if (!string.IsNullOrWhiteSpace(line))
            {
                if (paragraph.Length == 0) paragraphStart = i + 1;
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line.Trim());
                continue;
            }
            if (paragraph.Length == 0) continue;
            var text = paragraph.ToString();
            var firstSentence = Regex.Match(text, @"^.{1,180}?(?:[.!?。！？](?:\s|$)|$)").Value.Trim();
            if (!string.IsNullOrWhiteSpace(firstSentence)) Add(outline, firstSentence, "paragraph", 1, paragraphStart);
            paragraph.Clear();
        }
        Finalize(outline);
        return outline;
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlCharacters
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static DocumentOutline NewOutline(string type, string format, string source) => new()
    {
        OutlineType = type,
        Format = format,
        Source = source
    };

    private static void Add(DocumentOutline outline, string title, string kind, int level,
        int? lineNumber = null, int? pageNumber = null)
    {
        title = Regex.Replace(title, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(title) || outline.Entries.Count >= MaxEntries) return;
        if (outline.Entries.LastOrDefault() is { } previous
            && previous.Title.Equals(title, StringComparison.OrdinalIgnoreCase)
            && previous.LineNumber == lineNumber && previous.PageNumber == pageNumber) return;
        outline.Entries.Add(new OutlineEntry
        {
            Title = title.Length <= 240 ? title : title[..240] + "…",
            Kind = kind,
            Level = Math.Clamp(level, 1, 9),
            LineNumber = lineNumber,
            PageNumber = pageNumber
        });
    }

    private static void Finalize(DocumentOutline outline)
    {
        if (outline.Entries.Count >= MaxEntries)
        {
            outline.IsPartial = true;
            outline.Warnings.Add($"Outline was truncated to {MaxEntries} entries.");
        }
    }

    private static bool TryGetNumberedHeadingLevel(string text, out int level)
    {
        var match = Regex.Match(text, @"^\s*(\d+(?:\.\d+){0,8})[.)]?\s+\S");
        level = match.Success ? Math.Clamp(match.Groups[1].Value.Count(ch => ch == '.') + 1, 1, 9) : 0;
        return match.Success;
    }

    private static bool LooksLikeHeading(string text)
    {
        if (text.Length < 3 || text.Length > 180) return false;
        if (Regex.IsMatch(text, @"^[\d\s.,:;()\[\]-]+$")) return false;
        if (text.Count(char.IsLetterOrDigit) < 2) return false;
        return !Regex.IsMatch(text, @"[。.!?！？]\s*$") || text.Length < 60;
    }

    private static string TrimSignature(string value) =>
        value.Length <= 220 ? value : value[..220] + "…";

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static int ExtractTrailingNumber(string name)
    {
        var match = Regex.Match(name, @"(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : int.MaxValue;
    }

    private static IEnumerable? GetEnumerableProperty(object value, params string[] names)
    {
        var candidate = GetPropertyValue(value, names);
        return candidate is string ? null : candidate as IEnumerable;
    }

    private static object? GetPropertyValue(object value, params string[] names)
    {
        var type = value.GetType();
        foreach (var name in names)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property != null) return property.GetValue(value);
        }
        return null;
    }

    private static int? ToNullableInt(object? value)
    {
        if (value == null) return null;
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private sealed record CodeCandidate(string Title, string Kind, int Level);
    private sealed record PdfLine(string Text, double FontSize);
}
