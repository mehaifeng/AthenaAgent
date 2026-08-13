using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Athena.UI.Services.Spreadsheets;

public sealed partial class XlsxPackageService
{
    private const int MaxCsvRows = 200_000;
    private const long MaxCsvBytes = 64L * 1024 * 1024;

    // Strict decimal literal: keeps "007", "1,234", "+1" and 16-digit identifiers as text.
    private static readonly Regex NumericLiteral = new(@"^-?(0|[1-9][0-9]{0,14})(\.[0-9]+)?([eE][+-]?[0-9]{1,3})?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Converts between delimited text and a workbook. The direction is derived from the file
    /// extensions: .csv/.tsv in produces .xlsx, and .xlsx/.xlsm in produces .csv/.tsv.
    /// </summary>
    public object ConvertDelimited(string inputPath, string outputPath, string? sheet, string? delimiter, bool headerRow, bool overwrite)
    {
        var inputExtension = Path.GetExtension(inputPath).ToLowerInvariant();
        var outputExtension = Path.GetExtension(outputPath).ToLowerInvariant();
        EnsureCanWrite(outputPath, overwrite);

        return (inputExtension, outputExtension) switch
        {
            (".csv" or ".tsv" or ".txt", ".xlsx") => ImportDelimitedText(inputPath, outputPath, sheet, ResolveDelimiter(delimiter, inputExtension), headerRow, overwrite),
            (".xlsx" or ".xlsm", ".csv" or ".tsv" or ".txt") => ExportDelimitedText(inputPath, outputPath, sheet, ResolveDelimiter(delimiter, outputExtension), overwrite),
            _ => throw new ArgumentException(
                "Unsupported conversion. Use .csv/.tsv/.txt -> .xlsx to import, or .xlsx/.xlsm -> .csv/.tsv/.txt to export.")
        };
    }

    private static char ResolveDelimiter(string? delimiter, string extension)
    {
        if (!string.IsNullOrEmpty(delimiter))
        {
            var value = delimiter switch
            {
                "\\t" or "tab" or "TAB" => "\t",
                _ => delimiter
            };
            if (value.Length != 1) throw new ArgumentException("'delimiter' must be a single character such as ',' or ';' (use \"\\t\" for tab).");
            return value[0];
        }
        return extension == ".tsv" ? '\t' : ',';
    }

    private object ImportDelimitedText(string inputPath, string outputPath, string? sheetName, char delimiter, bool headerRow, bool overwrite)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Delimited text file not found.", inputPath);
        var length = new FileInfo(inputPath).Length;
        if (length > MaxCsvBytes) throw new InvalidDataException($"Input exceeds the {MaxCsvBytes / (1024 * 1024)} MB import limit.");

        var name = string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName.Trim();
        ValidateSheetName(name);

        var rows = ParseDelimitedText(inputPath, delimiter);
        if (rows.Count == 0) throw new InvalidDataException("The delimited file contains no rows.");
        var columnCount = rows.Max(row => row.Count);

        AtomicWrite(outputPath, overwrite, temporaryPath =>
        {
            using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
            WriteTextEntry(archive, "[Content_Types].xml", BuildContentTypes(1));
            WriteTextEntry(archive, "_rels/.rels", WorkbookRootRelationshipsXml);
            WriteTextEntry(archive, "docProps/core.xml", CorePropertiesXml());
            WriteTextEntry(archive, "docProps/app.xml", AppPropertiesXml([name]));
            WriteTextEntry(archive, "xl/workbook.xml", WorkbookXml([name]));
            WriteTextEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationshipsXml(1));
            WriteTextEntry(archive, "xl/styles.xml", StylesXml);
            WriteImportedWorksheet(archive, rows, columnCount, headerRow);
        });

