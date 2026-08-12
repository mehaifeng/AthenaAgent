---
name: docx
description: Create, edit, analyze, restyle, validate, redline, and visually verify professional Microsoft Word documents (.docx and legacy .doc). Use for reports, proposals, contracts, legal reviews, tracked changes, comments, forms, template application, thesis and multi-section layouts, CJK or GB/T 9704 documents, tables, images, headers/footers, TOCs, and document-format troubleshooting.
triggers:
  - Word
  - docx
  - Word document
  - document
  - 文档
  - Word 文档
  - 报告
  - 合同
  - 公文
  - 排版
  - 套模板
  - redline
  - tracked changes
  - revision marks
---

# DOCX production

Produce robust Word documents with progressive disclosure. Choose one execution path, read only the references required for that path, and validate every changed artifact.

## Resolve the skill root

Use the `rootDirectory` returned by `activate_skill` as `<skill-root>`. Resolve every bundled path from it; never assume the current working directory is the Skill directory.

The bundled OpenXML CLI is:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- <command> [options]
```

The CLI and derived references are MIT-licensed; retain `LICENSE-MiniMax.txt` when redistributing them.

## Route the task

1. **Read or analyze only**
   - Use Pandoc for semantic text, including revisions.
   - Use the bundled CLI `analyze` for sections, headings, tables, drawings, headers/footers, styles, and package structure.
2. **Create a new document**
   - Use the bundled OpenXML SDK CLI for report, letter, memo, and academic shells.
   - Use direct OpenXML SDK code based on bundled, compiling samples for complex or repeated production.
3. **Edit an existing document**
   - Use the bundled CLI for simple replacement, placeholder filling, table filling, analysis, and diffing.
   - Use direct OpenXML SDK code, based on the bundled samples, for structural edits.
   - Use the Redlining workflow for third-party documents, legal/contract review, or whenever reviewable tracked changes are requested.
4. **Apply a template or restyle**
   - Use the dedicated Template workflow. Decide between Overlay and Base-Replace before editing.
5. **Convert legacy `.doc`**
   - Use LibreOffice through `scripts/doc_to_docx.sh`, then continue as DOCX.
6. **Diagnose a broken or ugly document**
   - Read `references/troubleshooting.md`, search by visible symptom, repair, validate, and render again.

If a request spans paths, run them in sequence, such as Create -> Apply Template -> Validate -> Render.

## Environment and safety

The skill never installs dependencies automatically. `scripts/setup.ps1` and `scripts/setup.sh` are manual helpers that the user must run themselves. If a required command is missing, tell the user what to install and how — never invoke the setup scripts on their behalf.

### Dependency tiers and degraded behavior

| Tool | Tier | Required for | Missing-tool behavior |
|------|------|--------------|----------------------|
| `dotnet` SDK ≥ 8 | Required | All CLI commands: `create`, `edit`, `redline`, `validate`, `apply-template`, `analyze`, `diff`, `merge-runs`, `fix-order` | Hard fail — stop and tell the user to install .NET SDK 8+ from <https://dotnet.microsoft.com/download> |
| `pandoc` | Strongly recommended | Semantic text extraction with `--track-changes=all`, Markdown planning for redlining, `scenario_a/b/c` references | Skip Markdown-based planning and verification; rely on the CLI's `analyze --json` for structural view. Tell the user redlining is degraded without pandoc |
| `soffice` (LibreOffice) | Optional | `.doc` → `.docx` conversion (`scripts/doc_to_docx.sh`), visual QA via PDF rendering | Skip legacy `.doc` conversion (tell the user to convert it themselves); skip visual QA and rely on structural validation |
| `pdftoppm` (Poppler) | Optional | Page-by-page image rendering for visual inspection | Skip image-based visual QA; structural validation still runs |

When the user's request needs a missing optional tool, **say so explicitly before proceeding**. Example: "Visual QA requires LibreOffice and Poppler, which are not installed. I will run structural validation only — open the file in Word yourself for visual checks." Do not silently downgrade.

### Safety rules

- Check required commands before use. Run `dotnet --version`, `pandoc --version`, `soffice --version`, `pdftoppm -v` once per session and remember the result; do not re-check on every command.
- Build the bundled CLI on first use. Do not install SDKs, packages, fonts, LibreOffice, or other system dependencies without user authorization.
- `scripts/setup.ps1` and `scripts/setup.sh` are optional setup helpers, not permission to mutate the system silently.
- Work on a copy of every user-supplied document. Never overwrite the only original.
- Keep intermediate files in a task-specific directory and make the final output path explicit.
- Never claim a file is correct from XML inspection alone. Run structural validation and visual QA when the tools are available.

## Read and analyze

Extract semantic content and preserve revision marks:

```bash
pandoc --track-changes=all input.docx -o current.md
```

Use `--track-changes=accept` or `reject` only when the user's intent requires that view. Do not silently accept or reject revisions in the document.

Analyze package structure:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- analyze --input input.docx --json
```

