using System.CommandLine;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MiniMaxAIDocx.Core.Commands;

/// <summary>
/// Scenario B: Surgical content editing operations on existing DOCX files.
/// Preserves all existing formatting and minimizes XML changes.
/// </summary>
public static class EditContentCommand
{
    public static Command Create()
    {
        var cmd = new Command("edit", "Edit existing DOCX content");

        cmd.Add(CreateReplaceTextCommand());
        cmd.Add(CreateFillTableCommand());
        cmd.Add(CreateInsertParagraphCommand());
        cmd.Add(CreateUpdateFieldCommand());
        cmd.Add(CreateListPlaceholdersCommand());
        cmd.Add(CreateFillPlaceholdersCommand());

        return cmd;
    }

    private static Command CreateReplaceTextCommand()
    {
        var inputOpt = new Option<string>("--input") { Description = "Input DOCX file", Required = true };
        var outputOpt = new Option<string>("--output") { Description = "Output file path (defaults to overwriting input)" };
        var searchOpt = new Option<string>("--search") { Description = "Text to search for", Required = true };
        var replaceOpt = new Option<string>("--replace") { Description = "Replacement text", Required = true };
        var regexOpt = new Option<bool>("--regex") { Description = "Treat search as a regex pattern" };

        var cmd = new Command("replace-text", "Replace text while preserving formatting")
        {
            inputOpt, outputOpt, searchOpt, replaceOpt, regexOpt
        };

        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputOpt)!;
            var output = parseResult.GetValue(outputOpt) ?? input;
            var search = parseResult.GetValue(searchOpt)!;
            var replace = parseResult.GetValue(replaceOpt)!;
            var useRegex = parseResult.GetValue(regexOpt);

            if (output != input) File.Copy(input, output, overwrite: true);

            using var doc = WordprocessingDocument.Open(output, true);
            var mainDocument = doc.MainDocumentPart?.Document;
            if (mainDocument?.Body is not Body body) { Console.Error.WriteLine("No document body found."); return; }

