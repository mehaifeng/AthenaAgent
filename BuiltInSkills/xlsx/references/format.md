# Professional spreadsheet formatting

This guide retains MiniMax's core financial-model conventions while routing ordinary creation through Athena's native tools.

## Design principles

1. **Hierarchy** — titles, section headers, table headers, body rows, and totals must be visually distinct.
2. **Consistency** — use one font system, stable number formats, and repeatable spacing.
3. **Semantic color** — color communicates cell purpose, not decoration.
4. **Readable density** — use column width, alignment, and whitespace instead of excessive borders.
5. **Auditability** — distinguish inputs from formulas and show units/sources.

## Financial color convention

| Meaning | Color | Native style aliases |
|---|---|---|
| Hardcoded input / assumption | Blue | `input`, `currency-input`, `percent-input`, `integer-input` |
| Same-sheet formula | Black | `formula`, `currency-formula`, `percent-formula`, `integer-formula` |
| Cross-sheet reference | Green | `cross-sheet` |
| External link | Red | `external-link` |
| Key assumption requiring review | Blue on yellow | `assumption` |
| Table header | Bold white on dark blue | `header` |

Do not apply color to arbitrary labels merely to make the workbook colorful.

## Numbers

- Currency: include currency symbol/unit in the heading or use a currency number format consistently.
- Percentages: store `8%` as `0.08`, not `8`.
- Years: avoid thousands separators.
- Negatives: parentheses are preferred in financial statements.
- Zeros: display as `-` where appropriate.
- Precision: do not imply false accuracy. Use a consistent decimal policy per section.

Athena's built-in aliases provide general financial formats. For a template with specific formats, preserve/copy its existing style instead.

## Layout

- Freeze the primary header row on long sheets.
- Keep labels left-aligned and numeric values right-aligned.
- Use 20-30 character widths for primary labels and 12-16 for common numeric columns as a starting point.
- Avoid empty columns/rows as the only structural signal; use section headers and controlled spacing.
- Avoid merged cells in data regions because they interfere with filtering, sorting, and automation.
- Use borders sparingly: a top border for totals and a stronger rule for final outputs is usually enough.

## Formula design

- Derived figures must be formulas.
- Use absolute references for stable assumptions (`$B$4`) and relative references for copied schedules.
- Quote sheet names containing spaces (`'Revenue Detail'!B4`).
- Prefer transparent formulas over long, deeply nested expressions; use helper rows/sheets when useful.
- Do not embed magic constants inside formulas when the value is a reusable assumption.

## Existing templates

Inspect the workbook first. Use `copyStyleFrom` from an analogous existing cell so fonts, fills, borders, number formats, alignment, and protection remain coherent with the template. Native creation aliases are for new workbooks, not for restyling supplied templates.

## QA checklist

- headers visible and frozen where useful;
- labels/units clear;
- inputs/formulas visually distinct;
- formulas used for every derived value;
- number formats consistent across comparable rows/columns;
- totals and key outputs clearly signposted;
- no `#REF!` or static validation errors;
- recalculation and visual inspection completed in Excel/LibreOffice when available.