For raw inspection, treat DOCX as ZIP and inspect `word/document.xml`, `word/styles.xml`, section properties, relationships, headers/footers, comments, numbering, and media. Do not edit the archive casually; use OpenXML SDK patterns and preserve relationships.

## Create workflow

Read these before creating:

- `references/scenario_a_create.md`
- `references/design_principles.md`
- `references/typography_guide.md`
- For CJK: `references/cjk_typography.md`
- The relevant file under `scripts/dotnet/MiniMaxAIDocx.Core/Samples/`

Do not invent aesthetic values when a bundled recipe fits. Select from `AestheticRecipeSamples.cs` and its batch files: Modern Corporate, Academic Thesis, Executive Brief, Chinese Government (GB/T 9704), Minimal Modern, IEEE, ACM, APA 7, MLA 9, Chicago/Turabian, Springer LNCS, Nature, or HBR.

Use the CLI for simple report, letter, memo, or academic shells:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- create --type report --output out.docx --title "Title"
```

For complex structure, write C# against the bundled project and copy tested patterns from the relevant sample rather than reconstructing OpenXML from memory.

## Edit workflow

Read `references/scenario_b_edit_content.md`. Preview -> analyze -> edit a copy -> diff -> validate -> render.

Examples:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- edit replace-text --input in.docx --output out.docx --search "OLD" --replace "NEW"
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- edit fill-placeholders --input in.docx --output out.docx --mapping values.json
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- diff --before in.docx --after out.docx
```

For tables, styles, sections, comments, drawings, numbering, fields, headers/footers, or relationship changes, read `references/openxml_element_order.md` and the matching C# sample first.

For tracked review, read `references/redlining_workflow.md` and `references/track_changes_guide.md` before editing. The Athena workflow takes precedence over generic replacement guidance.

Use the deterministic CLI for exact plain-text changes. It refuses ambiguous match counts and writes a separate output:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- redline replace --input in.docx --output reviewed.docx --search "old text" --replace "new text" --author "Reviewer" --expected-count 1
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- redline apply-plan --input in.docx --output reviewed.docx --plan changes.json --author "Reviewer"
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- redline verify --input reviewed.docx --author "Reviewer" --require-changes
```

`changes.json` is an array of `{ "search", "replace", "expectedCount" }`. Batch only 3-10 reviewed changes at a time. The CLI preserves unchanged runs and their attributes, uses `w:delText` for deletions, assigns unique revision IDs, and rejects exact searches inside unsupported complex run structures. Use direct OpenXML code for fields, hyperlinks, content controls, moves, comments, or structural redlines.

## Template workflow

Read `references/scenario_c_apply_template.md` completely before manipulating either file. Also read `references/cjk_university_template_guide.md` for Chinese university templates.

Analyze both source and template, then choose:

- **Overlay**: the template is primarily styles/theme and the source structure should remain.
- **Base-Replace**: the template contains meaningful cover, TOC, section, header/footer, numbering, or example structure. Copy the template as the output base and replace only its content zones.

For multi-section or 10+ section templates, default to Base-Replace. Preserve header/footer parts, relationships, page-number regimes, `titlePg`, section breaks, and zone boundaries from the template.

Simple overlay:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- apply-template --input source.docx --template template.docx --output out.docx --mode overlay
```

