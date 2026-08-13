---
name: docx
description: Create, read, edit, restyle and validate Word documents (.docx/.docm) with Athena's built-in document tools - headings and outlines, mixed run formatting, bulleted and numbered lists, tables, images, page setup, custom styles, a table-of-contents field, cross-run find and replace, and export to Markdown. Use for reports, proposals, memos, letters, meeting notes, contracts and any task whose deliverable is a Word file.
triggers:
  - Word
  - docx
  - Word document
  - Word 文档
  - 文档
  - 报告
  - 方案
  - 合同
  - 公文
  - 排版
  - 套模板
  - 目录
---

# DOCX

Complete the document task and write the requested file. Do not merely explain how to do it.
Every operation runs on native tools: no Python, no .NET SDK, no Word or LibreOffice installation required.

## Route the task

| Intent | Tool |
|---|---|
| Understand an existing document | `inspect_document` |
| Read a long document end to end | `convert_document` (to Markdown) |
| Write a new document | `create_document` |
| Change text, styles, tables; insert or delete content | `edit_document` |
| Check the delivered file | `validate_document` |

## Mandatory workflow

1. Inspect before changing anything you did not just create. `inspect_document` returns the heading outline plus each body paragraph's 1-based index, style and heading path - those indexes are what `edit_document` targets.
2. Never overwrite the source. Editing writes to a distinct `outputPath` and leaves the input untouched.
3. Prefer the narrowest operation: `find`/`replace` for wording, `setText` for one paragraph, `setStyle` or `format` for appearance. Rewrite whole sections only when asked to.
4. All indexes inside one `edit_document` call refer to the document as it was read, so you can insert near the top and still target a later paragraph by its original index in the same call.
5. Run `validate_document` on every document you deliver and report its warnings.
6. State plainly that pagination, page numbers and field results are not computed. Only Word or LibreOffice does that.

## Writing documents

- Structure a document as `blocks`: `heading` (level 1-6), `paragraph`, `title`, `quote`, `list`, `table`, `image`, `pageBreak`, `toc`.
- Headings use Word's built-in styles, so the navigation pane, outline and any TOC work without extra setup. Do not fake a heading with bold body text.
- A paragraph carries either `text` or `runs[]`; use `runs` when one sentence mixes formatting, and a named style when the same look repeats.
- Declare reusable looks in the document-level `styles[]` array and reference them by name; that is what makes a document restylable later.
- For Chinese documents set `font.eastAsia` (for example 等线, 微软雅黑, 宋体) alongside `font.name`; the East Asian family is what Word applies to CJK characters. Sizes, margins and spacing are all in points.
- A `toc` block writes a real TOC field. It stays a placeholder until Word or LibreOffice updates fields on open - say so when delivering.

## Editing documents

- Target by `paragraph` index, by `table`/`row`/`column`, or by matching text with `find`.
- `find`/`replace` is run-aware: Word routinely splits one sentence across several runs, and the match still works. It fails loudly when the text is absent rather than silently doing nothing.
- `defineStyle` registers a new style into the document being edited, so `setStyle` can then apply it. A `styleIndex` or style id copied from another document is never valid here.
- Tables support cell `setText`, `appendRow` (which inherits the last row's widths and cell properties) and `deleteRow`.

## Boundaries

Tracked changes, comments, headers, footers, footnotes, charts, text boxes and macros are preserved when editing but cannot be created or modified; legacy `.doc` is not supported - convert it in Word or LibreOffice first. Nothing is paginated or rendered. When a request needs one of these, say so before starting and offer the closest native alternative rather than delivering a file that looks finished but is not.
