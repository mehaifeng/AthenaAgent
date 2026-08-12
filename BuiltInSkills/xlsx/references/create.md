# Create workbooks

Use Athena's `create_spreadsheet` for routine `.xlsx` creation. It avoids runtime dependencies and produces a complete OOXML package atomically.

## Specification

`workbookJson` contains `sheets`. Each sheet supports:

- `name` (required, unique, max 31 characters);
- `rows` as arrays of cells;
- `freezeRows` (optional);
- `columnWidths` (optional, 1-255);
- `autoFilter` (optional boolean).

A cell is a JSON primitive or an object with `value` or `formula`, plus optional `style`/`styleIndex`. Use `null` to omit a cell.

## Model design

Plan the workbook before calling the tool:

1. separate inputs, calculations, and outputs where the model warrants it;
2. decide units and date granularity;
3. map every derived figure to a formula;
4. choose readable widths and frozen headers;
5. include source/assumption labels.

Use the style aliases described in `SKILL.md`. For deeper financial formatting guidance, read `format.md`.

## Formula caches

Athena writes formula expressions and intentionally leaves their cached `<v>` result empty. This prevents stale computed values. Excel or LibreOffice must recalculate them. `validate_spreadsheet` reports the empty caches as warnings rather than structural errors.

## Template creation

When the user supplies an existing template, that is an edit task: inspect it and use `edit_spreadsheet` with `copyStyleFrom`. Do not rebuild it from scratch.
