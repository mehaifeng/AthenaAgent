using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Athena.UI.Services.Ooxml;

namespace Athena.UI.Services.Spreadsheets;

/// <summary>
/// Dependency-free OOXML workbook operations used by Athena's built-in xlsx skill.
/// The service deliberately supports bounded, surgical operations rather than a
/// lossy unzip/pretty-print/repack workflow.
/// </summary>
public sealed partial class XlsxPackageService : OoxmlPackageService
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>Child order of CT_Worksheet; elements written out of sequence make Excel ask to repair the file.</summary>
    private static readonly string[] WorksheetChildOrder =
    [
        "sheetPr", "dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "sheetCalcPr",
        "sheetProtection", "protectedRanges", "scenarios", "autoFilter", "sortState", "dataConsolidate",
        "customSheetViews", "mergeCells", "phoneticPr", "conditionalFormatting", "dataValidations",
        "hyperlinks", "printOptions", "pageMargins", "pageSetup", "headerFooter", "rowBreaks", "colBreaks",
        "customProperties", "cellWatches", "ignoredErrors", "smartTags", "drawing", "drawingHF", "picture",
        "oleObjects", "controls", "webPublishItems", "tableParts", "extLst"
    ];

    private const int MaxUpdates = 5_000;

    public object Inspect(string path, string? requestedSheet, int maxRows, int maxColumns, int startRow = 1, int startColumn = 1)
    {
        maxRows = Math.Clamp(maxRows, 1, 200);
        maxColumns = Math.Clamp(maxColumns, 1, 100);
        startRow = Math.Clamp(startRow, 1, 1_048_576);
        startColumn = Math.Clamp(startColumn, 1, 16_384);
        var endRow = Math.Min(1_048_576L, (long)startRow + maxRows - 1);
        var endColumn = Math.Min(16_384L, (long)startColumn + maxColumns - 1);

        using var archive = OpenChecked(path, ZipArchiveMode.Read);
        var sheets = ReadSheetMap(archive);
        var workbookSheetCount = sheets.Count;
        if (!string.IsNullOrWhiteSpace(requestedSheet))
        {
            sheets = sheets.Where(sheet => sheet.Name.Equals(requestedSheet, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sheets.Count == 0) throw new InvalidOperationException($"Worksheet not found: {requestedSheet}");
        }

        var sharedStrings = ReadSharedStrings(archive);
        var formats = ReadNumberFormats(archive);
        var inspections = new List<object>();
        foreach (var sheet in sheets)
        {
            var document = ReadXml(RequiredEntry(archive, sheet.PartPath));
            var cells = document.Descendants(SpreadsheetNs + "c").ToList();
            var formulaCount = cells.Count(cell => cell.Element(SpreadsheetNs + "f") is not null);
            var errorCount = cells.Count(cell => (string?)cell.Attribute("t") == "e");
            var usedMaxRow = 0;
            var usedMaxColumn = 0;
            var preview = new List<object>();

            foreach (var cell in cells)
            {
                var address = (string?)cell.Attribute("r") ?? string.Empty;
                if (!TryParseCellReference(address, out var row, out var column)) continue;
                usedMaxRow = Math.Max(usedMaxRow, row);
                usedMaxColumn = Math.Max(usedMaxColumn, column);
                if (row < startRow || row > endRow || column < startColumn || column > endColumn) continue;

                var styleIndex = (int?)cell.Attribute("s");
                var value = ReadCellValue(cell, sharedStrings);
                preview.Add(new
                {
                    address,
                    value,
                    // Excel keeps dates as serial numbers; surface the readable form so the model never guesses.
                    text = value is double number ? formats.Format(styleIndex, number) : null,
                    formula = (string?)cell.Element(SpreadsheetNs + "f"),
                    styleIndex
                });
            }

            var merges = document.Root?.Element(SpreadsheetNs + "mergeCells")?.Elements(SpreadsheetNs + "mergeCell")
                .Select(merge => (string?)merge.Attribute("ref"))
                .Where(reference => reference is not null)
                .Take(200)
                .ToList() ?? [];

            inspections.Add(new
            {
                sheet.Name,
                sheet.PartPath,
                maxRow = usedMaxRow,
                maxColumn = usedMaxColumn,
                formulaCount,
                errorCellCount = errorCount,
                mergedRanges = merges,
                window = new { startRow, endRow, startColumn, endColumn },
                hasMoreRows = usedMaxRow > endRow,
                hasMoreColumns = usedMaxColumn > endColumn,
                nextStartRow = usedMaxRow > endRow ? endRow + 1 : (long?)null,
                nextStartColumn = usedMaxColumn > endColumn ? endColumn + 1 : (long?)null,
                preview
            });
        }

        return new
        {
            path,
            sheetCount = workbookSheetCount,
            sheets = inspections,
            features = DetectFeatures(archive),
            pagingHint = "Preview windows are bounded. Re-run with startRow/startColumn to page through a larger sheet, or use convert_spreadsheet to export the whole sheet as CSV."
        };
    }

    public object Validate(string path)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var formulaCount = 0;
        var emptyFormulaCacheCount = 0;

        try
        {
            using var archive = OpenChecked(path, ZipArchiveMode.Read);
            foreach (var required in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels" })
            {
                if (archive.GetEntry(required) is null) errors.Add($"Missing required OOXML part: {required}");
            }

            foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                XDocument document;
                try
                {
                    document = ReadXml(entry);
                }
                catch (Exception ex)
                {
                    errors.Add($"Invalid XML in {entry.FullName}: {ex.Message}");
                    continue;
                }

                if (entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    var sourcePart = SourcePartForRelationships(entry.FullName);
                    foreach (var relationship in document.Root?.Elements(PackageRelationshipNs + "Relationship") ?? [])
                    {
                        if (string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
                        var target = (string?)relationship.Attribute("Target");
                        if (string.IsNullOrWhiteSpace(target)) continue;
                        var resolved = ResolvePartPath(sourcePart, target);
                        if (archive.GetEntry(resolved) is null) errors.Add($"Broken relationship in {entry.FullName}: {target} -> {resolved}");
                    }
                }

                if (!entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)) continue;
                errors.AddRange(FindMergeProblems(document, entry.FullName));
                foreach (var cell in document.Descendants(SpreadsheetNs + "c"))
                {
                    var formula = (string?)cell.Element(SpreadsheetNs + "f");
                    if (formula is null) continue;
                    formulaCount++;
                    if (formula.Contains("#REF!", StringComparison.OrdinalIgnoreCase))
                        errors.Add($"Broken formula reference at {entry.FullName}!{(string?)cell.Attribute("r")}: {formula}");
                    if (cell.Element(SpreadsheetNs + "v") is null || string.IsNullOrEmpty(cell.Element(SpreadsheetNs + "v")?.Value))
                        emptyFormulaCacheCount++;
                }
            }

            var features = DetectFeatures(archive);
            if (features.Count > 0)
                warnings.Add("Workbook contains advanced features. Native cell edits preserve untouched ZIP parts, but feature semantics require Excel/LibreOffice verification: " + string.Join(", ", features));
            if (emptyFormulaCacheCount > 0)
                warnings.Add($"{emptyFormulaCacheCount} formula cells have no cached result and must be recalculated by a spreadsheet engine.");
            warnings.Add("Validation is structural and static; it does not execute formulas or render the workbook.");

            return new
            {
                valid = errors.Count == 0,
                errors,
                warnings,
                formulaCount,
                emptyFormulaCacheCount,
                dynamicValidationPerformed = false,
                features
            };
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            return new
            {
                valid = false,
                errors,
                warnings,
                formulaCount,
                emptyFormulaCacheCount,
                dynamicValidationPerformed = false,
                features = Array.Empty<string>()
            };
        }
    }

    /// <summary>
    /// Overlapping or single-cell merges are the most common way a hand-built worksheet makes
    /// Excel show its "unreadable content" repair prompt, so they are reported as hard errors.
    /// </summary>
    private static IEnumerable<string> FindMergeProblems(XDocument worksheet, string partName)
    {
        var merges = worksheet.Root?.Element(SpreadsheetNs + "mergeCells")?.Elements(SpreadsheetNs + "mergeCell").ToList();
        if (merges is null || merges.Count == 0) yield break;

        var parsed = new List<(string Reference, int StartRow, int StartColumn, int EndRow, int EndColumn)>();
        foreach (var merge in merges)
        {
            var reference = (string?)merge.Attribute("ref");
            if (reference is null || !TryParseRange(reference, out var startRow, out var startColumn, out var endRow, out var endColumn))
            {
                yield return $"Invalid merged range in {partName}: {reference ?? "(missing ref)"}";
                continue;
            }
            if (startRow == endRow && startColumn == endColumn)
                yield return $"Merged range covers a single cell in {partName}: {reference}";
            parsed.Add((reference, startRow, startColumn, endRow, endColumn));
        }

        for (var first = 0; first < parsed.Count; first++)
        {
            for (var second = first + 1; second < parsed.Count; second++)
            {
                var left = parsed[first];
                var right = parsed[second];
                if (left.StartRow <= right.EndRow && left.EndRow >= right.StartRow &&
                    left.StartColumn <= right.EndColumn && left.EndColumn >= right.StartColumn)
                    yield return $"Overlapping merged ranges in {partName}: {left.Reference} and {right.Reference}";
            }
        }
    }

    public void Create(string outputPath, string workbookJson, bool overwrite)
    {
        if (!Path.GetExtension(outputPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("New workbooks must use the .xlsx extension. Use edit_spreadsheet to preserve an existing .xlsm package.");
        EnsureCanWrite(outputPath, overwrite);
        using var json = JsonDocument.Parse(workbookJson, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip });
        if (!json.RootElement.TryGetProperty("sheets", out var sheetsElement) || sheetsElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("workbookJson must contain a non-empty 'sheets' array.");

        var sheetSpecs = sheetsElement.EnumerateArray().ToList();
        if (sheetSpecs.Count is < 1 or > 100) throw new ArgumentException("A workbook must contain 1-100 worksheets.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < sheetSpecs.Count; index++)
        {
            var name = GetRequiredString(sheetSpecs[index], "name");
            ValidateSheetName(name);
            if (!names.Add(name)) throw new ArgumentException($"Duplicate worksheet name: {name}");
        }

        var styles = new XlsxStyleLibrary(XDocument.Parse(StylesXml));
        if (json.RootElement.TryGetProperty("styles", out var styleSpecs))
            styles.RegisterNamedStyles(styleSpecs, IsBuiltInStyleAlias);

        // Worksheet XML is built before the package so a bad style reference fails before any file is written.
        var worksheets = sheetSpecs.Select(spec => WorksheetXml(spec, styles)).ToList();

        AtomicWrite(outputPath, overwrite, temporaryPath =>
        {
            using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
            WriteTextEntry(archive, "[Content_Types].xml", BuildContentTypes(sheetSpecs.Count));
            WriteTextEntry(archive, "_rels/.rels", WorkbookRootRelationshipsXml);
            WriteTextEntry(archive, "docProps/core.xml", CorePropertiesXml());
            WriteTextEntry(archive, "docProps/app.xml", AppPropertiesXml(sheetSpecs.Select(spec => GetRequiredString(spec, "name"))));
            WriteTextEntry(archive, "xl/workbook.xml", WorkbookXml(sheetSpecs.Select(spec => GetRequiredString(spec, "name"))));
            WriteTextEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationshipsXml(sheetSpecs.Count));
            WriteTextEntry(archive, "xl/styles.xml", SerializeStyles(styles.Document));
            for (var index = 0; index < worksheets.Count; index++)
                WriteTextEntry(archive, $"xl/worksheets/sheet{index + 1}.xml", worksheets[index]);
        });
    }

    public void Edit(string inputPath, string outputPath, string updatesJson, bool overwrite)
    {
        EnsureDistinctWorkbookPaths(inputPath, outputPath);
        EnsureCanWrite(outputPath, overwrite);

        using var json = JsonDocument.Parse(updatesJson, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip });
        var updatesElement = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement
            : json.RootElement.TryGetProperty("updates", out var nested) ? nested : default;
        if (updatesElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("updatesJson must be an array or an object containing an 'updates' array.");
        var updates = updatesElement.EnumerateArray().ToList();
        if (updates.Count is < 1 or > MaxUpdates) throw new ArgumentException($"Provide 1-{MaxUpdates} cell updates per call.");

        // Validate package bounds and XML before creating output.
        using (var checkedArchive = OpenChecked(inputPath, ZipArchiveMode.Read))
            _ = ReadSheetMap(checkedArchive);

        AtomicWrite(outputPath, overwrite, temporaryPath =>
        {
            File.Copy(inputPath, temporaryPath, overwrite: true);
            using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false, Encoding.UTF8);
            var sheetMap = ReadSheetMap(archive);
            var documents = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
            var formulasChanged = false;
            XlsxStyleLibrary? styles = null;

            XlsxStyleLibrary EnsureStyles()
            {
                if (styles is not null) return styles;
                var entry = archive.GetEntry("xl/styles.xml")
                    ?? throw new InvalidDataException("The workbook has no styles part, so a new style cannot be registered.");
                styles = new XlsxStyleLibrary(ReadXml(entry, preserveWhitespace: true));
                return styles;
            }

            foreach (var update in updates)
            {
                var sheetName = GetRequiredString(update, "sheet");
                var sheet = sheetMap.FirstOrDefault(candidate => candidate.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Worksheet not found: {sheetName}");
                if (!documents.TryGetValue(sheet.PartPath, out var document))
                {
                    document = ReadXml(RequiredEntry(archive, sheet.PartPath), preserveWhitespace: true);
                    documents[sheet.PartPath] = document;
                }

                var worksheetRoot = document.Root ?? throw new InvalidDataException($"Worksheet has no root element: {sheetName}");
                if (update.TryGetProperty("merge", out var mergeElement))
                {
                    MergeRange(worksheetRoot, ReadRangeProperty(mergeElement, "merge"));
                    continue;
                }

                if (update.TryGetProperty("unmerge", out var unmergeElement))
                {
                    UnmergeRange(worksheetRoot, ReadRangeProperty(unmergeElement, "unmerge"));
                    continue;
                }

                var cellReference = GetRequiredString(update, "cell").ToUpperInvariant();
                if (!TryParseCellReference(cellReference, out var rowNumber, out var columnNumber))
                    throw new ArgumentException($"Invalid cell reference: {cellReference}");

                var clear = update.TryGetProperty("clear", out var clearElement) && clearElement.ValueKind == JsonValueKind.True;
                var hasFormula = update.TryGetProperty("formula", out var formulaElement) && formulaElement.ValueKind != JsonValueKind.Null;
                var hasValue = update.TryGetProperty("value", out var valueElement);
                var operationCount = (clear ? 1 : 0) + (hasFormula ? 1 : 0) + (hasValue ? 1 : 0);
                if (operationCount != 1) throw new ArgumentException($"{sheetName}!{cellReference} must specify exactly one of clear, formula, or value.");

                var sheetData = document.Root?.Element(SpreadsheetNs + "sheetData")
                    ?? throw new InvalidDataException($"Worksheet has no sheetData: {sheetName}");
                var row = GetOrCreateRow(sheetData, rowNumber);
                var cell = row.Elements(SpreadsheetNs + "c").FirstOrDefault(candidate => string.Equals((string?)candidate.Attribute("r"), cellReference, StringComparison.OrdinalIgnoreCase));

                if (clear)
                {
                    if (cell?.Element(SpreadsheetNs + "f") is not null) formulasChanged = true;
                    cell?.Remove();
                    continue;
                }

                cell ??= InsertCell(row, cellReference, columnNumber);
                ApplyStyle(update, sheetMap, documents, archive, cell, EnsureStyles);
                cell.Elements().Remove();

                if (hasFormula)
                {
                    var formula = formulaElement.GetString()?.Trim() ?? throw new ArgumentException($"Formula is empty at {sheetName}!{cellReference}.");
                    if (formula.StartsWith('=')) formula = formula[1..];
                    if (formula.Length == 0) throw new ArgumentException($"Formula is empty at {sheetName}!{cellReference}.");
                    cell.Attribute("t")?.Remove();
                    cell.Add(new XElement(SpreadsheetNs + "f", formula), new XElement(SpreadsheetNs + "v"));
                    formulasChanged = true;
                }
                else
                {
                    SetCellValue(cell, valueElement);
                }
            }

            foreach (var pair in documents)
            {
                UpdateDimension(pair.Value);
                ReplaceXmlEntry(archive, pair.Key, pair.Value);
            }

            if (styles is not null) ReplaceXmlEntry(archive, "xl/styles.xml", styles.Document);

            if (formulasChanged)
            {
                RemoveCalculationChain(archive);
                SetFullCalculation(archive);
            }
        });
    }

    private static void ApplyStyle(JsonElement update, IReadOnlyList<SheetPart> sheetMap,
        IDictionary<string, XDocument> documents, ZipArchive archive, XElement cell, Func<XlsxStyleLibrary> ensureStyles)
    {
        // An inline style object registers a new cell format in this workbook's own styles part,
        // which is the only safe way to style a workbook Athena did not create.
        if (update.TryGetProperty("style", out var styleSpec) && styleSpec.ValueKind == JsonValueKind.Object)
        {
            cell.SetAttributeValue("s", ensureStyles().Register(styleSpec));
            return;
        }

        if (update.TryGetProperty("styleIndex", out var styleElement))
        {
            if (!styleElement.TryGetInt32(out var styleIndex) || styleIndex < 0) throw new ArgumentException("styleIndex must be a non-negative integer.");
            cell.SetAttributeValue("s", styleIndex);
            return;
        }

        if (!update.TryGetProperty("copyStyleFrom", out var copyElement) || copyElement.ValueKind != JsonValueKind.String) return;
        var source = copyElement.GetString()!.Trim();
        var separator = source.LastIndexOf('!');
        var sourceSheetName = separator > 0 ? source[..separator].Trim('\'', ' ') : GetRequiredString(update, "sheet");
        var sourceCellReference = (separator > 0 ? source[(separator + 1)..] : source).ToUpperInvariant();
        if (!TryParseCellReference(sourceCellReference, out _, out _)) throw new ArgumentException($"Invalid copyStyleFrom reference: {source}");
        var sourceSheet = sheetMap.FirstOrDefault(candidate => candidate.Name.Equals(sourceSheetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Worksheet not found in copyStyleFrom: {sourceSheetName}");
        if (!documents.TryGetValue(sourceSheet.PartPath, out var sourceDocument))
        {
            sourceDocument = ReadXml(RequiredEntry(archive, sourceSheet.PartPath), preserveWhitespace: true);
            documents[sourceSheet.PartPath] = sourceDocument;
        }
        var sourceCell = sourceDocument.Descendants(SpreadsheetNs + "c")
            .FirstOrDefault(candidate => string.Equals((string?)candidate.Attribute("r"), sourceCellReference, StringComparison.OrdinalIgnoreCase));
        var style = sourceCell?.Attribute("s")?.Value;
        if (style is null) cell.Attribute("s")?.Remove();
        else cell.SetAttributeValue("s", style);
    }

    private static string ReadRangeProperty(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            throw new ArgumentException($"'{property}' must be a range such as 'A1:C1'.");
        return element.GetString()!.Trim().ToUpperInvariant().Replace("$", string.Empty);
    }

    /// <summary>
    /// Adds a merged range. Excel keeps the value of the top-left cell only, so every other cell in
    /// the range is stripped of its content while its style is preserved.
    /// </summary>
    private static void MergeRange(XElement root, string range)
    {
        if (!TryParseRange(range, out var startRow, out var startColumn, out var endRow, out var endColumn))
            throw new ArgumentException($"Invalid merge range: {range}");
        if (startRow == endRow && startColumn == endColumn)
            throw new ArgumentException($"A merge must cover more than one cell: {range}");
        if (endRow < startRow || endColumn < startColumn)
            throw new ArgumentException($"Merge range must be written top-left to bottom-right: {range}");

        var mergeCells = GetOrCreateWorksheetChild(root, "mergeCells");

        var normalized = $"{ColumnName(startColumn)}{startRow}:{ColumnName(endColumn)}{endRow}";
        foreach (var existing in mergeCells.Elements(SpreadsheetNs + "mergeCell"))
        {
            var reference = (string?)existing.Attribute("ref");
            if (reference is null || !TryParseRange(reference, out var otherStartRow, out var otherStartColumn, out var otherEndRow, out var otherEndColumn)) continue;
            var overlaps = startRow <= otherEndRow && endRow >= otherStartRow && startColumn <= otherEndColumn && endColumn >= otherStartColumn;
            if (overlaps) throw new ArgumentException($"Merge range {normalized} overlaps the existing merge {reference}.");
        }

        mergeCells.Add(new XElement(SpreadsheetNs + "mergeCell", new XAttribute("ref", normalized)));
        mergeCells.SetAttributeValue("count", mergeCells.Elements(SpreadsheetNs + "mergeCell").Count());

        foreach (var cell in root.Descendants(SpreadsheetNs + "c").ToList())
        {
            if (!TryParseCellReference((string?)cell.Attribute("r") ?? string.Empty, out var row, out var column)) continue;
            if (row < startRow || row > endRow || column < startColumn || column > endColumn) continue;
            if (row == startRow && column == startColumn) continue;
            cell.Elements().Remove();
            cell.Attribute("t")?.Remove();
        }
    }

    private static void UnmergeRange(XElement root, string range)
    {
        if (!TryParseRange(range, out var startRow, out var startColumn, out var endRow, out var endColumn))
            throw new ArgumentException($"Invalid unmerge range: {range}");

        var mergeCells = root.Element(SpreadsheetNs + "mergeCells")
            ?? throw new ArgumentException($"The worksheet has no merged ranges, so {range} cannot be unmerged.");
        var normalized = $"{ColumnName(startColumn)}{startRow}:{ColumnName(endColumn)}{endRow}";
        var match = mergeCells.Elements(SpreadsheetNs + "mergeCell").FirstOrDefault(element =>
        {
            var reference = (string?)element.Attribute("ref");
            return reference is not null
                && TryParseRange(reference, out var otherStartRow, out var otherStartColumn, out var otherEndRow, out var otherEndColumn)
                && otherStartRow == startRow && otherStartColumn == startColumn && otherEndRow == endRow && otherEndColumn == endColumn;
        }) ?? throw new ArgumentException($"No merged range exactly matches {normalized}.");

        match.Remove();
        if (!mergeCells.Elements(SpreadsheetNs + "mergeCell").Any()) mergeCells.Remove();
        else mergeCells.SetAttributeValue("count", mergeCells.Elements(SpreadsheetNs + "mergeCell").Count());
    }

    private static XElement GetOrCreateWorksheetChild(XElement root, string name) =>
        GetOrCreateOrderedChild(root, SpreadsheetNs, WorksheetChildOrder, name);

    private static string SerializeStyles(XDocument document) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + document.Root!.ToString(SaveOptions.DisableFormatting);

    private static void SetCellValue(XElement cell, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                cell.Attribute("t")?.Remove();
                cell.Add(new XElement(SpreadsheetNs + "v", value.GetRawText()));
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                cell.SetAttributeValue("t", "b");
                cell.Add(new XElement(SpreadsheetNs + "v", value.GetBoolean() ? "1" : "0"));
                break;
            case JsonValueKind.Null:
                cell.Attribute("t")?.Remove();
                break;
            case JsonValueKind.String:
                cell.SetAttributeValue("t", "inlineStr");
                var text = value.GetString() ?? string.Empty;
                var textElement = new XElement(SpreadsheetNs + "t", text);
                if (text.Length != text.Trim().Length) textElement.SetAttributeValue(XNamespace.Xml + "space", "preserve");
                cell.Add(new XElement(SpreadsheetNs + "is", textElement));
                break;
            default:
                throw new ArgumentException("Cell values must be string, number, boolean, or null.");
        }
    }

    private static XElement GetOrCreateRow(XElement sheetData, int rowNumber)
    {
        var existing = sheetData.Elements(SpreadsheetNs + "row").FirstOrDefault(row => (int?)row.Attribute("r") == rowNumber);
        if (existing is not null) return existing;
        var created = new XElement(SpreadsheetNs + "row", new XAttribute("r", rowNumber));
        var following = sheetData.Elements(SpreadsheetNs + "row").FirstOrDefault(row => ((int?)row.Attribute("r") ?? int.MaxValue) > rowNumber);
        if (following is null) sheetData.Add(created); else following.AddBeforeSelf(created);
        return created;
    }

    private static XElement InsertCell(XElement row, string reference, int columnNumber)
    {
        var created = new XElement(SpreadsheetNs + "c", new XAttribute("r", reference));
        var following = row.Elements(SpreadsheetNs + "c").FirstOrDefault(cell =>
            TryParseCellReference((string?)cell.Attribute("r") ?? string.Empty, out _, out var candidateColumn) && candidateColumn > columnNumber);
        if (following is null) row.Add(created); else following.AddBeforeSelf(created);
        return created;
    }

    private static void UpdateDimension(XDocument document)
    {
        if (document.Root is null) return;
        var maxRow = 0;
        var maxColumn = 0;
        foreach (var cell in document.Descendants(SpreadsheetNs + "c"))
        {
            if (!TryParseCellReference((string?)cell.Attribute("r") ?? string.Empty, out var row, out var column)) continue;
            maxRow = Math.Max(maxRow, row);
            maxColumn = Math.Max(maxColumn, column);
        }
        var reference = maxRow == 0 ? "A1" : $"A1:{ColumnName(maxColumn)}{maxRow}";
        var dimension = document.Root.Element(SpreadsheetNs + "dimension");
        if (dimension is null)
        {
            dimension = new XElement(SpreadsheetNs + "dimension", new XAttribute("ref", reference));
            document.Root.AddFirst(dimension);
        }
        else dimension.SetAttributeValue("ref", reference);
    }

    private static void RemoveCalculationChain(ZipArchive archive)
    {
        archive.GetEntry("xl/calcChain.xml")?.Delete();
        var relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (relationshipsEntry is not null)
        {
            var relationships = ReadXml(relationshipsEntry, preserveWhitespace: true);
            relationships.Root?.Elements(PackageRelationshipNs + "Relationship")
                .Where(element => ((string?)element.Attribute("Type"))?.EndsWith("/calcChain", StringComparison.OrdinalIgnoreCase) == true)
                .Remove();
            ReplaceXmlEntry(archive, relationshipsEntry.FullName, relationships);
        }
        var contentTypesEntry = archive.GetEntry("[Content_Types].xml");
        if (contentTypesEntry is not null)
        {
            var contentTypes = ReadXml(contentTypesEntry, preserveWhitespace: true);
            contentTypes.Root?.Elements(ContentTypesNs + "Override")
                .Where(element => string.Equals((string?)element.Attribute("PartName"), "/xl/calcChain.xml", StringComparison.OrdinalIgnoreCase))
                .Remove();
            ReplaceXmlEntry(archive, contentTypesEntry.FullName, contentTypes);
        }
    }

    private static void SetFullCalculation(ZipArchive archive)
    {
        var entry = RequiredEntry(archive, "xl/workbook.xml");
        var document = ReadXml(entry, preserveWhitespace: true);
        var calcPr = document.Root?.Element(SpreadsheetNs + "calcPr");
        if (calcPr is null)
        {
            calcPr = new XElement(SpreadsheetNs + "calcPr");
            var extLst = document.Root?.Element(SpreadsheetNs + "extLst");
            if (extLst is null) document.Root?.Add(calcPr); else extLst.AddBeforeSelf(calcPr);
        }
        calcPr.SetAttributeValue("calcMode", "auto");
        calcPr.SetAttributeValue("fullCalcOnLoad", "1");
        calcPr.SetAttributeValue("forceFullCalc", "1");
        ReplaceXmlEntry(archive, entry.FullName, document);
    }

    private static List<SheetPart> ReadSheetMap(ZipArchive archive)
    {
        var workbook = ReadXml(RequiredEntry(archive, "xl/workbook.xml"));
        var targets = ReadRelationshipTargets(archive, "xl/workbook.xml");
        var result = new List<SheetPart>();
        foreach (var sheet in workbook.Descendants(SpreadsheetNs + "sheet"))
        {
            var name = (string?)sheet.Attribute("name");
            var id = (string?)sheet.Attribute(RelationshipNs + "id");
            if (name is null || id is null || !targets.TryGetValue(id, out var target)) throw new InvalidDataException("Workbook contains an unresolved worksheet relationship.");
            result.Add(new SheetPart(name, target));
        }
        if (result.Count == 0) throw new InvalidDataException("Workbook contains no worksheets.");
        return result;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        var document = ReadXml(entry);
        return document.Descendants(SpreadsheetNs + "si")
            .Select(item => string.Concat(item.Descendants(SpreadsheetNs + "t").Select(text => text.Value)))
            .ToList();
    }

    private static object? ReadCellValue(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr") return string.Concat(cell.Descendants(SpreadsheetNs + "t").Select(text => text.Value));
        var raw = (string?)cell.Element(SpreadsheetNs + "v");
        if (raw is null) return null;
        if (type == "s" && int.TryParse(raw, out var index) && index >= 0 && index < sharedStrings.Count) return sharedStrings[index];
        if (type == "b") return raw == "1";
        if (type is null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        return raw;
    }

    private static List<string> DetectFeatures(ZipArchive archive)
    {
        var features = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in archive.Entries.Select(entry => entry.FullName))
        {
            if (name.Equals("xl/vbaProject.bin", StringComparison.OrdinalIgnoreCase)) features.Add("macros");
            else if (name.StartsWith("xl/pivot", StringComparison.OrdinalIgnoreCase)) features.Add("pivot tables/caches");
            else if (name.StartsWith("xl/charts/", StringComparison.OrdinalIgnoreCase)) features.Add("charts");
            else if (name.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase)) features.Add("external links");
            else if (name.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)) features.Add("digital signatures");
            else if (name.StartsWith("xl/tables/", StringComparison.OrdinalIgnoreCase)) features.Add("structured tables");
            else if (name.Contains("slicer", StringComparison.OrdinalIgnoreCase)) features.Add("slicers");
            else if (name.StartsWith("xl/drawings/", StringComparison.OrdinalIgnoreCase)) features.Add("drawings/images");
        }
        return features.ToList();
    }

    private static ZipArchive OpenChecked(string path, ZipArchiveMode mode)
    {
        EnsureWorkbookExtension(path);
        return OpenPackage(path, mode);
    }

    private static void EnsureWorkbookExtension(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only .xlsx and .xlsm OOXML workbooks are supported.");
    }

    private static string GetRequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"Missing required string property '{property}'.");
        return value.GetString()!;
    }

    private static void ValidateSheetName(string name)
    {
        if (name.Length > 31 || name.IndexOfAny(['\\', '/', '*', '?', ':', '[', ']']) >= 0 || name.StartsWith('\'') || name.EndsWith('\''))
            throw new ArgumentException($"Invalid Excel worksheet name: {name}");
    }

    private static bool TryParseCellReference(string reference, out int row, out int column)
    {
        row = 0;
        column = 0;
        if (string.IsNullOrWhiteSpace(reference)) return false;
        var index = 0;
        while (index < reference.Length && reference[index] is >= 'A' and <= 'Z')
        {
            column = checked(column * 26 + reference[index] - 'A' + 1);
            index++;
        }
        if (index == 0 || index == reference.Length || column > 16_384) return false;
        if (!int.TryParse(reference.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture, out row)) return false;
        return row is >= 1 and <= 1_048_576;
    }

    private static string ColumnName(int column)
    {
        var builder = new StringBuilder();
        while (column > 0)
        {
            column--;
            builder.Insert(0, (char)('A' + column % 26));
            column /= 26;
        }
        return builder.ToString();
    }

    private static string WorksheetXml(JsonElement specification, XlsxStyleLibrary styles)
    {
        var rows = specification.TryGetProperty("rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array
            ? rowsElement.EnumerateArray().ToList()
            : [];
        if (rows.Count > 100_000) throw new ArgumentException("A worksheet cannot contain more than 100,000 supplied rows.");
        var sheetData = new XElement(SpreadsheetNs + "sheetData");
        var maxColumn = 1;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            if (rows[rowIndex].ValueKind != JsonValueKind.Array) throw new ArgumentException("Each rows item must be an array of cells.");
            var cells = rows[rowIndex].EnumerateArray().ToList();
            if (cells.Count > 16_384) throw new ArgumentException("A row cannot contain more than 16,384 cells.");
            maxColumn = Math.Max(maxColumn, cells.Count);
            var row = new XElement(SpreadsheetNs + "row", new XAttribute("r", rowIndex + 1));
            for (var columnIndex = 0; columnIndex < cells.Count; columnIndex++)
            {
                var cellSpec = cells[columnIndex];
                if (cellSpec.ValueKind == JsonValueKind.Null) continue;
                var cell = new XElement(SpreadsheetNs + "c", new XAttribute("r", $"{ColumnName(columnIndex + 1)}{rowIndex + 1}"));
                if (cellSpec.ValueKind == JsonValueKind.Object)
                {
                    if (cellSpec.TryGetProperty("style", out var style) && style.ValueKind == JsonValueKind.String)
                        cell.SetAttributeValue("s", ResolveStyleIndex(style.GetString()!, styles));
                    else if (cellSpec.TryGetProperty("style", out var inlineStyle) && inlineStyle.ValueKind == JsonValueKind.Object)
                        cell.SetAttributeValue("s", styles.Register(inlineStyle));
                    else if (cellSpec.TryGetProperty("styleIndex", out var styleIndex) && styleIndex.TryGetInt32(out var explicitStyle) && explicitStyle >= 0)
                        cell.SetAttributeValue("s", explicitStyle);
                    if (cellSpec.TryGetProperty("formula", out var formulaElement) && formulaElement.ValueKind == JsonValueKind.String)
                    {
                        var formula = formulaElement.GetString()!.Trim();
                        if (formula.StartsWith('=')) formula = formula[1..];
                        if (formula.Length == 0) throw new ArgumentException($"Formula is empty at {cell.Attribute("r")?.Value}.");
                        cell.Add(new XElement(SpreadsheetNs + "f", formula), new XElement(SpreadsheetNs + "v"));
                    }
                    else if (cellSpec.TryGetProperty("value", out var value)) SetCellValue(cell, value);
                    else throw new ArgumentException($"Cell object at {cell.Attribute("r")?.Value} needs a value or formula.");
                }
                else SetCellValue(cell, cellSpec);
                row.Add(cell);
            }
            sheetData.Add(row);
        }

        var root = new XElement(SpreadsheetNs + "worksheet");
        root.Add(new XElement(SpreadsheetNs + "dimension", new XAttribute("ref", rows.Count == 0 ? "A1" : $"A1:{ColumnName(maxColumn)}{Math.Max(1, rows.Count)}")));
        var freezeRows = specification.TryGetProperty("freezeRows", out var freezeElement) && freezeElement.TryGetInt32(out var freezeValue) ? Math.Clamp(freezeValue, 0, 1000) : 0;
        if (freezeRows > 0)
        {
            root.Add(new XElement(SpreadsheetNs + "sheetViews",
                new XElement(SpreadsheetNs + "sheetView", new XAttribute("workbookViewId", 0),
                    new XElement(SpreadsheetNs + "pane", new XAttribute("ySplit", freezeRows), new XAttribute("topLeftCell", $"A{freezeRows + 1}"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))));
        }
        if (specification.TryGetProperty("columnWidths", out var widthsElement) && widthsElement.ValueKind == JsonValueKind.Array)
        {
            var cols = new XElement(SpreadsheetNs + "cols");
            var index = 1;
            foreach (var widthElement in widthsElement.EnumerateArray())
            {
                if (!widthElement.TryGetDouble(out var width) || width is < 1 or > 255) throw new ArgumentException("Column widths must be numbers from 1 to 255.");
                cols.Add(new XElement(SpreadsheetNs + "col", new XAttribute("min", index), new XAttribute("max", index), new XAttribute("width", width.ToString(CultureInfo.InvariantCulture)), new XAttribute("customWidth", 1)));
                index++;
            }
            root.Add(cols);
        }
        root.Add(sheetData);
        if (specification.TryGetProperty("autoFilter", out var filter) && filter.ValueKind == JsonValueKind.True && rows.Count > 0)
            root.Add(new XElement(SpreadsheetNs + "autoFilter", new XAttribute("ref", $"A1:{ColumnName(maxColumn)}{rows.Count}")));

        if (specification.TryGetProperty("merges", out var merges) && merges.ValueKind == JsonValueKind.Array)
        {
            foreach (var merge in merges.EnumerateArray())
                MergeRange(root, ReadRangeProperty(merge, "merges"));
        }

        root.Add(new XElement(SpreadsheetNs + "pageMargins", new XAttribute("left", "0.7"), new XAttribute("right", "0.7"), new XAttribute("top", "0.75"), new XAttribute("bottom", "0.75"), new XAttribute("header", "0.3"), new XAttribute("footer", "0.3")));
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).ToString(SaveOptions.DisableFormatting);
    }

    private static int ResolveStyleIndex(string style, XlsxStyleLibrary styles)
    {
        var builtIn = BuiltInStyleIndex(style);
        if (builtIn is not null) return builtIn.Value;
        if (styles.TryResolveName(style.Trim(), out var custom)) return custom;
        throw new ArgumentException($"Unknown style '{style}'. Use a built-in alias or declare it in the workbook-level 'styles' array.");
    }

    private static bool IsBuiltInStyleAlias(string style) => BuiltInStyleIndex(style) is not null;

    private static int? BuiltInStyleIndex(string style) => style.Trim().ToLowerInvariant() switch
    {
        "default" or "text" => 0,
        "input" => 1,
        "formula" => 2,
        "cross-sheet" => 3,
        "header" => 4,
        "currency-input" => 5,
        "currency-formula" => 6,
        "percent-input" => 7,
        "percent-formula" => 8,
        "integer-input" => 9,
        "integer-formula" => 10,
        "year" => 11,
        "assumption" => 12,
        "external-link" => 13,
        _ => null
    };

    private static string BuildContentTypes(int sheetCount)
    {
        var overrides = string.Join(string.Empty, Enumerable.Range(1, sheetCount).Select(index => $"<Override PartName=\"/xl/worksheets/sheet{index}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"));
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"{ContentTypesNs}\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/><Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>{overrides}</Types>";
    }

    private static string WorkbookXml(IEnumerable<string> sheetNames)
    {
        var sheets = string.Join(string.Empty, sheetNames.Select((name, index) => $"<sheet name=\"{XmlEscape(name)}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>"));
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"{RelationshipNs}\"><bookViews><workbookView xWindow=\"0\" yWindow=\"0\" windowWidth=\"24000\" windowHeight=\"12000\"/></bookViews><sheets>{sheets}</sheets><calcPr calcId=\"191029\" calcMode=\"auto\" fullCalcOnLoad=\"1\" forceFullCalc=\"1\"/></workbook>";
    }

    private static string WorkbookRelationshipsXml(int sheetCount)
    {
        var relations = string.Join(string.Empty, Enumerable.Range(1, sheetCount).Select(index => $"<Relationship Id=\"rId{index}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{index}.xml\"/>"));
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"{PackageRelationshipNs}\">{relations}<Relationship Id=\"rId{sheetCount + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";
    }

    private static string AppPropertiesXml(IEnumerable<string> sheetNames)
    {
        var names = sheetNames.ToList();
        var titles = string.Join(string.Empty, names.Select(name => $"<vt:lpstr>{XmlEscape(name)}</vt:lpstr>"));
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\"><Application>AthenaAgent</Application><HeadingPairs><vt:vector size=\"2\" baseType=\"variant\"><vt:variant><vt:lpstr>Worksheets</vt:lpstr></vt:variant><vt:variant><vt:i4>{names.Count}</vt:i4></vt:variant></vt:vector></HeadingPairs><TitlesOfParts><vt:vector size=\"{names.Count}\" baseType=\"lpstr\">{titles}</vt:vector></TitlesOfParts></Properties>";
    }

    private sealed record SheetPart(string Name, string PartPath);

    private static string WorkbookRootRelationshipsXml =>
        RootRelationshipsXml("xl/workbook.xml", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument");

    // Athena's own base style table for new workbooks. Custom styles are appended on top of it by
    // XlsxStyleLibrary, so cellXfs indexes 0-13 must keep matching BuiltInStyleIndex:
    // 0 text, 1 input, 2 formula, 3 cross-sheet, 4 header, 5/6 currency, 7/8 percent,
    // 9/10 integer, 11 year, 12 assumption, 13 external-link.
    // Colour convention follows standard financial modelling practice: blue = typed input,
    // black = calculated, green = link to another sheet, red = link outside the workbook.
    private const string StylesXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="3">
            <numFmt numFmtId="164" formatCode="#,##0.00_);[Red](#,##0.00);&quot;-&quot;_)"/>
            <numFmt numFmtId="165" formatCode="0.0%_);[Red](0.0%);&quot;-&quot;_)"/>
            <numFmt numFmtId="166" formatCode="#,##0_);[Red](#,##0);&quot;-&quot;_)"/>
          </numFmts>
          <fonts count="6">
            <font><sz val="11"/><name val="Calibri"/><family val="2"/></font>
            <font><b/><sz val="11"/><name val="Calibri"/><family val="2"/></font>
            <font><color rgb="FF1F4EC8"/><sz val="11"/><name val="Calibri"/><family val="2"/></font>
            <font><color rgb="FF107C41"/><sz val="11"/><name val="Calibri"/><family val="2"/></font>
            <font><color rgb="FFC00000"/><sz val="11"/><name val="Calibri"/><family val="2"/></font>
            <font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/><family val="2"/></font>
          </fonts>
          <fills count="4">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF1F3864"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFFFF2CC"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="2">
            <border><left/><right/><top/><bottom/><diagonal/></border>
            <border><left/><right/><top/><bottom style="thin"><color rgb="FFFFFFFF"/></bottom><diagonal/></border>
          </borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="14">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="3" fillId="0" borderId="0" xfId="0" applyFont="1"/>
            <xf numFmtId="0" fontId="5" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="164" fontId="2" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="165" fontId="2" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/>
            <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="166" fontId="2" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/>
            <xf numFmtId="166" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="center"/></xf>
            <xf numFmtId="0" fontId="2" fillId="3" borderId="0" xfId="0" applyFont="1" applyFill="1"/>
            <xf numFmtId="0" fontId="4" fillId="0" borderId="0" xfId="0" applyFont="1"/>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
          <dxfs count="0"/>
          <tableStyles count="0" defaultTableStyle="TableStyleMedium2" defaultPivotStyle="PivotStyleLight16"/>
        </styleSheet>
        """;
}
