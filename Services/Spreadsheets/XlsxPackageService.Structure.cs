using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Athena.UI.Services.Spreadsheets;

public sealed partial class XlsxPackageService
{
    private const int MaxStructureOperations = 100;

    /// <summary>
    /// Inserts or deletes whole rows/columns and repairs every reference that moves with them:
    /// formulas on all worksheets, merged ranges, autofilter/sort/validation/conditional ranges,
    /// hyperlinks, column definitions and workbook defined names.
    /// </summary>
    public object ModifyStructure(string inputPath, string outputPath, string operationsJson, bool overwrite)
    {
        EnsureDistinctWorkbookPaths(inputPath, outputPath);
        EnsureCanWrite(outputPath, overwrite);

        using var json = JsonDocument.Parse(operationsJson, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip });
        var operationsElement = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement
            : json.RootElement.TryGetProperty("operations", out var nested) ? nested : default;
        if (operationsElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("operationsJson must be an array or an object containing an 'operations' array.");
        var requests = operationsElement.EnumerateArray().ToList();
        if (requests.Count is < 1 or > MaxStructureOperations)
            throw new ArgumentException($"Provide 1-{MaxStructureOperations} structure operations per call.");

        using (var checkedArchive = OpenChecked(inputPath, ZipArchiveMode.Read))
            _ = ReadSheetMap(checkedArchive);

        var applied = new List<object>();
        var warnings = new List<string>();

        AtomicWrite(outputPath, overwrite, temporaryPath =>
        {
            File.Copy(inputPath, temporaryPath, overwrite: true);
            using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false, Encoding.UTF8);
            var sheetMap = ReadSheetMap(archive);
            var documents = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
            var tableDocuments = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
            var workbook = ReadXml(RequiredEntry(archive, "xl/workbook.xml"), preserveWhitespace: true);

            foreach (var request in requests)
            {
                var operation = ParseOperation(request, sheetMap);
                var target = sheetMap.First(sheet => sheet.Name.Equals(operation.SheetName, StringComparison.OrdinalIgnoreCase));
                var worksheet = LoadWorksheet(archive, documents, target.PartPath);

                GuardStructuredTables(archive, tableDocuments, target, worksheet, operation);
                ApplySheetData(worksheet, operation);
                ApplyWorksheetRanges(worksheet, operation);
                ShiftTableRanges(archive, tableDocuments, target, worksheet, operation);

                foreach (var sheet in sheetMap)
                {
                    var document = LoadWorksheet(archive, documents, sheet.PartPath);
                    ShiftFormulas(document, sheet.Name, operation);
                }

                ShiftDefinedNames(workbook, operation);

                applied.Add(new
                {
                    sheet = operation.SheetName,
                    action = (operation.IsInsert ? "insert" : "delete") + (operation.Axis == ShiftAxis.Row ? "Rows" : "Columns"),
                    index = operation.Index,
                    count = operation.Count
                });
            }

            foreach (var pair in documents)
            {
                UpdateDimension(pair.Value);
                ReplaceXmlEntry(archive, pair.Key, pair.Value);
            }
            foreach (var pair in tableDocuments) ReplaceXmlEntry(archive, pair.Key, pair.Value);
            ReplaceXmlEntry(archive, "xl/workbook.xml", workbook);

            RemoveCalculationChain(archive);
            SetFullCalculation(archive);

            var features = DetectFeatures(archive);
            if (features.Count > 0)
                warnings.Add("Workbook contains features whose internal ranges Athena cannot rewrite: " + string.Join(", ", features) + ". Verify them in Excel or LibreOffice.");
        });

