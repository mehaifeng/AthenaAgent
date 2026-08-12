# Precision redlining workflow

Use this workflow for legal, contract, academic, regulated, or third-party document review when changes must remain visible and reviewable. Preserve the original document's formatting and revision identity everywhere that content is unchanged.

## Contents

1. [Non-negotiable rules](#non-negotiable-rules)
2. [Plan from a semantic view](#plan-from-a-semantic-view)
3. [Map changes to OOXML](#map-changes-to-ooxml)
4. [Apply changes in batches](#apply-changes-in-batches)
5. [Revision patterns](#revision-patterns)
6. [Verification](#verification)

## Non-negotiable rules

- Mark only text or properties that actually change.
- Reuse the original unchanged `w:r` nodes, including their `w:rPr`, `w:rsidR`, whitespace semantics, bookmarks, proofing markers, and surrounding field structure.
- Use `w:delText` inside deletions and `w:t` inside insertions.
- Give each revision a unique ID and consistent author; use UTC timestamps.
- Do not accept or reject existing revisions unless explicitly requested.
- Do not flatten existing fields, hyperlinks, content controls, comments, numbering, or relationships.
- Work on a copy and keep a stable before-file for diffing.
- Batch 3-10 related changes so a failure can be isolated without losing efficiency.

## Plan from a semantic view

Create a review view before touching XML:

```bash
pandoc --track-changes=all input.docx -o current.md
```

Inventory every requested change and group it by section, change type, or proximity. Use headings, clause numbers, paragraph IDs, bookmarks, or unique surrounding text as anchors.

Do not use Markdown line numbers as XML addresses. Markdown lines do not map reliably to `w:p` or `w:r` nodes.

For each planned change, record:

| Field | Purpose |
|---|---|
| Anchor | Unique nearby text or structural identifier |
| Old text/property | What must be removed or superseded |
| New text/property | What must be inserted or proposed |
| Scope | Run, paragraph, table cell, field, section, or relationship |
| Verification | A positive and negative check |

## Map changes to OOXML

Analyze structure first:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- analyze --input input.docx --json
```

Inspect `word/document.xml` immediately before scripting each batch. Search for unique text with surrounding context and verify how the phrase is split across runs. Re-run the search before the next batch because serialized XML positions can change.

Read `references/track_changes_guide.md` and `scripts/dotnet/MiniMaxAIDocx.Core/Samples/TrackChangesSamples.cs` before writing revision code. For comments, also read `references/comments_guide.md` and `FootnoteAndCommentSamples.cs`.

## Apply changes in batches

For every batch:

1. Copy the current working DOCX to a batch checkpoint.
2. Locate every anchor uniquely; fail rather than editing an ambiguous match.
3. Split affected runs at exact change boundaries.
4. Clone unchanged run fragments with their original properties and RSIDs.
5. Wrap only deleted content in a deletion and only new content in an insertion.
6. Save through OpenXML SDK.
7. Run structural/business validation and a focused diff.
8. Inspect the semantic Markdown view before continuing.

Prefer one deterministic script per logical batch. Do not perform global string replacement inside XML.

## Revision patterns

Changing `30 days` to `60 days` should preserve the unchanged fragments:

```xml
<w:r w:rsidR="00AB12CD"><w:t xml:space="preserve">The term is </w:t></w:r>
<w:del w:id="21" w:author="Reviewer" w:date="2026-08-12T00:00:00Z">
  <w:r><w:delText>30</w:delText></w:r>
</w:del>
<w:ins w:id="22" w:author="Reviewer" w:date="2026-08-12T00:00:00Z">
  <w:r><w:t>60</w:t></w:r>
</w:ins>
<w:r w:rsidR="00AB12CD"><w:t xml:space="preserve"> days.</w:t></w:r>
```

Do not replace the whole sentence:

```xml
<!-- Avoid: unchanged words become noisy revisions and original RSIDs are lost. -->
<w:del><w:r><w:delText>The term is 30 days.</w:delText></w:r></w:del>
<w:ins><w:r><w:t>The term is 60 days.</w:t></w:r></w:ins>
```

When a change crosses run boundaries, reconstruct the smallest changed span while retaining all unaffected original nodes. When deleting or inserting a paragraph break, follow the paragraph-mark patterns in `track_changes_guide.md`; a paragraph break is not ordinary text.

For formatting-only proposals, use the corresponding `w:rPrChange`, `w:pPrChange`, or section/table revision element. Do not simulate formatting changes by deleting and reinserting text.

## Verification

After each batch:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- validate --input working.docx --business
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- diff --before checkpoint.docx --after working.docx
pandoc --track-changes=all working.docx -o batch-check.md
```

After all batches:

1. Search the semantic view for every old phrase that should be absent from the final accepted reading and every new phrase that should be present.
2. Confirm the number and authorship of new insertions, deletions, property changes, and comments.
3. Confirm existing revisions and comments were preserved.
4. Run XSD-targeted and business validation.
5. Diff against the untouched original and investigate every unexpected structural change.
6. Render to PDF and inspect all pages, including revision balloons or markup views when available.
7. Deliver the redlined file without accepting the proposed revisions.

