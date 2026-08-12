# Validation gate

Always run `validate_spreadsheet` on the output workbook.

It checks:

- bounded ZIP/package safety;
- XML well-formedness with DTDs prohibited;
- required OOXML parts;
- internal relationship targets;
- `#REF!` in formulas;
- formula cells without cached results;
- advanced-feature presence.

It does not:

- execute formulas or detect all circular references;
- prove numerical correctness;
- render sheets or detect clipped text;
- verify charts, pivots, macros, external links, or data connections;
- validate Excel's full XSD/business-rule surface.

## Dynamic verification

If available, open/save/recalculate in Excel or use `scripts/libreoffice_recalc.py`, then re-run static validation. LibreOffice can change workbook formatting or unsupported features, so retain the pre-recalculation artifact for comparison.

## Delivery threshold

Do not deliver when static validation reports errors. Warnings are acceptable only when explained, especially empty formula caches and advanced features requiring engine verification.