Critical rules:

- Map styles by exact ID, then style name, then explicit manual mapping. Do not assume `Heading1`; Chinese templates often use numeric IDs.
- Strip source direct formatting only when applying template styles; retain semantic properties and text.
- Preserve each content paragraph exactly once.
- Never add empty paragraphs for spacing or section separation.
- Use section properties and style spacing for layout.
- Copy template header/footer XML and related parts; do not recreate complex page furniture from memory.
- A chapter that must start on an odd page needs an `oddPage` break before every applicable chapter.
- A two-column chapter commonly needs three transitions: odd-page chapter start, continuous two-column start, and continuous return to one column.

Template output must pass the validation gate and content-preservation diff before delivery.

## OpenXML invariants

Read `references/openxml_element_order.md` before direct XML or SDK manipulation.

- `w:p`: `w:pPr` before runs.
- `w:r`: `w:rPr` before text/break/tab content.
- `w:tbl`: `w:tblPr`, then `w:tblGrid`, then rows.
- `w:tr`: `w:trPr` before cells.
- `w:tc`: `w:tcPr` before block content and at least one `w:p`.
- `w:body`: final body-level `w:sectPr` is last.
- Deleted revision text uses `w:delText`; inserted revision text uses `w:t`.
- `w:sz` uses half-points; DXA uses 1440 per inch; EMU uses 914400 per inch.
- Heading styles require `outlineLvl` for TOC and navigation behavior.
- Preserve relationship IDs or remap every reference when copying parts.

## Validation and visual QA

Run after every write. For template application, structural editing, comments, redlining, or multi-section output, all applicable checks are mandatory.

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- validate --input out.docx --xsd "<skill-root>/assets/xsd/wml-subset.xsd"
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- validate --input out.docx --business
```

`merge-runs` is an optional optimization for newly created, revision-free documents only. Never run it blindly on reviewed documents, because run boundaries and RSIDs may be evidence-bearing.

If ordering validation fails:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- fix-order --input out.docx
```

Re-run validation after repair. Treat `wml-subset.xsd` as a targeted guard, not a complete ECMA-376 conformance proof; valid advanced constructs may require SDK validation plus business checks and rendering.

For template output, run the template-aware gate:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- validate --input out.docx --gate-check template.docx
```

Then diff source vs output where content preservation matters:

```text
dotnet run --project "<skill-root>/scripts/dotnet/MiniMaxAIDocx.Cli" -- diff --before source.docx --after out.docx
```

Render the final DOCX through LibreOffice to PDF and inspect every page image. Verify at minimum:

- no clipping, overlap, repair warning, blank surprise page, or orphaned heading;
- correct fonts, hierarchy, spacing, page breaks, columns, and margins;
- correct table widths/repetition and image placement;
- correct headers, footers, numbering regimes, TOC/field behavior, comments, and revision marks;
- no content loss or duplicated paragraphs.

## Reference router

Load only what the current task needs.

| Need | Read |
|---|---|
| New document | `references/scenario_a_create.md`, `references/design_principles.md`, `references/typography_guide.md` |
| Edit/fill | `references/scenario_b_edit_content.md` |
| Tracked review | `references/redlining_workflow.md`, then `references/track_changes_guide.md` |
| Apply/rebuild from template | `references/scenario_c_apply_template.md` |
| CJK or GB/T 9704 | `references/cjk_typography.md` |
| Chinese thesis template | `references/cjk_university_template_guide.md` |
| Design diagnosis | `references/design_good_bad_examples.md` |
| Visible failure | `references/troubleshooting.md` |
| XML order | `references/openxml_element_order.md` |
| Units | `references/openxml_units.md` |
| Namespaces/parts | `references/openxml_namespaces.md` |
| Comments | `references/comments_guide.md` and `FootnoteAndCommentSamples.cs` |
| XSD/gates | `references/xsd_validation_guide.md` |

Relevant C# samples live under `scripts/dotnet/MiniMaxAIDocx.Core/Samples/`. Read the narrowest matching sample before writing C#; do not load the entire encyclopedia or every sample.
