# Edit workbooks safely

## Native path

1. Run `inspect_spreadsheet`.
2. Confirm the actual sheet name and target cells. Do not trust a prompt's row number when it also provides a label; locate the label in the preview/data.
3. Use `edit_spreadsheet` with a distinct output and the same extension.
4. Prefer `copyStyleFrom` from an adjacent or analogous template cell.
5. Validate the output and compare sheet names and representative untouched cells.

The native editor copies the source package, changes only selected worksheet parts, updates the used dimension, removes a stale calculation chain when formulas change, and asks Excel to fully recalculate. Untouched ZIP parts remain byte-copied into the new package, though the package itself is newly written.

## High-risk features

Pause and disclose risk if inspection reports macros, digital signatures, pivots, charts, tables, external links, slicers, or drawings. Cell edits generally preserve their parts, but dependencies may still refer to edited ranges. A digital signature will not remain trustworthy after any package modification.

## Structural fallback

The optional MiniMax helpers include:

- `xlsx_unpack.py` / `xlsx_pack.py`
- `xlsx_insert_row.py`
- `xlsx_add_column.py`
- `xlsx_shift_rows.py`
- `shared_strings_builder.py`
- `style_audit.py`

They require Python and, depending on the operation, third-party packages. Use `--help` for their current interface. Work only in a new temporary directory and keep the original untouched.

Structural shifts are not complete dependency rewrites. After inserting/shifting rows or columns, audit at least:

- worksheet formulas, including shared/array formulas;
- merged cells, filters, validations, hyperlinks, conditional formatting;
- workbook defined names and print areas;
- table ranges and structured references;
- chart series, pivot sources/caches, external links;
- drawing anchors and comments.

If these matter, prefer Excel automation or an OOXML library designed for that feature set.

## No lossy round-trip promises

Do not advertise “zero format loss.” Generic libraries and XML pretty-printers may normalize markup or drop unsupported extensions. The native path is intentionally surgical; the optional unpack path is an expert fallback.
