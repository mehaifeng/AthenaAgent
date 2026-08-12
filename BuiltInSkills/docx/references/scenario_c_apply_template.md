# Scenario C: Apply a template safely

Use this workflow when an existing DOCX must adopt a template's design or structure. Template application is not a single operation: first classify the template, then choose exactly one mode.

## Required inspection

Analyze both files before writing:

```text
$CLI analyze --input source.docx --json
$CLI analyze --input template.docx --json
```

Inspect the rendered template as well as its XML. Record:

- paragraph and section counts;
- cover, declaration, abstract, TOC, body, references, and back-matter zones;
- style IDs and names actually used by paragraphs;
- page size, margins, orientation, columns, break types, and `titlePg` per section;
- header/footer variants and page-number regimes;
- images, hyperlinks, numbering, fields, and tracked changes.

Do not infer a style ID from its visible name. CJK templates often use numeric or short IDs.

## Choose a mode

### Overlay

Use Overlay when the source structure must remain and the template is mainly a style/theme/layout source.

```text
$CLI apply-template --input source.docx --template template.docx --output out.docx --mode overlay
```

Overlay behavior:

- keeps source body content and review history;
- merges numbering with ID remapping instead of overwriting source definitions;
- makes template definitions win when the same style ID exists;
- copies the template theme;
- maps one template section to every source section, or maps equal section counts one-to-one;
- refuses ambiguous section-count mappings;
- copies headers and footers only when explicitly requested.

To apply template headers and footers:

```text
$CLI apply-template --input source.docx --template template.docx --output out.docx --mode overlay --apply-headers-footers
```

Header/footer copying includes supported images and external hyperlinks. Unsupported related part types cause a hard failure rather than a silently broken file.

### Base-Replace

Use Base-Replace when the template contains valuable structure such as a cover, declaration, TOC, section layout, header/footer regimes, or back matter. The template becomes the output base; only one explicitly marked body zone is replaced.

Add unique marker paragraphs to a working copy of the template, for example:

```text
[[ATHENA_BODY_START]]
template example content to replace
[[ATHENA_BODY_END]]
```

The markers must be direct body paragraphs, must each occur exactly once, and the replacement zone must not cross a section boundary.

```text
$CLI apply-template --input source.docx --template marked-template.docx --output out.docx \
  --mode base-replace \
  --start-marker "[[ATHENA_BODY_START]]" \
  --end-marker "[[ATHENA_BODY_END]]"
```

Base-Replace behavior:

- starts from a complete copy of the template;
- preserves template front matter, back matter, fields, section structure, and page furniture;
- inserts source paragraphs and tables in document order;
- removes source paragraph-level section properties so they cannot corrupt the template zone;
- merges styles and numbering with ID remapping;
- copies referenced images and external hyperlinks;
- refuses missing, duplicated, reversed, or cross-section markers;
- refuses unsupported relationships rather than dropping them.

Do not use guessed text such as “Chapter 1” as a boundary. Add unambiguous markers to a working template copy.

## Multi-section rules

- One template section can be mapped to all output sections.
- Otherwise template and output section counts must match exactly.
- Preserve `oddPage`, `evenPage`, `nextPage`, and `continuous` transitions intentionally.
- Preserve `titlePg` when first-page headers/footers differ.
- Preserve page-number format and restart settings per section.
- A temporary two-column region normally requires a section transition into two columns and another back to one column.
- Never create spacing or page transitions with empty paragraphs.

## Direct-format cleanup

Template style application can be masked by source direct formatting. Remove direct `w:rPr` and `w:pPr` formatting only when the task explicitly requires a style-only result, and retain semantic properties such as:

- paragraph style, numbering, outline level, keep-with-next, and page-break behavior;
- language and CJK font declarations required by the template;
- bookmarks, fields, hyperlinks, comments, and revision markup.

Do not perform blanket XML deletion on a reviewed or legally significant file.

## Hard validation gate

Run all checks after template application:

```text
$CLI validate --input out.docx --xsd "<skill-root>/assets/xsd/wml-subset.xsd"
$CLI validate --input out.docx --business
$CLI validate --input out.docx --gate-check template.docx
$CLI diff --before source.docx --after out.docx
```

The gate compares every mapped section, not only the final section. It checks template styles, page size, orientation, margins, break type, title-page setting, page-number settings, column count, default font, and heading-size hierarchy.

Treat the bundled XSD as a targeted structural guard, not complete ECMA-376 certification. A deliverable must also open without repair warnings and pass visual inspection of every rendered page.

For Overlay, diff should show no unintended content loss. For Base-Replace, verify the intended source body appears exactly once while the expected template zones remain.

## Failure policy

Do not deliver if any of these occurs:

- a marker or section mapping is ambiguous;
- a relationship cannot be transferred safely;
- validation reports missing relationships or invalid element order;
- source content is missing or duplicated;
- Word/LibreOffice reports a repair;
- rendered headers, footers, numbering, columns, or page breaks differ from the approved template.
