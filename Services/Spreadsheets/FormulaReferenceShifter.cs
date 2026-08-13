using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Athena.UI.Services.Spreadsheets;

internal enum ShiftAxis
{
    Row,
    Column
}

/// <summary>
/// One structural edit applied to a single worksheet: inserting or deleting a contiguous
/// band of rows or columns. <see cref="Map"/> encodes Excel's coordinate fix-up rule —
/// a null result means the coordinate no longer exists and collapses to #REF!.
/// </summary>
internal sealed class ShiftOperation
{
    public ShiftOperation(string sheetName, ShiftAxis axis, int index, int count, bool isInsert)
    {
        SheetName = sheetName;
        Axis = axis;
        Index = index;
        Count = count;
        IsInsert = isInsert;
    }

    public string SheetName { get; }
    public ShiftAxis Axis { get; }
    public int Index { get; }
    public int Count { get; }
    public bool IsInsert { get; }
    public int Limit => Axis == ShiftAxis.Row ? 1_048_576 : 16_384;

    public int? Map(int coordinate)
    {
        if (IsInsert)
        {
            if (coordinate < Index) return coordinate;
            var shifted = coordinate + Count;
            return shifted > Limit ? null : shifted;
        }

        if (coordinate < Index) return coordinate;
        if (coordinate < Index + Count) return null;
        return coordinate - Count;
    }
}

/// <summary>
/// Rewrites A1-style references inside stored formulas and range attributes after rows or
/// columns are inserted or deleted. The scanner skips string literals, error literals,
/// function names, structured references and external workbook references so that only real
/// cell references are touched.
/// </summary>
internal static class FormulaReferenceShifter
{
    private const string RefError = "#REF!";
    private const int MaxColumn = 16_384;
    private const int MaxRow = 1_048_576;

    public static string ShiftFormula(string formula, string? hostSheet, ShiftOperation operation)
    {
        if (string.IsNullOrEmpty(formula)) return formula;

        var builder = new StringBuilder(formula.Length + 16);
        var index = 0;
        while (index < formula.Length)
        {
            var current = formula[index];
            if (current == '"')
            {
                var end = SkipStringLiteral(formula, index);
                builder.Append(formula, index, end - index);
                index = end;
                continue;
            }

            if (current == '#')
            {
                var end = SkipErrorLiteral(formula, index);
                builder.Append(formula, index, end - index);
                index = end;
                continue;
            }

            if (current == '[')
            {
                var end = SkipExternalReference(formula, index);
                builder.Append(formula, index, end - index);
                index = end;
                continue;
            }

            if (TryReadReference(formula, index, out var reference, out var length))
            {
                builder.Append(Rewrite(reference, hostSheet, operation));
                index += length;
                continue;
            }

            var tokenEnd = SkipOpaqueToken(formula, index);
            builder.Append(formula, index, tokenEnd - index);
            index = tokenEnd;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Shifts a worksheet-local range attribute such as mergeCell/@ref or dataValidation/@sqref.
    /// Returns null when every range in the attribute was removed by the operation.
    /// </summary>
    public static string? ShiftLocalRanges(string value, ShiftOperation operation)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        var survivors = new List<string>();
        foreach (var part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryReadReference(part, 0, out var reference, out var length) || length != part.Length)
            {
                survivors.Add(part);
                continue;
            }

            var shifted = Rewrite(reference, operation.SheetName, operation);
            if (shifted.Contains(RefError, StringComparison.Ordinal)) continue;
            survivors.Add(shifted);
        }

        return survivors.Count == 0 ? null : string.Join(' ', survivors);
    }

    private static string Rewrite(ParsedReference reference, string? hostSheet, ShiftOperation operation)
    {
        if (!TargetsOperationSheet(reference, hostSheet, operation)) return reference.Text;

        return reference.Kind switch
        {
            ReferenceKind.Cell => RewriteCell(reference, operation),
            ReferenceKind.Range => RewriteRange(reference, operation),
            ReferenceKind.WholeColumns => operation.Axis == ShiftAxis.Column ? RewriteBand(reference, operation) : reference.Text,
            ReferenceKind.WholeRows => operation.Axis == ShiftAxis.Row ? RewriteBand(reference, operation) : reference.Text,
            _ => reference.Text
        };
    }

