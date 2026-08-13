---
name: xlsx
description: Create, inspect, edit, restructure, convert and validate Excel workbooks (.xlsx/.xlsm) and delimited data (.csv/.tsv) with Athena's built-in spreadsheet tools - values, formulas, number formats, fonts, fills, merged cells, inserting and deleting rows or columns, and CSV import/export. Use for reports, budgets, financial models, data cleanup and template filling; any task whose deliverable is a spreadsheet file.
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
  - 报表
  - 套模板
  - CSV
  - TSV
---

# XLSX

Complete the spreadsheet task and write the requested file. Do not merely explain how to do it.
Every operation runs on native tools: no Python, no .NET SDK and no Excel installation required.

## Route the task

| Intent | Tool |
|---|---|
| Read or understand a workbook | `inspect_spreadsheet` |
| Build a new workbook | `create_spreadsheet` |
| Change cell values, formulas, styles, merges | `edit_spreadsheet` |
| Insert or delete whole rows and columns | `modify_spreadsheet_structure` |
| CSV/TSV in or out, or read a sheet too large to preview | `convert_spreadsheet` |
| Check the delivered file | `validate_spreadsheet` |

## Mandatory workflow

1. Inspect before changing anything you did not just create. Note sheet names, used ranges, target cells, formulas and merged ranges.
2. Never overwrite the source. Editing tools require a distinct `outputPath` and leave the input untouched.
3. Make the narrowest change that satisfies the request.
4. Put derived numbers in formulas, not hardcoded values, unless static results were explicitly requested.
5. Run `validate_spreadsheet` on every workbook you deliver, and report its warnings.
6. State plainly that formulas are not calculated and layout is not rendered. Only Excel or LibreOffice can do that.

## Styling and layout

- `create_spreadsheet` accepts built-in aliases (`header`, `input`, `formula`, `currency-input`, `percent-formula`, `year`, ...) for quick financial sheets. They follow the usual modelling convention: blue = typed input, black = calculated, green = link to another sheet, red = link outside the workbook. The `currency-*` aliases use a neutral thousands format with no currency symbol - put the symbol in the column header, or declare a custom `numberFormat` when the cell itself must show one.
- For anything else, declare a workbook-level `styles` array and reference entries by name, or pass an inline style object. A style sets `font` (including CJK families such as 微软雅黑 or PingFang SC), `fill`, `border`, `numberFormat` and `align`.
- Use `merges` on a sheet, or `{sheet, merge}` / `{sheet, unmerge}` in `edit_spreadsheet`. A merge keeps only the top-left cell's content, must cover more than one cell, and must not overlap another merge.
- `edit_spreadsheet` styles cells in workbooks Athena did not create by registering a new format inside that workbook, so a `styleIndex` taken from another file is never valid. Use `copyStyleFrom` to reuse a style that already exists in the file.

## Structural edits

- `modify_spreadsheet_structure` inserts or deletes whole rows and columns and rewrites what moves with them: formulas on every sheet, merged ranges, autofilter and sort ranges, conditional formatting, data validation, hyperlinks, column widths and defined names.
- `index` is 1-based, and column operations also accept a letter. Inserting at index N pushes the current row/column N down or right.
- A range that loses only part of itself shrinks; a reference whose target is entirely deleted becomes `#REF!`, exactly as Excel would produce. Validate afterwards and repair any `#REF!` you introduced.
- The tool refuses to cut through a structured table. Convert the table to a range first, or make that edit in Excel.

## Large sheets

`inspect_spreadsheet` returns a bounded window. When `hasMoreRows` or `hasMoreColumns` is true, call it again with the returned `nextStartRow` / `nextStartColumn`. To read a whole sheet at once, export it with `convert_spreadsheet` and read the CSV.

## Boundaries

Charts, pivot tables, images, macros and slicers survive an edit but cannot be created or modified; formulas are never calculated; `.xls` and `.ods` are not supported. When a request needs one of these, say so before starting and offer the closest native alternative rather than delivering a file that looks finished but is not.
