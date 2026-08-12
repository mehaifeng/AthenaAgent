---
name: xlsx
description: Create, inspect, analyze, edit, repair, or validate Excel OOXML workbooks (.xlsx and .xlsm), including financial models, formulas, formatting, worksheet data, and template-based cell updates. Also use for Excel-targeted CSV/TSV work. Prefer Athena's native spreadsheet tools; use bundled MiniMax OOXML references and optional Python helpers only for advanced structural operations when their runtime dependencies are available.
triggers:
  - Excel
  - xlsx
  - xlsm
  - spreadsheet
  - workbook
  - 表格
  - 电子表格
  - Excel 表格
  - 财务模型
  - 公式
  - 数据透视表
  - pivot table
  - 套模板
  - CSV
  - TSV
---

# XLSX

This is Athena's native-first adaptation of MiniMax XLSX. Complete the spreadsheet task and write the requested output file. Do not merely explain how to do it.

After activation, treat the returned `rootDirectory` as `SKILL_DIR`. Resolve every resource path beneath it; never guess an installation path.

## Route the task

| User intent | Primary path | Read when needed |
|---|---|---|
| Inspect/read an existing workbook | `inspect_spreadsheet` | `references/read-analyze.md` |
| Create a new workbook | `create_spreadsheet` | `references/create.md`, then `references/format.md` for professional styling |
| Fill or change specific existing cells | `inspect_spreadsheet`, then `edit_spreadsheet` | `references/edit.md` |
| Repair a known cell formula/value | `edit_spreadsheet` | `references/fix.md` |
| Validate the delivered workbook | `validate_spreadsheet` | `references/validate.md` |
| Insert/delete rows or columns, rewrite merged ranges, or do unsupported structural surgery | Advanced OOXML path | `references/edit.md`, `references/ooxml-cheatsheet.md` |

## Mandatory workflow

1. Preserve the source. For edits, output to a distinct path with the same extension.
2. Inspect unfamiliar workbooks before changing them. Note sheets, target cells, formulas, and advanced features.
3. Make the narrowest change that satisfies the request.
4. Put derived values in formulas, not hardcoded values, unless the user explicitly requests static results.
5. Run `validate_spreadsheet` on every created or edited `.xlsx`/`.xlsm` before delivery.
6. If formulas or visual layout matter, state that static validation is not calculation/rendering. When Excel or LibreOffice is available, recalculate and visually inspect there; otherwise disclose that limitation.

## Native tools

### `inspect_spreadsheet`

Use it for workbook discovery and bounded previews. It returns:

- worksheet names and OOXML part paths;
- used row/column bounds;
- cell values, formulas, and style indexes in the preview;
- formula/error counts;
- warnings for macros, pivots, charts, external links, signatures, tables, slicers, and drawings.

Increase `maxRows`/`maxColumns` only as needed. This tool does not modify the source or execute formulas.

### `create_spreadsheet`

Pass `workbookJson` as a JSON string:

```json
{
  "sheets": [
    {
      "name": "Summary",
      "freezeRows": 1,
      "columnWidths": [24, 16, 16],
      "autoFilter": true,
      "rows": [
        [
          {"value": "Metric", "style": "header"},
          {"value": "2025", "style": "header"},
          {"value": "2026", "style": "header"}
        ],
        [
          {"value": "Revenue", "style": "text"},
          {"value": 125000, "style": "currency-input"},
          {"formula": "B2*(1+$B$5)", "style": "currency-formula"}
        ]
      ]
    }
  ]
}
```

Formula strings may start with `=` but storing them without `=` is preferred. Available style aliases:

- `text`, `input`, `formula`, `cross-sheet`, `external-link`
- `header`, `assumption`, `year`
- `currency-input`, `currency-formula`
- `percent-input`, `percent-formula`
- `integer-input`, `integer-formula`

The MiniMax financial color convention is built in: blue inputs, black same-sheet formulas, green cross-sheet formulas, red external links, white-on-dark headers, and yellow assumptions.

### `edit_spreadsheet`

Pass `updatesJson` as a JSON string containing at most 5,000 targeted cell operations:

```json
[
  {"sheet": "Inputs", "cell": "B4", "value": 0.08, "copyStyleFrom": "Inputs!B3"},
  {"sheet": "Model", "cell": "F12", "formula": "SUM(B12:E12)", "copyStyleFrom": "Model!F11"},
  {"sheet": "Notes", "cell": "A20", "clear": true}
]
```

Each item must specify exactly one of `value`, `formula`, or `clear:true`. Prefer `copyStyleFrom` for template edits; use `styleIndex` only after inspection has established the correct existing index.

The native editor intentionally does not shift rows/columns or update defined names, table structured references, external links, chart series, pivot caches, or merged ranges. Advanced structural edits require the explicit OOXML path below.

### `validate_spreadsheet`

This is a static gate. It checks ZIP safety bounds, XML parsing, required parts, internal relationships, formula `#REF!`, empty formula caches, and advanced features. A passing result means structurally plausible, not fully calculated or visually approved.

## Financial-model quality rules

- Never hardcode a derived total, subtotal, margin, growth rate, balance, or roll-forward result.
- Use explicit assumptions and distinct input styling.
- Keep units consistent and label them (`$`, `$mm`, `%`, shares, dates).
- Show zeros as `-` where the chosen number format supports it; show negatives in parentheses/red when appropriate.
- Freeze header rows for long schedules, set useful column widths, and avoid gratuitous merged cells.
- Use formulas that survive normal edits. Prefer bounded ranges to whole-column references in large models.
- Preserve existing styles in template workbooks rather than imposing Athena's default style system.

## Advanced OOXML path (fallback, not default)

The bundled `scripts/` are adapted MiniMax helpers for structural operations. They are optional because Athena does not require or bundle Python, `openpyxl`, `pandas`, or LibreOffice.

Use them only when all of the following are true:

1. the native cell editor cannot express the required structural change;
2. a compatible Python runtime and required packages are available;
3. you have read `references/edit.md` and `references/ooxml-cheatsheet.md`;
4. you operate on a temporary working directory and deliver to a new output path;
5. you verify sheet identity, relationships, formulas, and advanced features afterward.

Do not claim zero format loss. Raw OOXML edits can invalidate digital signatures and structural shifts can leave defined names, tables, charts, pivots, external links, or shared formulas inconsistent. Escalate those cases to Excel automation or a purpose-built library when fidelity is critical.

`assets/minimal_xlsx/` is retained as a MiniMax reference template, with its invalid workbook comment removed. New routine workbooks should use `create_spreadsheet`, which emits a complete package directly.

## CSV and TSV

CSV/TSV has no workbook styling, formulas, multiple sheets, or OOXML package. Use Athena's ordinary file tools for direct text-table tasks. If the user asks for a real Excel workbook, create `.xlsx`; do not rename CSV bytes to `.xlsx`.

## Delivery

Return the absolute output path, summarize sheets/cells changed, report the static validation result, and clearly identify any remaining dynamic recalculation or visual QA limitation.