    private static bool TargetsOperationSheet(ParsedReference reference, string? hostSheet, ShiftOperation operation)
    {
        if (reference.SheetNames.Count == 0)
            return hostSheet is not null && hostSheet.Equals(operation.SheetName, StringComparison.OrdinalIgnoreCase);

        foreach (var name in reference.SheetNames)
        {
            if (name.Equals(operation.SheetName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string RewriteCell(ParsedReference reference, ShiftOperation operation)
    {
        var start = reference.Start;
        var coordinate = operation.Axis == ShiftAxis.Row ? start.Row : start.Column;
        var mapped = operation.Map(coordinate);
        if (mapped is null) return reference.Prefix + RefError;

        var moved = operation.Axis == ShiftAxis.Row
            ? start with { Row = mapped.Value }
            : start with { Column = mapped.Value };
        return reference.Prefix + Render(moved);
    }

    private static string RewriteRange(ParsedReference reference, ShiftOperation operation)
    {
        var start = reference.Start;
        var end = reference.End!.Value;
        var startCoordinate = operation.Axis == ShiftAxis.Row ? start.Row : start.Column;
        var endCoordinate = operation.Axis == ShiftAxis.Row ? end.Row : end.Column;

        if (!TryMapBand(operation, startCoordinate, endCoordinate, out var newStart, out var newEnd))
            return reference.Prefix + RefError;

        var movedStart = operation.Axis == ShiftAxis.Row ? start with { Row = newStart } : start with { Column = newStart };
        var movedEnd = operation.Axis == ShiftAxis.Row ? end with { Row = newEnd } : end with { Column = newEnd };
        return reference.Prefix + Render(movedStart) + ":" + Render(movedEnd);
    }

    private static string RewriteBand(ParsedReference reference, ShiftOperation operation)
    {
        var start = reference.Start;
        var end = reference.End!.Value;
        var startCoordinate = operation.Axis == ShiftAxis.Row ? start.Row : start.Column;
        var endCoordinate = operation.Axis == ShiftAxis.Row ? end.Row : end.Column;

        if (!TryMapBand(operation, startCoordinate, endCoordinate, out var newStart, out var newEnd))
            return reference.Prefix + RefError;

        var startText = reference.Kind == ReferenceKind.WholeColumns
            ? (start.ColumnAbsolute ? "$" : string.Empty) + ColumnName(newStart)
            : (start.RowAbsolute ? "$" : string.Empty) + newStart.ToString(CultureInfo.InvariantCulture);
        var endText = reference.Kind == ReferenceKind.WholeColumns
            ? (end.ColumnAbsolute ? "$" : string.Empty) + ColumnName(newEnd)
            : (end.RowAbsolute ? "$" : string.Empty) + newEnd.ToString(CultureInfo.InvariantCulture);
        return reference.Prefix + startText + ":" + endText;
    }

    /// <summary>
    /// Maps both endpoints of a span. A deleted start snaps to the deletion point and a deleted
    /// end snaps to the row/column just before it, which is how Excel shrinks partially deleted
    /// ranges. Returns false when nothing of the span survives.
    /// </summary>
    private static bool TryMapBand(ShiftOperation operation, int start, int end, out int newStart, out int newEnd)
    {
        var mappedStart = operation.Map(start);
        var mappedEnd = operation.Map(end);
        newStart = mappedStart ?? operation.Index;
        newEnd = mappedEnd ?? operation.Index - 1;
        if (operation.IsInsert && (mappedStart is null || mappedEnd is null)) return false;
        return newEnd >= newStart && newStart >= 1;
    }

    private static string Render(Coordinate coordinate) =>
        (coordinate.ColumnAbsolute ? "$" : string.Empty) + ColumnName(coordinate.Column) +
        (coordinate.RowAbsolute ? "$" : string.Empty) + coordinate.Row.ToString(CultureInfo.InvariantCulture);

    private static string ColumnName(int column)
    {
        var builder = new StringBuilder(3);
        while (column > 0)
        {
            column--;
            builder.Insert(0, (char)('A' + column % 26));
            column /= 26;
        }
        return builder.ToString();
    }

    private static int SkipStringLiteral(string text, int start)
    {
        var index = start + 1;
        while (index < text.Length)
        {
            if (text[index] == '"')
            {
                if (index + 1 < text.Length && text[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }
                return index + 1;
            }
            index++;
        }
        return text.Length;
    }

    private static int SkipErrorLiteral(string text, int start)
    {
        var index = start + 1;
        while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '/' or '!' or '?')) index++;
        return Math.Max(index, start + 1);
    }

    /// <summary>
    /// Consumes an external workbook reference such as [1]Sheet1!A1 verbatim: Athena never
    /// rewrites references that point outside the edited package.
    /// </summary>
    private static int SkipExternalReference(string text, int start)
    {
        var index = start;
        while (index < text.Length && text[index] != ']') index++;
        if (index < text.Length) index++;
        while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '.' or '\'' or ' ')) index++;
        if (index < text.Length && text[index] == '!')
        {
            index++;
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '.' or '$' or ':')) index++;
        }
        return Math.Max(index, start + 1);
    }

    private static int SkipOpaqueToken(string text, int start)
    {
        var current = text[start];
        if (current == '\'')
        {
            var index = start + 1;
            while (index < text.Length)
            {
                if (text[index] == '\'')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\'')
                    {
                        index += 2;
                        continue;
                    }
                    index++;
                    break;
                }
                index++;
            }
            if (index < text.Length && text[index] == '!') index++;
            return Math.Max(index, start + 1);
        }

        if (IsNameCharacter(current) || char.IsDigit(current))
        {
            var index = start;
            while (index < text.Length && (IsNameCharacter(text[index]) || char.IsDigit(text[index]) || text[index] == '$')) index++;
            if (index < text.Length && text[index] == '!') index++;
            return Math.Max(index, start + 1);
        }

        return start + 1;
    }

    private static bool IsNameCharacter(char value) =>
        char.IsLetter(value) || value == '_' || value == '.' || value == '\\' || value > 0x7F;

    private static bool TryReadReference(string text, int start, out ParsedReference reference, out int length)
    {
        reference = default!;
        length = 0;

        if (start > 0)
        {
            var previous = text[start - 1];
            if (IsNameCharacter(previous) || char.IsDigit(previous) || previous == '!' || previous == '$') return false;
        }

        var position = start;
        var sheetNames = new List<string>();
        if (TryReadSheetPrefix(text, position, sheetNames, out var afterPrefix)) position = afterPrefix;
        else sheetNames.Clear();

        var prefix = text[start..position];
        if (!TryReadBody(text, position, out var kind, out var first, out var second, out var bodyEnd)) return false;

        if (bodyEnd < text.Length && (text[bodyEnd] == '(' || text[bodyEnd] == '[' || IsNameCharacter(text[bodyEnd]))) return false;

        length = bodyEnd - start;
        reference = new ParsedReference(text[start..bodyEnd], prefix, sheetNames, kind, first, second);
        return true;
    }

    private static bool TryReadSheetPrefix(string text, int start, List<string> sheetNames, out int end)
    {
        end = start;
        var position = start;
        while (true)
        {
            if (!TryReadSheetName(text, position, out var name, out var afterName)) return false;
            sheetNames.Add(name);
            position = afterName;
            if (position < text.Length && text[position] == ':' && sheetNames.Count < 8)
            {
                position++;
                continue;
            }
            break;
        }

        if (position >= text.Length || text[position] != '!') return false;
        end = position + 1;
        return true;
    }

    private static bool TryReadSheetName(string text, int start, out string name, out int end)
    {
        name = string.Empty;
        end = start;
        if (start >= text.Length) return false;

        if (text[start] == '\'')
        {
            var builder = new StringBuilder();
            var index = start + 1;
            while (index < text.Length)
            {
                if (text[index] == '\'')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\'')
                    {
                        builder.Append('\'');
                        index += 2;
                        continue;
                    }
                    name = builder.ToString();
                    end = index + 1;
                    return name.Length > 0;
                }
                builder.Append(text[index]);
                index++;
            }
            return false;
        }

        var cursor = start;
        while (cursor < text.Length && (IsNameCharacter(text[cursor]) || char.IsDigit(text[cursor]))) cursor++;
        if (cursor == start) return false;
        name = text[start..cursor];
        end = cursor;
        return true;
    }

    private static bool TryReadBody(string text, int start, out ReferenceKind kind, out Coordinate first, out Coordinate? second, out int end)
    {
        kind = ReferenceKind.Cell;
        first = default;
        second = null;
        end = start;

        if (TryReadCell(text, start, out var firstCell, out var afterFirst))
        {
            if (afterFirst < text.Length && text[afterFirst] == ':' &&
                TryReadCell(text, afterFirst + 1, out var secondCell, out var afterSecond))
            {
                kind = ReferenceKind.Range;
                first = firstCell;
                second = secondCell;
                end = afterSecond;
                return true;
            }

            kind = ReferenceKind.Cell;
            first = firstCell;
            end = afterFirst;
            return true;
        }

        if (TryReadColumn(text, start, out var firstColumn, out var firstColumnAbsolute, out var afterFirstColumn) &&
            afterFirstColumn < text.Length && text[afterFirstColumn] == ':' &&
            TryReadColumn(text, afterFirstColumn + 1, out var secondColumn, out var secondColumnAbsolute, out var afterSecondColumn))
        {
            kind = ReferenceKind.WholeColumns;
            first = new Coordinate(firstColumn, 1, firstColumnAbsolute, false);
            second = new Coordinate(secondColumn, 1, secondColumnAbsolute, false);
            end = afterSecondColumn;
            return true;
        }

        if (TryReadRow(text, start, out var firstRow, out var firstRowAbsolute, out var afterFirstRow) &&
            afterFirstRow < text.Length && text[afterFirstRow] == ':' &&
            TryReadRow(text, afterFirstRow + 1, out var secondRow, out var secondRowAbsolute, out var afterSecondRow))
        {
            kind = ReferenceKind.WholeRows;
            first = new Coordinate(1, firstRow, false, firstRowAbsolute);
            second = new Coordinate(1, secondRow, false, secondRowAbsolute);
            end = afterSecondRow;
            return true;
        }

        return false;
    }

    private static bool TryReadCell(string text, int start, out Coordinate coordinate, out int end)
    {
        coordinate = default;
        end = start;
        if (!TryReadColumn(text, start, out var column, out var columnAbsolute, out var afterColumn)) return false;
        if (!TryReadRow(text, afterColumn, out var row, out var rowAbsolute, out var afterRow)) return false;
        coordinate = new Coordinate(column, row, columnAbsolute, rowAbsolute);
        end = afterRow;
        return true;
    }

    private static bool TryReadColumn(string text, int start, out int column, out bool absolute, out int end)
    {
        column = 0;
        absolute = false;
        end = start;
        var index = start;
        if (index < text.Length && text[index] == '$')
        {
            absolute = true;
            index++;
        }

        var letters = 0;
        while (index < text.Length && char.IsAsciiLetter(text[index]) && letters < 3)
        {
            column = column * 26 + (char.ToUpperInvariant(text[index]) - 'A' + 1);
            letters++;
            index++;
        }

        if (letters == 0 || column > MaxColumn) return false;
        if (index < text.Length && char.IsAsciiLetter(text[index])) return false;
        end = index;
        return true;
    }

    private static bool TryReadRow(string text, int start, out int row, out bool absolute, out int end)
    {
        row = 0;
        absolute = false;
        end = start;
        var index = start;
        if (index < text.Length && text[index] == '$')
        {
            absolute = true;
            index++;
        }

        var digits = 0;
        while (index < text.Length && char.IsAsciiDigit(text[index]) && digits < 8)
        {
            row = row * 10 + (text[index] - '0');
            digits++;
            index++;
        }

        if (digits == 0 || row < 1 || row > MaxRow) return false;
        if (index < text.Length && char.IsAsciiDigit(text[index])) return false;
        end = index;
        return true;
    }

    private enum ReferenceKind
    {
        Cell,
        Range,
        WholeColumns,
        WholeRows
    }

    private readonly record struct Coordinate(int Column, int Row, bool ColumnAbsolute, bool RowAbsolute);

    private sealed record ParsedReference(
        string Text,
        string Prefix,
        IReadOnlyList<string> SheetNames,
        ReferenceKind Kind,
        Coordinate Start,
        Coordinate? End);
}