            int count = 0;
            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                count += ReplaceInParagraph(paragraph, search, replace, useRegex);
            }

            mainDocument.Save();
            Console.WriteLine($"Replaced {count} occurrence(s) in {output}");
        });

        return cmd;
    }

    private static Command CreateFillTableCommand()
    {
        var inputOpt = new Option<string>("--input") { Description = "Input DOCX file", Required = true };
        var outputOpt = new Option<string>("--output") { Description = "Output file path" };
        var tableIndexOpt = new Option<int>("--table-index") { Description = "Zero-based index of the table to fill" };
        tableIndexOpt.DefaultValueFactory = _ => 0;
        var csvOpt = new Option<string>("--csv") { Description = "CSV file with data to fill", Required = true };
        var appendOpt = new Option<bool>("--append") { Description = "Append rows instead of replacing existing data rows" };

        var cmd = new Command("fill-table", "Fill a table with data from CSV")
        {
            inputOpt, outputOpt, tableIndexOpt, csvOpt, appendOpt
        };

        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputOpt)!;
            var output = parseResult.GetValue(outputOpt) ?? input;
            var tableIndex = parseResult.GetValue(tableIndexOpt);
            var csvPath = parseResult.GetValue(csvOpt)!;
            var append = parseResult.GetValue(appendOpt);

            if (output != input) File.Copy(input, output, overwrite: true);

            if (!File.Exists(csvPath)) { Console.Error.WriteLine($"CSV file not found: {csvPath}"); return; }

            using var doc = WordprocessingDocument.Open(output, true);
            var mainDocument = doc.MainDocumentPart?.Document;
            if (mainDocument?.Body is not Body body) { Console.Error.WriteLine("No document body found."); return; }

            var tables = body.Elements<Table>().ToList();
            if (tableIndex >= tables.Count)
            {
                Console.Error.WriteLine($"Table index {tableIndex} out of range (found {tables.Count} tables).");
                return;
            }

            var table = tables[tableIndex];
            var csvLines = File.ReadAllLines(csvPath);
            if (csvLines.Length == 0) { Console.WriteLine("CSV is empty, nothing to fill."); return; }

            // Get template row properties from the first data row (second row, after header)
            var existingRows = table.Elements<TableRow>().ToList();
            TableRow? templateRow = existingRows.Count > 1 ? existingRows[1] : existingRows.FirstOrDefault();
            var templateTrPr = templateRow?.TableRowProperties?.CloneNode(true) as TableRowProperties;

            if (!append)
            {
                // Remove all rows except the header row
                for (int i = existingRows.Count - 1; i >= 1; i--)
                    existingRows[i].Remove();
            }

            int rowsAdded = 0;
            // Skip header line in CSV (index 0)
            for (int i = 1; i < csvLines.Length; i++)
            {
                var values = ParseCsvLine(csvLines[i]);
                var newRow = new TableRow();
                if (templateTrPr != null)
                    newRow.Append(templateTrPr.CloneNode(true));

                foreach (var val in values)
                {
                    var cell = new TableCell(
                        new Paragraph(new Run(new Text(val))));
                    newRow.Append(cell);
                }

                table.Append(newRow);
                rowsAdded++;
            }

            mainDocument.Save();
            Console.WriteLine($"Added {rowsAdded} rows to table {tableIndex} in {output}");
        });

        return cmd;
    }

    private static Command CreateInsertParagraphCommand()
    {
        var inputOpt = new Option<string>("--input") { Description = "Input DOCX file", Required = true };
        var outputOpt = new Option<string>("--output") { Description = "Output file path" };
        var textOpt = new Option<string>("--text") { Description = "Paragraph text", Required = true };
        var styleOpt = new Option<string>("--style") { Description = "Paragraph style (e.g. Heading1, Normal)" };
        var afterOpt = new Option<int>("--after-paragraph") { Description = "Insert after this paragraph index (0-based)" };
        afterOpt.DefaultValueFactory = _ => -1; // -1 = append at end

        var cmd = new Command("insert-paragraph", "Insert a new paragraph")
        {
            inputOpt, outputOpt, textOpt, styleOpt, afterOpt
        };

        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputOpt)!;
            var output = parseResult.GetValue(outputOpt) ?? input;
            var text = parseResult.GetValue(textOpt)!;
            var style = parseResult.GetValue(styleOpt);
            var afterIndex = parseResult.GetValue(afterOpt);

            if (output != input) File.Copy(input, output, overwrite: true);

            using var doc = WordprocessingDocument.Open(output, true);
            var mainDocument = doc.MainDocumentPart?.Document;
            if (mainDocument?.Body is not Body body) { Console.Error.WriteLine("No document body found."); return; }

            var newPara = new Paragraph();
            if (!string.IsNullOrEmpty(style))
                newPara.Append(new ParagraphProperties(new ParagraphStyleId { Val = style }));
            newPara.Append(new Run(new Text(text)));

            var paragraphs = body.Elements<Paragraph>().ToList();
            if (afterIndex >= 0 && afterIndex < paragraphs.Count)
            {
                paragraphs[afterIndex].InsertAfterSelf(newPara);
            }
            else
            {
                // Insert before sectPr if present, otherwise append
                var sectPr = body.Elements<SectionProperties>().FirstOrDefault();
                if (sectPr != null)
                    sectPr.InsertBeforeSelf(newPara);
                else
                    body.Append(newPara);
            }

            mainDocument.Save();
            Console.WriteLine($"Inserted paragraph in {output}");
        });

        return cmd;
    }

    private static Command CreateUpdateFieldCommand()
    {
        var inputOpt = new Option<string>("--input") { Description = "Input DOCX file", Required = true };
        var outputOpt = new Option<string>("--output") { Description = "Output file path" };
        var fieldNameOpt = new Option<string>("--field") { Description = "Document property field name (e.g. TITLE, AUTHOR)", Required = true };
        var valueOpt = new Option<string>("--value") { Description = "New field value", Required = true };

        var cmd = new Command("update-field", "Update a document property field value")
        {
            inputOpt, outputOpt, fieldNameOpt, valueOpt
        };

        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputOpt)!;
            var output = parseResult.GetValue(outputOpt) ?? input;
            var fieldName = parseResult.GetValue(fieldNameOpt)!;
            var value = parseResult.GetValue(valueOpt)!;

            if (output != input) File.Copy(input, output, overwrite: true);

            using var doc = WordprocessingDocument.Open(output, true);

            // Update core properties
            var props = doc.PackageProperties;
            switch (fieldName.ToUpperInvariant())
            {
                case "TITLE": props.Title = value; break;
                case "AUTHOR": props.Creator = value; break;
                case "SUBJECT": props.Subject = value; break;
                case "KEYWORDS": props.Keywords = value; break;
                case "DESCRIPTION": props.Description = value; break;
                case "CATEGORY": props.Category = value; break;
                default:
                    Console.Error.WriteLine($"Unknown field: {fieldName}. Supported: TITLE, AUTHOR, SUBJECT, KEYWORDS, DESCRIPTION, CATEGORY");
                    return;
            }

            Console.WriteLine($"Updated {fieldName} to \"{value}\" in {output}");
        });

        return cmd;
    }

    private static Command CreateListPlaceholdersCommand()
    {
        var inputOpt = new Option<string>("--input") { Description = "Input DOCX file", Required = true };
        var patternOpt = new Option<string>("--pattern") { Description = "Placeholder pattern (regex)" };
        patternOpt.DefaultValueFactory = _ => @"\{\{(\w+)\}\}"; // {{PLACEHOLDER}}

        var cmd = new Command("list-placeholders", "List all placeholders found in the document")
        {
            inputOpt, patternOpt
        };

        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputOpt)!;
            var pattern = parseResult.GetValue(patternOpt)!;

            using var doc = WordprocessingDocument.Open(input, false);
            var mainDocument = doc.MainDocumentPart?.Document;
            if (mainDocument?.Body is not Body body) { Console.Error.WriteLine("No document body found."); return; }

            var placeholders = new HashSet<string>();
            var regex = new Regex(pattern);

            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                var fullText = string.Concat(paragraph.Descendants<Text>().Select(t => t.Text));
                foreach (Match match in regex.Matches(fullText))
                {
                    placeholders.Add(match.Value);
                }
            }

            if (placeholders.Count == 0)
            {
                Console.WriteLine("No placeholders found.");
                return;
            }

            Console.WriteLine($"Found {placeholders.Count} unique placeholder(s):");
            foreach (var p in placeholders.OrderBy(x => x))
                Console.WriteLine($"  {p}");
        });

        return cmd;
    }

    private static Command CreateFillPlaceholdersCommand()
    {
        var inputOpt = new Option<string>("--input") { Description = "Input DOCX file", Required = true };
        var outputOpt = new Option<string>("--output") { Description = "Output file path" };
        var mappingOpt = new Option<string>("--mapping") { Description = "JSON file mapping placeholder names to values", Required = true };
        var patternOpt = new Option<string>("--pattern") { Description = "Placeholder pattern with capture group for the name" };
        patternOpt.DefaultValueFactory = _ => @"\{\{(\w+)\}\}";

        var cmd = new Command("fill-placeholders", "Replace placeholders with values from a mapping file")
        {
            inputOpt, outputOpt, mappingOpt, patternOpt
        };

        cmd.SetAction((parseResult) =>
        {
            var input = parseResult.GetValue(inputOpt)!;
            var output = parseResult.GetValue(outputOpt) ?? input;
            var mappingPath = parseResult.GetValue(mappingOpt)!;
            var pattern = parseResult.GetValue(patternOpt)!;

            if (!File.Exists(mappingPath)) { Console.Error.WriteLine($"Mapping file not found: {mappingPath}"); return; }

            var mappingJson = File.ReadAllText(mappingPath);
            Dictionary<string, string> mapping;
            try
            {
                mapping = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson) ?? [];
            }
            catch (System.Text.Json.JsonException ex)
            {
                Console.Error.WriteLine($"Invalid mapping JSON: {ex.Message}");
                return;
            }

            if (output != input) File.Copy(input, output, overwrite: true);

            using var doc = WordprocessingDocument.Open(output, true);
            var mainDocument = doc.MainDocumentPart?.Document;
            if (mainDocument?.Body is not Body body) { Console.Error.WriteLine("No document body found."); return; }

            int totalReplacements = 0;
            var regex = new Regex(pattern);

            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                var fullText = string.Concat(paragraph.Descendants<Text>().Select(t => t.Text));
                var matches = regex.Matches(fullText);
                if (matches.Count == 0) continue;

                foreach (Match match in matches)
                {
                    var placeholderName = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                    if (mapping.TryGetValue(placeholderName, out var replacement))
                    {
                        totalReplacements += ReplaceInParagraph(paragraph, match.Value, replacement, false);
                    }
                }
            }

            mainDocument.Save();
            Console.WriteLine($"Filled {totalReplacements} placeholder(s) in {output}");
        });

        return cmd;
    }

    /// <summary>
    /// Replaces text within a paragraph while preserving run formatting.
    /// Handles the case where search text may span multiple runs.
    /// </summary>
    private static int ReplaceInParagraph(Paragraph paragraph, string search, string replace, bool useRegex)
    {
        if (string.IsNullOrEmpty(search))
            return 0;

        if (useRegex)
        {
            // Regex replacement is deliberately run-local. Rebuilding a paragraph from
            // a regex match that crosses runs destroys mixed formatting and RSIDs.
            var regex = new Regex(search);
            var count = 0;
            foreach (var text in paragraph.Descendants<Text>().ToList())
            {
                var matches = regex.Matches(text.Text).Count;
                if (matches == 0) continue;
                text.Text = regex.Replace(text.Text, replace);
                text.Space = NeedsPreservedWhitespace(text.Text)
                    ? SpaceProcessingModeValues.Preserve
                    : null;
                count += matches;
            }
            return count;
        }

        var total = 0;
        foreach (var group in GetPlainRunGroups(paragraph))
        {
            var fullText = string.Concat(group.Select(slice => slice.Text));
            var starts = new List<int>();
            var index = 0;
            while ((index = fullText.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
            {
                starts.Add(index);
                index += search.Length;
            }

            if (starts.Count == 0) continue;
            RewritePlainRunGroup(group, starts, search.Length, replace);
            total += starts.Count;
        }
        return total;
    }

    private static List<List<RunSlice>> GetPlainRunGroups(Paragraph paragraph)
    {
        var groups = new List<List<RunSlice>>();
        List<RunSlice>? current = null;
        var offset = 0;
        foreach (var child in paragraph.ChildElements)
        {
            if (child is Run run
                && run.Elements<Text>().Any()
                && run.ChildElements.All(item => item is RunProperties or Text))
            {
                current ??= [];
                var value = string.Concat(run.Elements<Text>().Select(item => item.Text));
                current.Add(new RunSlice(run, value, offset, offset + value.Length));
                offset += value.Length;
                continue;
            }

            if (current is { Count: > 0 }) groups.Add(current);
            current = null;
            offset = 0;
        }
        if (current is { Count: > 0 }) groups.Add(current);
        return groups;
    }

    private static void RewritePlainRunGroup(
        IReadOnlyList<RunSlice> group,
        IReadOnlyList<int> starts,
        int matchLength,
        string replacement)
    {
        var nodes = new List<OpenXmlElement>();
        var cursor = 0;
        foreach (var start in starts)
        {
            nodes.AddRange(CloneRange(group, cursor, start));
            if (replacement.Length > 0)
            {
                var source = group.First(slice => slice.End > start).Run;
                nodes.Add(CloneRunWithText(source, replacement));
            }
            cursor = start + matchLength;
        }
        nodes.AddRange(CloneRange(group, cursor, group[^1].End));

        var first = group[0].Run;
        foreach (var node in nodes) first.InsertBeforeSelf(node);
        foreach (var slice in group) slice.Run.Remove();
    }

    private static IEnumerable<OpenXmlElement> CloneRange(
        IReadOnlyList<RunSlice> group,
        int start,
        int end)
    {
        if (start >= end) yield break;
        foreach (var slice in group)
        {
            var overlapStart = Math.Max(start, slice.Start);
            var overlapEnd = Math.Min(end, slice.End);
            if (overlapStart >= overlapEnd) continue;
            if (overlapStart == slice.Start && overlapEnd == slice.End)
            {
                yield return slice.Run.CloneNode(true);
                continue;
            }
            yield return CloneRunWithText(
                slice.Run,
                slice.Text.Substring(overlapStart - slice.Start, overlapEnd - overlapStart));
        }
    }

    private static Run CloneRunWithText(Run source, string value)
    {
        var clone = (Run)source.CloneNode(true);
        foreach (var child in clone.ChildElements.Where(item => item is not RunProperties).ToList())
            child.Remove();
        clone.Append(new Text(value)
        {
            Space = NeedsPreservedWhitespace(value) ? SpaceProcessingModeValues.Preserve : null
        });
        return clone;
    }

    private static bool NeedsPreservedWhitespace(string value) =>
        value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    private sealed record RunSlice(Run Run, string Text, int Start, int End);

    private static string[] ParseCsvLine(string line)
    {
        // Simple CSV parser (handles quoted fields)
        var result = new List<string>();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result.ToArray();
    }
}