        return new
        {
            inputPath,
            outputPath,
            direction = "text-to-workbook",
            sheet = name,
            rowCount = rows.Count,
            columnCount,
            warnings = new[]
            {
                "Values that look numeric were written as numbers; identifiers with leading zeros or more than 15 digits stay text.",
                "Text starting with '=' is imported literally and never becomes a formula."
            }
        };
    }

    private static void WriteImportedWorksheet(ZipArchive archive, IReadOnlyList<List<string>> rows, int columnCount, bool headerRow)
    {
        var entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        writer.Write($"<worksheet xmlns=\"{SpreadsheetNs}\">");
        writer.Write($"<dimension ref=\"A1:{ColumnName(Math.Max(1, columnCount))}{rows.Count}\"/>");
        if (headerRow)
        {
            writer.Write($"<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        }
        writer.Write("<sheetData>");

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var cells = rows[rowIndex];
            writer.Write($"<row r=\"{rowIndex + 1}\">");
            for (var columnIndex = 0; columnIndex < cells.Count; columnIndex++)
            {
                var text = cells[columnIndex];
                if (text.Length == 0) continue;
                var address = ColumnName(columnIndex + 1) + (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
                var style = headerRow && rowIndex == 0 ? " s=\"4\"" : string.Empty;

                if (NumericLiteral.IsMatch(text))
                {
                    writer.Write($"<c r=\"{address}\"{style}><v>{text}</v></c>");
                }
                else if (text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || text.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                {
                    writer.Write($"<c r=\"{address}\"{style} t=\"b\"><v>{(text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? 1 : 0)}</v></c>");
                }
                else
                {
                    var preserve = text.Length != text.Trim().Length ? " xml:space=\"preserve\"" : string.Empty;
                    writer.Write($"<c r=\"{address}\"{style} t=\"inlineStr\"><is><t{preserve}>{XmlEscape(text)}</t></is></c>");
                }
            }
            writer.Write("</row>");
        }

        writer.Write("</sheetData>");
        writer.Write("<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>");
        writer.Write("</worksheet>");
    }

    private static List<List<string>> ParseDelimitedText(string path, char delimiter)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var rows = new List<List<string>>();
        var current = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        void CommitField()
        {
            current.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void CommitRow()
        {
            CommitField();
            if (rows.Count >= MaxCsvRows) throw new InvalidDataException($"The delimited file exceeds the {MaxCsvRows} row import limit.");
            if (current.Count > 16_384) throw new InvalidDataException("A row exceeds the 16384 column limit.");
            rows.Add([.. current]);
            current.Clear();
        }

        while (true)
        {
            var read = reader.Read();
            if (read < 0) break;
            var character = (char)read;

            if (inQuotes)
            {
                if (character == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                        continue;
                    }
                    inQuotes = false;
                    continue;
                }
                field.Append(character);
                continue;
            }

            if (character == '"' && !fieldStarted)
            {
                inQuotes = true;
                fieldStarted = true;
                continue;
            }

            if (character == delimiter)
            {
                CommitField();
                continue;
            }

            if (character == '\r')
            {
                if (reader.Peek() == '\n') reader.Read();
                CommitRow();
                continue;
            }

            if (character == '\n')
            {
                CommitRow();
                continue;
            }

            fieldStarted = true;
            field.Append(character);
        }

        if (field.Length > 0 || current.Count > 0) CommitRow();
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    private object ExportDelimitedText(string inputPath, string outputPath, string? sheetName, char delimiter, bool overwrite)
    {
        using var archive = OpenChecked(inputPath, ZipArchiveMode.Read);
        var sheets = ReadSheetMap(archive);
        var target = string.IsNullOrWhiteSpace(sheetName)
            ? sheets[0]
            : sheets.FirstOrDefault(candidate => candidate.Name.Equals(sheetName.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"Worksheet not found: {sheetName}");

        var sharedStrings = ReadSharedStrings(archive);
        var formats = ReadNumberFormats(archive);
        var worksheet = ReadXml(RequiredEntry(archive, target.PartPath));

        var grid = new SortedDictionary<int, SortedDictionary<int, string>>();
        var maxColumn = 0;
        var emptyFormulaCells = 0;

        foreach (var cell in worksheet.Descendants(SpreadsheetNs + "c"))
        {
            if (!TryParseCellReference((string?)cell.Attribute("r") ?? string.Empty, out var row, out var column)) continue;
            var isFormula = cell.Element(SpreadsheetNs + "f") is not null;
            var value = ReadCellValue(cell, sharedStrings);
            if (isFormula && value is null)
            {
                emptyFormulaCells++;
                continue;
            }
            if (value is null) continue;

            var styleIndex = (int?)cell.Attribute("s");
            var text = value is double number
                ? formats.Format(styleIndex, number) ?? number.ToString("R", CultureInfo.InvariantCulture)
                : value.ToString() ?? string.Empty;

            if (!grid.TryGetValue(row, out var line))
            {
                line = new SortedDictionary<int, string>();
                grid[row] = line;
            }
            line[column] = text;
            maxColumn = Math.Max(maxColumn, column);
        }

        var lastRow = grid.Count == 0 ? 0 : grid.Keys.Max();
        AtomicWrite(outputPath, overwrite, temporaryPath =>
        {
            using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            for (var row = 1; row <= lastRow; row++)
            {
                grid.TryGetValue(row, out var line);
                for (var column = 1; column <= maxColumn; column++)
                {
                    if (column > 1) writer.Write(delimiter);
                    var text = line is not null && line.TryGetValue(column, out var cell) ? cell : string.Empty;
                    writer.Write(QuoteDelimitedField(text, delimiter));
                }
                writer.Write('\n');
            }
        });

        var warnings = new List<string>();
        if (sheets.Count > 1 && string.IsNullOrWhiteSpace(sheetName))
            warnings.Add($"The workbook has {sheets.Count} worksheets; only '{target.Name}' was exported. Pass 'sheet' to choose another.");
        if (emptyFormulaCells > 0)
            warnings.Add($"{emptyFormulaCells} formula cells had no cached result and were exported as empty. Recalculate in Excel or LibreOffice first.");
        warnings.Add("Only values are exported: formulas, styles, merges and charts are not represented in delimited text.");

        return new
        {
            inputPath,
            outputPath,
            direction = "workbook-to-text",
            sheet = target.Name,
            rowCount = lastRow,
            columnCount = maxColumn,
            warnings
        };
    }

    private static string QuoteDelimitedField(string value, char delimiter)
    {
        if (value.Length == 0) return value;
        var needsQuotes = value.Contains(delimiter) || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuotes) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static XlsxNumberFormatMap ReadNumberFormats(ZipArchive archive)
    {
        var stylesEntry = archive.GetEntry("xl/styles.xml");
        var styles = stylesEntry is null ? null : ReadXml(stylesEntry);
        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        var date1904 = false;
        if (workbookEntry is not null)
        {
            var workbook = ReadXml(workbookEntry);
            var properties = workbook.Root?.Element(SpreadsheetNs + "workbookPr");
            var flag = (string?)properties?.Attribute("date1904") ?? (string?)properties?.Attribute("dateCompatibility");
            date1904 = flag is "1" or "true";
        }
        return new XlsxNumberFormatMap(styles, date1904);
    }
}
