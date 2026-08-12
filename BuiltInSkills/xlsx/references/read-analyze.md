# Read and analyze

Start with `inspect_spreadsheet`. It handles shared strings, inline strings, numbers, booleans, formulas, styles, used bounds, and feature discovery without changing the workbook.

For large data analysis, increase preview bounds only enough to discover the schema. If the full dataset must be aggregated and no spreadsheet engine/library is available, extract the relevant worksheet data with a suitable local tool or ask for CSV. Never infer totals from a truncated preview.

When the user requests a fixed decimal precision, apply it consistently to all reported numeric values. Compute sums, means, counts, and ratios from the actual source column/range, not from rounded display strings.

Formula cells may have stale or empty cached results. Treat the formula text as authoritative until a real engine recalculates the workbook.
