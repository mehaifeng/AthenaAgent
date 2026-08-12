# Repair formulas or values

For a known bad cell:

1. inspect the workbook and neighboring formulas;
2. infer the intended pattern from labels, adjacent periods, and analogous rows;
3. use `edit_spreadsheet` on a new output path;
4. copy style from the analogous cell;
5. validate and request engine recalculation when formula results matter.

Never replace a broken formula with a hardcoded result merely to silence validation. If the intended logic cannot be established, report the ambiguity instead of inventing a formula.
