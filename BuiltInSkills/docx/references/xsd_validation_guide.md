# DOCX validation gates

Use validation as layered evidence. No single check proves complete ECMA-376 conformance or visual correctness.

## Contents

1. [Commands](#commands)
2. [What each layer proves](#what-each-layer-proves)
3. [Template hard gate](#template-hard-gate)
4. [Failures and false positives](#failures-and-false-positives)

## Commands

Targeted WordprocessingML subset validation:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- validate --input output.docx --xsd "<skill-root>/assets/xsd/wml-subset.xsd"
```

Programmatic business validation:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- validate --input output.docx --business
```

Template-aware gate:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- validate --input output.docx --gate-check template.docx
```

Machine-readable output is available with `--json`.

## What each layer proves

### Targeted XSD

`wml-subset.xsd` checks common `word/document.xml` structure: paragraphs, runs, tables, section properties, basic drawings, comments, revision wrappers, and element ordering represented by the subset.

It is intentionally incomplete. It does not fully model DrawingML, VML, OMML, Microsoft extension namespaces, custom XML, every content control, every relationship, or every legal ECMA-376 construct. A failure may identify real corruption or an unsupported valid construct; inspect the reported namespace and element before changing the document.

### Programmatic business rules

`--business` checks practical invariants that are awkward or impossible to express in the targeted XSD, including:

- printable margin and font-size ranges;
- heading hierarchy gaps;
- table-width coherence;
- missing and orphaned relationships;
- comment-part consistency.

Errors block delivery. Warnings require review and a documented judgment; do not ignore them blindly.

### Template-aware gate

`--gate-check template.docx` compares output against the actual template rather than a generic schema. It checks:

- all template style IDs remain available;
- page size and margins match;
- default font matches when both documents expose one;
- heading sizes remain hierarchical.

This gate complements, but does not replace, content diffing and multi-section header/footer inspection.

## Template hard gate

For template application:

1. Apply Overlay or Base-Replace.
2. Run targeted XSD where applicable.
3. Run `--business`.
4. Run `--gate-check template.docx`.
5. Diff source and output for content preservation.
6. Render every page and inspect headers, footers, section transitions, page-number regimes, tables, drawings, TOC, and typography.
7. Fix and repeat until every blocking check passes.

Do not deliver a template result that fails business validation, the template-aware gate, content-preservation checks, or visual QA.

## Failures and false positives

### Ordering error

Read `references/openxml_element_order.md`, run `fix-order` on a copy, and revalidate. Never assume automatic repair preserved semantics; diff afterward.

### Vendor extension or markup compatibility

Known `w14`, `w15`, `w16*`, `mc:AlternateContent`, VML, math, or advanced DrawingML may sit outside the targeted subset. Confirm that the construct is known, relationships are intact, OpenXML SDK can open the package, and rendering succeeds.

### Gate mismatch

Do not force output to match the last template section if the document legitimately uses multiple regimes. Inspect every source/template/output section and use Base-Replace for structurally meaningful templates.

### Visual failure after structural pass

Structural checks cannot detect clipping, awkward pagination, missing fonts, stale fields, or design defects. Treat rendering as a separate mandatory gate.
