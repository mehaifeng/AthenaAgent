using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using static Athena.UI.Services.Documents.WordprocessingSchema;

namespace Athena.UI.Services.Documents;

public sealed partial class DocxPackageService
{
    /// <summary>
    /// Renders a whole document as Markdown or plain text. inspect_document only returns a window,
    /// so this is how the model reads a long document end to end without paging.
    /// </summary>
    public object ConvertToText(string inputPath, string outputPath, bool markdown, bool overwrite)
    {
        EnsureDocumentExtension(inputPath);
        var extension = Path.GetExtension(outputPath).ToLowerInvariant();
        if (extension is not (".md" or ".markdown" or ".txt"))
            throw new ArgumentException("The output must be .md, .markdown or .txt.");
        EnsureCanWrite(outputPath, overwrite);
        markdown = markdown && extension != ".txt";

        using var archive = OpenChecked(inputPath, ZipArchiveMode.Read);
        var document = ReadXml(RequiredEntry(archive, DocumentPart));
        var body = document.Root?.Element(W + "body") ?? throw new InvalidDataException("Document has no body.");
        var outlineLevels = ReadStyleOutlineLevels(archive);

        var builder = new StringBuilder();
        var paragraphCount = 0;
        var tableCount = 0;
        var imageCount = 0;
        var orderedCounters = new Dictionary<int, int>();

        foreach (var element in body.Elements())
        {
            if (element.Name == W + "p")
            {
                paragraphCount++;
                imageCount += element.Descendants(A + "blip").Count();
                AppendParagraph(builder, element, outlineLevels, markdown, orderedCounters);
                continue;
            }

            if (element.Name != W + "tbl") continue;
            tableCount++;
            orderedCounters.Clear();
            AppendTable(builder, element, markdown);
        }

        var text = builder.ToString().TrimEnd() + "\n";
        AtomicWrite(outputPath, overwrite, temporaryPath => File.WriteAllText(temporaryPath, text, new UTF8Encoding(false)));

        var warnings = new List<string>();
        if (imageCount > 0) warnings.Add($"{imageCount} image(s) were noted as placeholders; the picture data stays in the .docx.");
        var features = DetectFeatures(archive);
        if (features.Contains("tracked changes")) warnings.Add("Tracked changes were rendered as their accepted text.");
        if (features.Contains("headers") || features.Contains("footers")) warnings.Add("Headers and footers are not part of the exported text.");

        return new
        {
            inputPath,
            outputPath,
            format = markdown ? "markdown" : "text",
            paragraphCount,
            tableCount,
            characterCount = text.Length,
            warnings
        };
    }

    private static void AppendParagraph(StringBuilder builder, XElement paragraph,
        IReadOnlyDictionary<string, int> outlineLevels, bool markdown, Dictionary<int, int> orderedCounters)
    {
        var text = ParagraphText(paragraph).Trim();
        var hasImage = paragraph.Descendants(A + "blip").Any();

        if (text.Length == 0 && !hasImage)
        {
            orderedCounters.Clear();
            builder.Append('\n');
            return;
        }

        var level = HeadingLevel(paragraph, outlineLevels);
        if (level is int heading)
        {
            orderedCounters.Clear();
            builder.Append('\n');
            if (markdown) builder.Append('#', Math.Min(6, heading)).Append(' ');
            builder.Append(text).Append("\n\n");
            return;
        }

        if (IsListParagraph(paragraph))
        {
            var listLevel = ListLevel(paragraph);
            var numberingId = (string?)paragraph.Element(W + "pPr")?.Element(W + "numPr")?.Element(W + "numId")?.Attribute(W + "val");
            var ordered = numberingId == OrderedNumberingId.ToString();

            if (markdown)
            {
                builder.Append(new string(' ', listLevel * 2));
                if (ordered)
                {
                    orderedCounters.TryGetValue(listLevel, out var counter);
                    orderedCounters[listLevel] = ++counter;
                    builder.Append(counter).Append(". ");
                }
                else builder.Append("- ");
            }
            builder.Append(FormatInline(paragraph, markdown)).Append('\n');
            return;
        }

        orderedCounters.Clear();
        if (hasImage && text.Length == 0)
        {
            builder.Append(markdown ? "![image](embedded)" : "[image]").Append("\n\n");
            return;
        }

        builder.Append(FormatInline(paragraph, markdown)).Append("\n\n");
    }

    /// <summary>Renders runs, promoting bold and italic runs to Markdown emphasis.</summary>
    private static string FormatInline(XElement paragraph, bool markdown)
    {
        if (!markdown) return ParagraphText(paragraph).Trim();

        var builder = new StringBuilder();
        foreach (var run in paragraph.Elements(W + "r"))
        {
            var text = string.Concat(run.Elements(W + "t").Select(element => element.Value));
            if (run.Elements(W + "br").Any()) text += "\n";
            if (text.Length == 0) continue;

            var properties = run.Element(W + "rPr");
            var bold = IsEnabled(properties?.Element(W + "b"));
            var italic = IsEnabled(properties?.Element(W + "i"));

            // Emphasis markers cannot wrap the surrounding whitespace or Markdown ignores them.
            var leading = text[..(text.Length - text.TrimStart().Length)];
            var trailing = text[text.TrimEnd().Length..];
            var core = text.Trim();
            if (core.Length == 0)
            {
                builder.Append(text);
                continue;
            }

            if (bold) core = $"**{core}**";
            if (italic) core = $"*{core}*";
            builder.Append(leading).Append(core).Append(trailing);
        }

        var result = builder.ToString().Trim();
        return result.Length > 0 ? result : ParagraphText(paragraph).Trim();
    }

    private static bool IsEnabled(XElement? toggle)
    {
        if (toggle is null) return false;
        var value = (string?)toggle.Attribute(W + "val");
        return value is null || value is "1" or "true" or "on";
    }

    private static void AppendTable(StringBuilder builder, XElement table, bool markdown)
    {
        var rows = table.Elements(W + "tr").ToList();
        if (rows.Count == 0) return;
        builder.Append('\n');

        if (!markdown)
        {
            foreach (var row in rows)
                builder.Append(string.Join("\t", row.Elements(W + "tc").Select(cell => Flatten(CellText(cell))))).Append('\n');
            builder.Append('\n');
            return;
        }

        var columnCount = rows.Max(row => row.Elements(W + "tc").Count());
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var cells = rows[rowIndex].Elements(W + "tc").Select(cell => Flatten(CellText(cell))).ToList();
            while (cells.Count < columnCount) cells.Add(string.Empty);
            builder.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (rowIndex == 0) builder.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", columnCount))).Append(" |\n");
        }
        builder.Append('\n');
    }

    /// <summary>Cell text has to survive on one Markdown table row, so breaks and pipes are escaped.</summary>
    private static string Flatten(string text) =>
        text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|").Trim();
}
