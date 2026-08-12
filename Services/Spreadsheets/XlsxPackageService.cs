using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Athena.UI.Services.Spreadsheets;

/// <summary>
/// Dependency-free OOXML workbook operations used by Athena's built-in xlsx skill.
/// The service deliberately supports bounded, surgical operations rather than a
/// lossy unzip/pretty-print/repack workflow.
/// </summary>
public sealed class XlsxPackageService
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelationshipNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    private const int MaxEntries = 20_000;
    private const long MaxEntryBytes = 128L * 1024 * 1024;
    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private const int MaxUpdates = 5_000;

    public object Inspect(string path, string? requestedSheet, int maxRows, int maxColumns)
    {
        maxRows = Math.Clamp(maxRows, 1, 200);
        maxColumns = Math.Clamp(maxColumns, 1, 100);

        using var archive = OpenChecked(path, ZipArchiveMode.Read);
        var sheets = ReadSheetMap(archive);
        if (!string.IsNullOrWhiteSpace(requestedSheet))
        {
            sheets = sheets.Where(sheet => sheet.Name.Equals(requestedSheet, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sheets.Count == 0) throw new InvalidOperationException($"Worksheet not found: {requestedSheet}");
        }

        var sharedStrings = ReadSharedStrings(archive);
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
                if (row > maxRows || column > maxColumns) continue;

                preview.Add(new
                {
                    address,
                    value = ReadCellValue(cell, sharedStrings),
                    formula = (string?)cell.Element(SpreadsheetNs + "f"),
                    styleIndex = (int?)cell.Attribute("s")
                });
            }

            inspections.Add(new
            {
                sheet.Name,
                sheet.PartPath,
                maxRow = usedMaxRow,
                maxColumn = usedMaxColumn,
                formulaCount,
                errorCellCount = errorCount,
                preview
            });
        }

        return new
        {
            path,
            sheetCount = ReadSheetMap(archive).Count,
            sheets = inspections,
            features = DetectFeatures(archive)
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

        AtomicWrite(outputPath, overwrite, temporaryPath =>
        {
            using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
            WriteTextEntry(archive, "[Content_Types].xml", BuildContentTypes(sheetSpecs.Count));
            WriteTextEntry(archive, "_rels/.rels", RootRelationshipsXml);
            WriteTextEntry(archive, "docProps/core.xml", CorePropertiesXml());
            WriteTextEntry(archive, "docProps/app.xml", AppPropertiesXml(sheetSpecs.Select(spec => GetRequiredString(spec, "name"))));
            WriteTextEntry(archive, "xl/workbook.xml", WorkbookXml(sheetSpecs.Select(spec => GetRequiredString(spec, "name"))));
            WriteTextEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationshipsXml(sheetSpecs.Count));
            WriteTextEntry(archive, "xl/styles.xml", StylesXml);
            for (var index = 0; index < sheetSpecs.Count; index++)
                WriteTextEntry(archive, $"xl/worksheets/sheet{index + 1}.xml", WorksheetXml(sheetSpecs[index]));
        });
    }

    public void Edit(string inputPath, string outputPath, string updatesJson, bool overwrite)
    {
        EnsureWorkbookExtension(inputPath);
        EnsureWorkbookExtension(outputPath);
        if (Path.GetFullPath(inputPath).Equals(Path.GetFullPath(outputPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("outputPath must differ from inputPath so the original workbook remains recoverable.");
        if (!Path.GetExtension(inputPath).Equals(Path.GetExtension(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("inputPath and outputPath must use the same extension so macro-enabled workbooks are not silently downgraded.");
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

            foreach (var update in updates)
            {
                var sheetName = GetRequiredString(update, "sheet");
                var cellReference = GetRequiredString(update, "cell").ToUpperInvariant();
                if (!TryParseCellReference(cellReference, out var rowNumber, out var columnNumber))
                    throw new ArgumentException($"Invalid cell reference: {cellReference}");
                var sheet = sheetMap.FirstOrDefault(candidate => candidate.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Worksheet not found: {sheetName}");
                if (!documents.TryGetValue(sheet.PartPath, out var document))
                {
                    document = ReadXml(RequiredEntry(archive, sheet.PartPath), preserveWhitespace: true);
                    documents[sheet.PartPath] = document;
                }

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
                ApplyStyle(update, document, sheetMap, documents, archive, cell);
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

            if (formulasChanged)
            {
                RemoveCalculationChain(archive);
                SetFullCalculation(archive);
            }
        });
    }

    private static void ApplyStyle(JsonElement update, XDocument currentDocument, IReadOnlyList<SheetPart> sheetMap,
        IDictionary<string, XDocument> documents, ZipArchive archive, XElement cell)
    {
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
        var relationships = ReadXml(RequiredEntry(archive, "xl/_rels/workbook.xml.rels"));
        var targets = relationships.Root?.Elements(PackageRelationshipNs + "Relationship")
            .Where(element => element.Attribute("Id") is not null && element.Attribute("Target") is not null)
            .ToDictionary(element => (string)element.Attribute("Id")!, element => ResolvePartPath("xl/workbook.xml", (string)element.Attribute("Target")!), StringComparer.Ordinal)
            ?? new Dictionary<string, string>();
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
        if (!File.Exists(path)) throw new FileNotFoundException("Workbook not found.", path);
        var archive = ZipFile.Open(path, mode);
        try
        {
            if (archive.Entries.Count > MaxEntries) throw new InvalidDataException($"Workbook contains more than {MaxEntries} ZIP entries.");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                ValidateEntryName(entry.FullName);
                if (entry.Length > MaxEntryBytes) throw new InvalidDataException($"OOXML part is too large: {entry.FullName}");
                total = checked(total + entry.Length);
                if (total > MaxPackageBytes) throw new InvalidDataException($"Uncompressed workbook exceeds {MaxPackageBytes} bytes.");
                if (entry.CompressedLength > 0 && entry.Length / Math.Max(1d, entry.CompressedLength) > 200d)
                    throw new InvalidDataException($"Suspicious compression ratio in OOXML part: {entry.FullName}");
            }
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static XDocument ReadXml(ZipArchiveEntry entry, bool preserveWhitespace = false)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxEntryBytes,
            IgnoreWhitespace = !preserveWhitespace
        });
        return XDocument.Load(reader, preserveWhitespace ? LoadOptions.PreserveWhitespace : LoadOptions.None);
    }

    private static void ReplaceXmlEntry(ZipArchive archive, string name, XDocument document)
    {
        archive.GetEntry(name)?.Delete();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false });
        document.Save(writer);
    }

    private static ZipArchiveEntry RequiredEntry(ZipArchive archive, string name) =>
        archive.GetEntry(name) ?? throw new InvalidDataException($"Missing required OOXML part: {name}");

    private static string SourcePartForRelationships(string relationshipsPath)
    {
        if (relationshipsPath.Equals("_rels/.rels", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        var marker = "/_rels/";
        var index = relationshipsPath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || !relationshipsPath.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return relationshipsPath[..index] + "/" + relationshipsPath[(index + marker.Length)..^5];
    }

    private static string ResolvePartPath(string sourcePart, string target)
    {
        if (target.StartsWith('/')) return NormalizePartPath(target[1..]);
        var directory = sourcePart.Contains('/') ? sourcePart[..(sourcePart.LastIndexOf('/') + 1)] : string.Empty;
        return NormalizePartPath(directory + target);
    }

    private static string NormalizePartPath(string path)
    {
        var stack = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (stack.Count == 0) throw new InvalidDataException($"OOXML relationship escapes package root: {path}");
                stack.RemoveAt(stack.Count - 1);
            }
            else stack.Add(segment);
        }
        return string.Join('/', stack);
    }

    private static void ValidateEntryName(string name)
    {
        var raw = name.Replace('\\', '/');
        if (raw.StartsWith('/') || raw.Contains(':'))
            throw new InvalidDataException($"Unsafe ZIP entry name: {name}");
        var normalized = NormalizePartPath(name);
        if (!normalized.Equals(raw.TrimEnd('/'), StringComparison.Ordinal))
            throw new InvalidDataException($"Unsafe or non-canonical ZIP entry name: {name}");
    }

    private static void EnsureWorkbookExtension(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only .xlsx and .xlsm OOXML workbooks are supported.");
    }

    private static void EnsureCanWrite(string outputPath, bool overwrite)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (string.IsNullOrEmpty(directory)) throw new ArgumentException("Output path must include a valid parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(outputPath) && !overwrite) throw new IOException("Output workbook already exists. Set overwrite=true only when replacement is intended.");
    }

    private static void AtomicWrite(string outputPath, bool overwrite, Action<string> writer)
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutput)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");
        try
        {
            writer(temporaryPath);
            File.Move(temporaryPath, fullOutput, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
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

    private static string WorksheetXml(JsonElement specification)
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
                        cell.SetAttributeValue("s", StyleIndex(style.GetString()!));
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
        root.Add(new XElement(SpreadsheetNs + "pageMargins", new XAttribute("left", "0.7"), new XAttribute("right", "0.7"), new XAttribute("top", "0.75"), new XAttribute("bottom", "0.75"), new XAttribute("header", "0.3"), new XAttribute("footer", "0.3")));
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).ToString(SaveOptions.DisableFormatting);
    }

    private static int StyleIndex(string style) => style.Trim().ToLowerInvariant() switch
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
        _ => throw new ArgumentException($"Unknown style alias: {style}")
    };

    private static void WriteTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string BuildContentTypes(int sheetCount)
    {
        var overrides = string.Join(string.Empty, Enumerable.Range(1, sheetCount).Select(index => $"<Override PartName=\"/xl/worksheets/sheet{index}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"));
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"{ContentTypesNs}\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/><Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>{overrides}</Types>";
    }

    private static string WorkbookXml(IEnumerable<string> sheetNames)
    {
        var sheets = string.Join(string.Empty, sheetNames.Select((name, index) => $"<sheet name=\"{SecurityElementEscape(name)}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>"));
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
        var titles = string.Join(string.Empty, names.Select(name => $"<vt:lpstr>{SecurityElementEscape(name)}</vt:lpstr>"));
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\"><Application>AthenaAgent</Application><HeadingPairs><vt:vector size=\"2\" baseType=\"variant\"><vt:variant><vt:lpstr>Worksheets</vt:lpstr></vt:variant><vt:variant><vt:i4>{names.Count}</vt:i4></vt:variant></vt:vector></HeadingPairs><TitlesOfParts><vt:vector size=\"{names.Count}\" baseType=\"lpstr\">{titles}</vt:vector></TitlesOfParts></Properties>";
    }

    private static string CorePropertiesXml()
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><dc:creator>AthenaAgent</dc:creator><cp:lastModifiedBy>AthenaAgent</cp:lastModifiedBy><dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified></cp:coreProperties>";
    }

    private static string SecurityElementEscape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private sealed record SheetPart(string Name, string PartPath);

    private const string RootRelationshipsXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/></Relationships>";

    private const string StylesXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="3"><numFmt numFmtId="164" formatCode="$#,##0.00;[Red]($#,##0.00);-"/><numFmt numFmtId="165" formatCode="0.0%;[Red](0.0%);-"/><numFmt numFmtId="166" formatCode="0;[Red](0);-"/></numFmts>
          <fonts count="6">
            <font><sz val="11"/><name val="Arial"/><family val="2"/></font>
            <font><b/><sz val="11"/><name val="Arial"/><family val="2"/></font>
            <font><color rgb="FF0000FF"/><sz val="11"/><name val="Arial"/><family val="2"/></font>
            <font><color rgb="FF008000"/><sz val="11"/><name val="Arial"/><family val="2"/></font>
            <font><color rgb="FFFF0000"/><sz val="11"/><name val="Arial"/><family val="2"/></font>
            <font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Arial"/><family val="2"/></font>
          </fonts>
          <fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1F4E78"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFFFF00"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top/><bottom style="thin"><color rgb="FFFFFFFF"/></bottom><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="14">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="3" fillId="0" borderId="0" xfId="0" applyFont="1"/>
            <xf numFmtId="0" fontId="5" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1"><alignment horizontal="center"/></xf>
            <xf numFmtId="164" fontId="2" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="165" fontId="2" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/>
            <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="166" fontId="2" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/>
            <xf numFmtId="166" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"><alignment horizontal="center"/></xf>
            <xf numFmtId="0" fontId="2" fillId="3" borderId="0" xfId="0" applyFont="1" applyFill="1"/>
            <xf numFmtId="0" fontId="4" fillId="0" borderId="0" xfId="0" applyFont="1"/>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles><dxfs count="0"/><tableStyles count="0" defaultTableStyle="TableStyleMedium2" defaultPivotStyle="PivotStyleLight16"/>
        </styleSheet>
        """;
}
