---
name: pptx
description: Create, inspect, edit, reorder and validate PowerPoint presentations (.pptx/.pptm/.potx/.potm) with Athena's native PresentationML tools. Use for slide decks, presentations, pitch decks, reports, templates, speaker notes, text replacement and slide-level structural changes.
triggers:
  - PowerPoint
  - powerpoint
  - PPT
  - pptx
  - pptm
  - potx
  - presentation
  - slide deck
  - slides
  - 演示文稿
  - 幻灯片
  - 演示
  - 路演
  - 汇报材料
---

# PPTX

Complete the presentation task and write the requested file. Athena's native tools run entirely in
the application process: no Python, Node, LibreOffice, Poppler, Open XML SDK or PowerPoint install is
required.

## Route the task

| Intent | Tool |
|---|---|
| Understand a deck, its notes, layouts or addressable shapes | `inspect_presentation` |
| Create a new common-elements deck | `create_presentation` |
| Replace text, rewrite a text shape, add a text box, duplicate/delete/move/reorder slides | `edit_presentation` |
| Check package integrity, common chart faults and likely text overflow | `validate_presentation` |

Legacy `.ppt` is not OOXML and is not supported. Ask for a `.pptx` conversion instead of pretending
that renaming the extension converts it.

## Mandatory workflow

1. Inspect any existing deck before editing it. Page with `startSlide` when needed. Use the returned
   1-based slide numbers and shape ids/names as edit targets.
2. Never overwrite the source. `edit_presentation` requires a distinct `outputPath` with the same
   extension and leaves the input recoverable.
3. Put all slide duplication, deletion, movement and reordering operations before text/content edits
   in the same call. Slide numbers are interpreted against the current order at each operation.
4. Use cross-run `{find,replace,all}` for wording changes. Use `setText` only when replacing a whole
   text shape is intentional, because it collapses that shape to paragraphs styled from its first run.
5. After every create or edit, run `validate_presentation`, then `inspect_presentation` on the output.
   Fix every structural error and every credible overflow warning before delivery.
6. Static validation is not rendering. Inspect every slide in Athena's presentation preview or the
   user's PowerPoint-compatible viewer for clipping, unintended overlap, bad wrapping, low contrast,
   missing fonts, chart/data mismatch and leftover placeholders.

## Creating a deck

`create_presentation` accepts `presentationJson` with `slides[]` and optional canvas/theme settings.
Coordinates and sizes are inches. Font and line sizes are points. Colours are 6/8 hexadecimal digits
without `#`.

```json
{
  "layout": "wide",
  "theme": {
    "background": "FFFFFF",
    "primary": "203864",
    "accent": "F05A28",
    "text": "222222",
    "muted": "666666",
    "font": "Arial",
    "eastAsiaFont": "Microsoft YaHei"
  },
  "slides": [
    { "layout": "title", "title": "Quarterly review", "subtitle": "FY2026 Q3" },
    {
      "layout": "titleAndContent",
      "title": "What changed",
      "bullets": [
        "Revenue grew 18%",
        { "text": "Enterprise led the increase", "level": 1 }
      ]
    },
    {
      "layout": "blank",
      "elements": [
        { "type": "text", "text": "Decision", "x": 0.8, "y": 0.5, "width": 5.0, "height": 0.8, "font": { "size": 28, "bold": true, "color": "203864" } },
        { "type": "image", "path": "chart.png", "x": 6.5, "y": 1.2, "width": 5.8, "height": 4.8, "altText": "Revenue by segment" },
        { "type": "table", "rows": [["Metric","Value"],["Growth","18%"]], "x": 0.8, "y": 1.7, "width": 5.0, "height": 2.0, "header": true }
      ]
    }
  ]
}
```

Supported layouts: `title`, `section`, `titleAndContent`, `blank`. Supported custom elements:

- `text`: `text` or `paragraphs`, bounds, `font`, `align`, `valign`, `margin`, optional `fill`/`line`.
- `shape`: `rect`, `roundRect`, `ellipse`, `line`; may include text except for line.
- `image`: PNG/JPEG/GIF/BMP path, bounds and useful `altText`.
- `table`: scalar `rows[][]`, bounds, header/body fills and font.

The generator does not create charts, SmartArt, animation, video, OLE or notes. Use images for custom
visuals when native editability is not required. Do not claim a chart is native if it is an image.

## Editing a deck

```json
[
  { "duplicateSlide": 2, "after": 3 },
  { "moveSlide": 4, "to": 2 },
  { "deleteSlide": 7 },
  { "find": "Old name", "replace": "New name", "all": true },
  { "slide": 2, "shapeId": 7, "setText": "Replacement text" },
  { "slide": 3, "setTitle": "Updated conclusion" },
  { "slide": 3, "addTextBox": { "text": "Source: internal analysis", "x": 0.8, "y": 6.9, "width": 5, "height": 0.3, "font": { "size": 10, "color": "666666" } } }
]
```

- `reorder` must contain every current slide number exactly once.
- `find` works across DrawingML runs. Set `includeNotes:true` to change existing speaker-note text too.
- Duplicated complex slides share charts, SmartArt and embedded-object parts with the source. Athena
  reports this. It is safe when only slide text is changed; do not attempt out-of-band XML chart edits.
- Deleting a slide retains possibly shared related media/chart parts. This is deliberate preservation,
  not a package-integrity error.

## Design and QA rules

Read `references/presets.md` with `read_skill_resource` before laying out a new deck. It carries five
palettes and seven copy-ready slide skeletons — cover, section divider, split, 2×2 cards, stat row,
numbered steps, table — with worked coordinates for the 13.333 × 7.5 inch canvas, plus the three
generator constraints that most often produce a broken layout: one style per element, `elements` order
is z-order, and box heights never stretch to fit their text.

- Use at least 0.5-inch outside margins and 0.3-inch gaps between unrelated content blocks.
- Default to 32–44 pt titles, 20–24 pt section headings, 16–20 pt body and 10–12 pt captions.
- Shorten content before shrinking body text. A one-line title must stay one line.
- Prefer Arial/Calibri/Cambria or another font known to exist on the target computer; font names are
  resolved by the viewer, not embedded by Athena.
- Use one dominant background/foreground relationship and one accent. Ensure text and icons have
  strong contrast.
- Avoid slide-after-slide title-and-bullets repetition. Use blank slides with images, tables, large
  callouts or a small number of basic shapes when they improve comprehension.
- Do not add decorative filler, fake UI controls, unexplained icons, or dense card grids.
- Put source citations in existing speaker notes when editing a deck that already has notes. Native
  note creation is not yet supported; otherwise place a concise source footer on the slide.

## Validation meaning

`validate_presentation.valid=true` means the OOXML package passed Athena's static invariants. It does
not mean the deck is visually correct. `overflowWarnings` are conservative SkiaSharp estimates with a
confidence value; medium-confidence warnings must be fixed or visually disproved, while low-confidence
warnings require direct inspection because theme inheritance, groups or font substitution are involved.
`visualValidationRequired` is always true.