        warnings.Add("Formulas were rewritten statically. Open the result in Excel or LibreOffice to recalculate before trusting computed values.");
        return new
        {
            inputPath,
            outputPath,
            operations = applied,
            warnings
        };
    }

    private ShiftOperation ParseOperation(JsonElement request, IReadOnlyList<SheetPart> sheetMap)
    {
        if (request.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each structure operation must be an object.");
        var sheetName = GetRequiredString(request, "sheet");
        var sheet = sheetMap.FirstOrDefault(candidate => candidate.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Worksheet not found: {sheetName}");

        var action = GetRequiredString(request, "action").Trim();
        var (axis, isInsert) = action.ToLowerInvariant() switch
        {
            "insertrows" => (ShiftAxis.Row, true),
            "deleterows" => (ShiftAxis.Row, false),
            "insertcolumns" => (ShiftAxis.Column, true),
            "deletecolumns" => (ShiftAxis.Column, false),
            _ => throw new ArgumentException($"Unsupported action '{action}'. Use insertRows, deleteRows, insertColumns or deleteColumns.")
        };

        var limit = axis == ShiftAxis.Row ? 1_048_576 : 16_384;
        var index = ParseIndex(request, axis);
        if (index < 1 || index > limit) throw new ArgumentException($"'index' must be between 1 and {limit}.");

        var count = 1;
        if (request.TryGetProperty("count", out var countElement))
        {
            if (!countElement.TryGetInt32(out count) || count < 1 || count > 10_000)
                throw new ArgumentException("'count' must be an integer from 1 to 10000.");
        }
        if (index + count - 1 > limit) throw new ArgumentException("The operation exceeds the worksheet bounds.");

        return new ShiftOperation(sheet.Name, axis, index, count, isInsert);
    }

    private static int ParseIndex(JsonElement request, ShiftAxis axis)
    {
        if (!request.TryGetProperty("index", out var indexElement))
            throw new ArgumentException("Each structure operation needs an 'index'.");

        if (indexElement.ValueKind == JsonValueKind.Number)
            return indexElement.TryGetInt32(out var numeric) ? numeric : throw new ArgumentException("'index' must be an integer.");

        if (indexElement.ValueKind != JsonValueKind.String) throw new ArgumentException("'index' must be a number, or a column letter for column operations.");
        var text = indexElement.GetString()!.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        if (axis != ShiftAxis.Column) throw new ArgumentException("Row operations need a numeric 'index'.");
        if (!TryParseColumnName(text, out var column)) throw new ArgumentException($"Invalid column reference: {text}");
        return column;
    }

    private static bool TryParseColumnName(string text, out int column)
    {
        column = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 3) return false;
        foreach (var character in text)
        {
            if (!char.IsAsciiLetter(character)) return false;
            column = column * 26 + (char.ToUpperInvariant(character) - 'A' + 1);
        }
        return column is >= 1 and <= 16_384;
    }

    private static XDocument LoadWorksheet(ZipArchive archive, IDictionary<string, XDocument> documents, string partPath)
    {
        if (documents.TryGetValue(partPath, out var existing)) return existing;
        var document = ReadXml(RequiredEntry(archive, partPath), preserveWhitespace: true);
        documents[partPath] = document;
        return document;
    }

    private static void ApplySheetData(XDocument worksheet, ShiftOperation operation)
    {
        var sheetData = worksheet.Root?.Element(SpreadsheetNs + "sheetData")
            ?? throw new InvalidDataException("Worksheet has no sheetData.");

        if (operation.Axis == ShiftAxis.Row) ApplyRowOperation(sheetData, operation);
        else ApplyColumnOperation(sheetData, operation);
    }

    private static void ApplyRowOperation(XElement sheetData, ShiftOperation operation)
    {
        var rows = sheetData.Elements(SpreadsheetNs + "row").ToList();
        var ordered = operation.IsInsert ? Enumerable.Reverse(rows) : rows;

        foreach (var row in ordered)
        {
            if (!int.TryParse((string?)row.Attribute("r"), out var rowNumber)) continue;
            var mapped = operation.Map(rowNumber);
            if (mapped is null)
            {
                if (operation.IsInsert)
                    throw new ArgumentException($"Inserting {operation.Count} rows would push existing data past row {operation.Limit}.");
                row.Remove();
                continue;
            }

            if (mapped.Value == rowNumber) continue;
            row.SetAttributeValue("r", mapped.Value);
            row.Attribute("spans")?.Remove();
            foreach (var cell in row.Elements(SpreadsheetNs + "c"))
            {
                if (!TryParseCellReference((string?)cell.Attribute("r") ?? string.Empty, out _, out var column)) continue;
                cell.SetAttributeValue("r", ColumnName(column) + mapped.Value.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static void ApplyColumnOperation(XElement sheetData, ShiftOperation operation)
    {
        foreach (var row in sheetData.Elements(SpreadsheetNs + "row"))
        {
            row.Attribute("spans")?.Remove();
            var cells = row.Elements(SpreadsheetNs + "c").ToList();
            var ordered = operation.IsInsert ? Enumerable.Reverse(cells) : cells;

            foreach (var cell in ordered)
            {
                if (!TryParseCellReference((string?)cell.Attribute("r") ?? string.Empty, out var rowNumber, out var column)) continue;
                var mapped = operation.Map(column);
                if (mapped is null)
                {
                    if (operation.IsInsert)
                        throw new ArgumentException($"Inserting {operation.Count} columns would push existing data past column XFD.");
                    cell.Remove();
                    continue;
                }

                if (mapped.Value == column) continue;
                cell.SetAttributeValue("r", ColumnName(mapped.Value) + rowNumber.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>Rewrites every worksheet-scoped range attribute that Excel would move itself.</summary>
    private static void ApplyWorksheetRanges(XDocument worksheet, ShiftOperation operation)
    {
        var root = worksheet.Root;
        if (root is null) return;

        ShiftRangeAttribute(root.Element(SpreadsheetNs + "autoFilter"), "ref", operation, removeWhenGone: true);
        ShiftRangeAttribute(root.Element(SpreadsheetNs + "sortState"), "ref", operation, removeWhenGone: true);

        foreach (var element in root.Elements(SpreadsheetNs + "conditionalFormatting").ToList())
            ShiftRangeAttribute(element, "sqref", operation, removeWhenGone: true);

        foreach (var element in root.Descendants(SpreadsheetNs + "dataValidation").ToList())
            ShiftRangeAttribute(element, "sqref", operation, removeWhenGone: true);
        PruneCountedContainer(root.Element(SpreadsheetNs + "dataValidations"), "dataValidation", setCount: true);

        foreach (var element in root.Descendants(SpreadsheetNs + "hyperlink").ToList())
            ShiftRangeAttribute(element, "ref", operation, removeWhenGone: true);
        // CT_Hyperlinks has no count attribute; only prune the container when it empties out.
        PruneCountedContainer(root.Element(SpreadsheetNs + "hyperlinks"), "hyperlink", setCount: false);

        ApplyMergedRanges(root, operation);

        if (operation.Axis == ShiftAxis.Column) ApplyColumnDefinitions(root, operation);
        root.Element(SpreadsheetNs + "rowBreaks")?.Remove();
        root.Element(SpreadsheetNs + "colBreaks")?.Remove();
    }

    private static void ApplyColumnDefinitions(XElement root, ShiftOperation operation)
    {
        var cols = root.Element(SpreadsheetNs + "cols");
        if (cols is null) return;

        foreach (var col in cols.Elements(SpreadsheetNs + "col").ToList())
        {
            if (!int.TryParse((string?)col.Attribute("min"), out var min) || !int.TryParse((string?)col.Attribute("max"), out var max)) continue;
            var mappedMin = operation.Map(min);
            var mappedMax = operation.Map(max);
            var newMin = mappedMin ?? operation.Index;
            var newMax = mappedMax ?? operation.Index - 1;
            if (newMax < newMin || newMin < 1)
            {
                col.Remove();
                continue;
            }
            col.SetAttributeValue("min", newMin);
            col.SetAttributeValue("max", newMax);
        }

        if (!cols.Elements(SpreadsheetNs + "col").Any()) cols.Remove();
    }

    /// <summary>
    /// Moves merged ranges and drops any that collapse: Excel rejects a merge that covers a
    /// single cell, so a two-cell merge losing one of its cells must disappear entirely.
    /// </summary>
    private static void ApplyMergedRanges(XElement root, ShiftOperation operation)
    {
        var mergeCells = root.Element(SpreadsheetNs + "mergeCells");
        if (mergeCells is null) return;

        foreach (var merge in mergeCells.Elements(SpreadsheetNs + "mergeCell").ToList())
        {
            var reference = (string?)merge.Attribute("ref");
            if (reference is null) continue;
            var shifted = FormulaReferenceShifter.ShiftLocalRanges(reference, operation);
            if (shifted is null || !TryParseRange(shifted, out var startRow, out var startColumn, out var endRow, out var endColumn)
                || (startRow == endRow && startColumn == endColumn))
            {
                merge.Remove();
                continue;
            }
            merge.SetAttributeValue("ref", shifted);
        }

        PruneCountedContainer(mergeCells, "mergeCell", setCount: true);
    }

    private static void PruneCountedContainer(XElement? container, string childName, bool setCount)
    {
        if (container is null) return;
        var remaining = container.Elements(SpreadsheetNs + childName).Count();
        if (remaining == 0) container.Remove();
        else if (setCount) container.SetAttributeValue("count", remaining);
    }

    private static void ShiftRangeAttribute(XElement? element, string attributeName, ShiftOperation operation, bool removeWhenGone)
    {
        var attribute = element?.Attribute(attributeName);
        if (element is null || attribute is null) return;

        var shifted = FormulaReferenceShifter.ShiftLocalRanges(attribute.Value, operation);
        if (shifted is null)
        {
            if (removeWhenGone) element.Remove();
            return;
        }
        attribute.Value = shifted;
    }

    private static void ShiftFormulas(XDocument worksheet, string sheetName, ShiftOperation operation)
    {
        foreach (var formula in worksheet.Descendants(SpreadsheetNs + "f"))
        {
            if (!string.IsNullOrEmpty(formula.Value))
            {
                var shifted = FormulaReferenceShifter.ShiftFormula(formula.Value, sheetName, operation);
                if (!string.Equals(shifted, formula.Value, StringComparison.Ordinal)) formula.Value = shifted;
            }

            // Shared and array formulas carry their own span, which only moves with its own sheet.
            if (sheetName.Equals(operation.SheetName, StringComparison.OrdinalIgnoreCase))
                ShiftRangeAttribute(formula, "ref", operation, removeWhenGone: false);
        }
    }

    private static void ShiftDefinedNames(XDocument workbook, ShiftOperation operation)
    {
        var definedNames = workbook.Root?.Element(SpreadsheetNs + "definedNames");
        if (definedNames is null) return;

        foreach (var definedName in definedNames.Elements(SpreadsheetNs + "definedName"))
        {
            if (string.IsNullOrWhiteSpace(definedName.Value)) continue;
            // hostSheet stays null: a defined name without an explicit sheet prefix is not anchored here.
            definedName.Value = FormulaReferenceShifter.ShiftFormula(definedName.Value, null, operation);
        }
    }

    private static void GuardStructuredTables(ZipArchive archive, IDictionary<string, XDocument> tableDocuments,
        SheetPart sheet, XDocument worksheet, ShiftOperation operation)
    {
        foreach (var (partPath, table) in ReadTables(archive, tableDocuments, sheet, worksheet))
        {
            var reference = (string?)table.Root?.Attribute("ref");
            if (reference is null || !TryParseRange(reference, out var startRow, out var startColumn, out var endRow, out var endColumn)) continue;

            var (bandStart, bandEnd) = operation.Axis == ShiftAxis.Row ? (startRow, endRow) : (startColumn, endColumn);
            var intersects = operation.IsInsert
                ? operation.Index > bandStart && operation.Index <= bandEnd
                : operation.Index <= bandEnd && operation.Index + operation.Count - 1 >= bandStart;
            if (!intersects) continue;

            var name = (string?)table.Root?.Attribute("displayName") ?? Path.GetFileName(partPath);
            throw new ArgumentException(
                $"Worksheet '{sheet.Name}' has structured table '{name}' covering {reference}. " +
                "Inserting or deleting inside a table would invalidate its column definitions — convert it to a range first, or edit it in Excel.");
        }
    }

    private static void ShiftTableRanges(ZipArchive archive, IDictionary<string, XDocument> tableDocuments,
        SheetPart sheet, XDocument worksheet, ShiftOperation operation)
    {
        foreach (var (_, table) in ReadTables(archive, tableDocuments, sheet, worksheet))
        {
            if (table.Root is null) continue;
            ShiftRangeAttribute(table.Root, "ref", operation, removeWhenGone: false);
            ShiftRangeAttribute(table.Root.Element(SpreadsheetNs + "autoFilter"), "ref", operation, removeWhenGone: false);
        }
    }

    private static IEnumerable<(string PartPath, XDocument Table)> ReadTables(ZipArchive archive,
        IDictionary<string, XDocument> tableDocuments, SheetPart sheet, XDocument worksheet)
    {
        var parts = worksheet.Root?.Element(SpreadsheetNs + "tableParts");
        if (parts is null) yield break;

        var targets = ReadRelationshipTargets(archive, sheet.PartPath);
        if (targets.Count == 0) yield break;

        foreach (var part in parts.Elements(SpreadsheetNs + "tablePart"))
        {
            var id = (string?)part.Attribute(RelationshipNs + "id");
            if (id is null || !targets.TryGetValue(id, out var partPath)) continue;

            if (!tableDocuments.TryGetValue(partPath, out var document))
            {
                var entry = archive.GetEntry(partPath);
                if (entry is null) continue;
                document = ReadXml(entry, preserveWhitespace: true);
                tableDocuments[partPath] = document;
            }
            yield return (partPath, document);
        }
    }

    private static bool TryParseRange(string reference, out int startRow, out int startColumn, out int endRow, out int endColumn)
    {
        startRow = startColumn = endRow = endColumn = 0;
        var parts = reference.Replace("$", string.Empty).Split(':');
        if (parts.Length is < 1 or > 2) return false;
        if (!TryParseCellReference(parts[0].ToUpperInvariant(), out startRow, out startColumn)) return false;
        if (parts.Length == 1)
        {
            endRow = startRow;
            endColumn = startColumn;
            return true;
        }
        return TryParseCellReference(parts[1].ToUpperInvariant(), out endRow, out endColumn);
    }

    private static void EnsureDistinctWorkbookPaths(string inputPath, string outputPath)
    {
        EnsureWorkbookExtension(inputPath);
        EnsureWorkbookExtension(outputPath);
        EnsureDistinctPaths(inputPath, outputPath);
    }
}
