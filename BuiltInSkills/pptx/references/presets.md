# Deck presets

Ready-to-adapt palettes and slide skeletons for `create_presentation`. Copy a skeleton, replace the
copy, keep the geometry. Every coordinate below is tuned for the default `"layout": "wide"` canvas:
**13.333 × 7.5 inches**, 0.8" outer margin, content width 11.733".

## Four rules the generator enforces

1. **One style per element.** A `text` or `shape` element renders all of its paragraphs in a single
   size/weight/colour. A bold heading above lighter body copy is *two* elements, not one element with
   two paragraphs. Every skeleton below already does this.
2. **`elements` order is z-order.** Later elements paint on top. Card backgrounds come before the text
   that sits on them.
3. **Height is not elastic.** Text that does not fit spills outside its box and stays visible —
   `validate_presentation` reports it as an overflow warning. The heights below carry roughly 25%
   slack; if you add lines, add height.
4. **Slide `background` does not restyle text.** On a dark slide, give every text element an explicit
   `color`. The theme's `text` colour stays whatever the deck-level theme says.

Fills are solid only — no gradients, shadows, charts or SmartArt. Use 6 hex digits, no `#`.

## Palettes

Pick one for the whole deck. `accent` is a large-type and fill colour: use it at **24 pt and above**,
or as a shape fill — it is deliberately not contrasty enough for body copy.

| Name | Fits | background | primary | accent | text | muted |
|---|---|---|---|---|---|---|
| **Ink** | dark decks, exec readouts | `0F1E3D` | `FFFFFF` | `4DA3FF` | `E8EEF7` | `93A7C4` |
| **Paper** | general purpose, light | `FFFFFF` | `152C4E` | `D6483B` | `2B3440` | `6B7684` |
| **Forest** | sustainability, health, ops | `F6F9F4` | `1E4D2B` | `6FA95C` | `22302A` | `68786C` |
| **Clay** | culture, education, history | `FCF7F2` | `7E3428` | `CE7A2E` | `372B26` | `8B7A6F` |
| **Graphite** | engineering, data, infra | `FFFFFF` | `1F2933` | `0E9594` | `232B33` | `76828F` |

```json
"theme": {
  "background": "FFFFFF", "primary": "152C4E", "accent": "D6483B",
  "text": "2B3440", "muted": "6B7684",
  "font": "Arial", "eastAsiaFont": "Microsoft YaHei"
}
```

A light palette with dark cover and section slides reads better than a deck that is dark or light
throughout. Set `"background"` on those individual slides and give their text explicit colours.

## Skeletons

### Cover

Dark slide inside a light deck. Size contrast and whitespace carry it — no rules, bars or stripes.

```json
{
  "layout": "blank",
  "background": "0F1E3D",
  "elements": [
    { "type": "text", "text": "Presentation title", "x": 1.0, "y": 2.55, "width": 11.0, "height": 1.5,
      "valign": "bottom", "margin": 0, "font": { "size": 44, "bold": true, "color": "FFFFFF" } },
    { "type": "text", "text": "One line that says what this argues", "x": 1.0, "y": 4.2, "width": 9.5, "height": 0.7,
      "margin": 0, "font": { "size": 20, "color": "93A7C4" } },
    { "type": "text", "text": "Team · March 2026", "x": 1.0, "y": 6.45, "width": 11.0, "height": 0.4,
      "margin": 0, "font": { "size": 12, "color": "93A7C4" } }
  ]
}
```

### Section divider

The oversized number is the visual anchor; keep the label short.

```json
{
  "layout": "blank",
  "background": "0F1E3D",
  "elements": [
    { "type": "text", "text": "02", "x": 1.0, "y": 2.1, "width": 3.0, "height": 1.3,
      "margin": 0, "font": { "size": 68, "bold": true, "color": "4DA3FF" } },
    { "type": "text", "text": "What the data shows", "x": 1.0, "y": 3.5, "width": 11.0, "height": 1.0,
      "margin": 0, "font": { "size": 36, "bold": true, "color": "FFFFFF" } },
    { "type": "text", "text": "Three findings and what follows from them", "x": 1.0, "y": 4.6, "width": 9.0, "height": 0.6,
      "margin": 0, "font": { "size": 18, "color": "93A7C4" } }
  ]
}
```

### Split — copy left, image right

The workhorse content slide. Swap the sides between slides so the deck does not feel stamped out.

```json
{
  "layout": "blank",
  "elements": [
    { "type": "text", "text": "Section heading", "x": 0.8, "y": 0.7, "width": 11.7, "height": 0.85,
      "margin": 0, "font": { "size": 30, "bold": true, "color": "152C4E" } },
    { "type": "text", "x": 0.8, "y": 1.95, "width": 5.6, "height": 4.5, "margin": 0,
      "paragraphs": [
        { "text": "First point, stated as a claim", "bullet": true },
        { "text": "Supporting detail", "bullet": true, "level": 1 },
        { "text": "Second point", "bullet": true }
      ],
      "font": { "size": 18, "color": "2B3440" } },
    { "type": "image", "path": "figure.png", "x": 7.0, "y": 1.95, "width": 5.53, "height": 4.5,
      "altText": "Describe what the figure shows" }
  ]
}
```

### 2 × 2 cards

Background shape first, then its two text elements. Repeat the block four times at
`x` 0.8 / 6.9 and `y` 1.95 / 4.35.

```json
{
  "layout": "blank",
  "elements": [
    { "type": "text", "text": "Four things that changed", "x": 0.8, "y": 0.7, "width": 11.7, "height": 0.85,
      "margin": 0, "font": { "size": 30, "bold": true, "color": "152C4E" } },

    { "type": "shape", "shape": "roundRect", "x": 0.8, "y": 1.95, "width": 5.6, "height": 2.2,
      "fill": "F2F5F9", "line": "F2F5F9" },
    { "type": "text", "text": "Throughput", "x": 1.15, "y": 2.2, "width": 4.9, "height": 0.45,
      "margin": 0, "font": { "size": 20, "bold": true, "color": "152C4E" } },
    { "type": "text", "text": "Two sentences at most. Say the finding, not the methodology.",
      "x": 1.15, "y": 2.75, "width": 4.9, "height": 1.15,
      "margin": 0, "font": { "size": 14, "color": "2B3440" } },

    { "type": "shape", "shape": "roundRect", "x": 6.9, "y": 1.95, "width": 5.6, "height": 2.2,
      "fill": "F2F5F9", "line": "F2F5F9" },
    { "type": "text", "text": "Latency", "x": 7.25, "y": 2.2, "width": 4.9, "height": 0.45,
      "margin": 0, "font": { "size": 20, "bold": true, "color": "152C4E" } },
    { "type": "text", "text": "Second card body.", "x": 7.25, "y": 2.75, "width": 4.9, "height": 1.15,
      "margin": 0, "font": { "size": 14, "color": "2B3440" } }
  ]
}
```

### Stat row

Three numbers, evenly spaced. Numbers are `accent`; labels are `muted`; both centred over the same
column so they read as pairs.

```json
{
  "layout": "blank",
  "elements": [
    { "type": "text", "text": "The quarter in three numbers", "x": 0.8, "y": 0.7, "width": 11.7, "height": 0.85,
      "margin": 0, "font": { "size": 30, "bold": true, "color": "152C4E" } },

    { "type": "text", "text": "18%", "x": 0.8, "y": 2.3, "width": 3.5, "height": 1.45,
      "align": "center", "margin": 0, "font": { "size": 60, "bold": true, "color": "D6483B" } },
    { "type": "text", "text": "Revenue growth, year over year", "x": 0.8, "y": 3.85, "width": 3.5, "height": 0.9,
      "align": "center", "margin": 0, "font": { "size": 15, "color": "6B7684" } },

    { "type": "text", "text": "2.4×", "x": 4.9, "y": 2.3, "width": 3.5, "height": 1.45,
      "align": "center", "margin": 0, "font": { "size": 60, "bold": true, "color": "D6483B" } },
    { "type": "text", "text": "Enterprise pipeline coverage", "x": 4.9, "y": 3.85, "width": 3.5, "height": 0.9,
      "align": "center", "margin": 0, "font": { "size": 15, "color": "6B7684" } },

    { "type": "text", "text": "91", "x": 9.0, "y": 2.3, "width": 3.5, "height": 1.45,
      "align": "center", "margin": 0, "font": { "size": 60, "bold": true, "color": "D6483B" } },
    { "type": "text", "text": "Net promoter score", "x": 9.0, "y": 3.85, "width": 3.5, "height": 0.9,
      "align": "center", "margin": 0, "font": { "size": 15, "color": "6B7684" } },

    { "type": "text", "text": "Growth came from expansion, not new logos.",
      "x": 0.8, "y": 5.4, "width": 11.7, "height": 0.8,
      "margin": 0, "font": { "size": 18, "color": "2B3440" } }
  ]
}
```

### Numbered steps

Four `ellipse` shapes carry the numbers; labels sit underneath. The circle text is centred with
`align`/`valign` on the shape itself.

```json
{
  "layout": "blank",
  "elements": [
    { "type": "text", "text": "How it works", "x": 0.8, "y": 0.7, "width": 11.7, "height": 0.85,
      "margin": 0, "font": { "size": 30, "bold": true, "color": "152C4E" } },

    { "type": "shape", "shape": "ellipse", "x": 1.8, "y": 2.4, "width": 0.8, "height": 0.8,
      "fill": "152C4E", "line": "152C4E", "text": "1",
      "align": "center", "valign": "middle", "margin": 0, "font": { "size": 18, "bold": true, "color": "FFFFFF" } },
    { "type": "text", "text": "Collect", "x": 0.8, "y": 3.45, "width": 2.8, "height": 0.4,
      "align": "center", "margin": 0, "font": { "size": 17, "bold": true, "color": "152C4E" } },
    { "type": "text", "text": "What happens in this step", "x": 0.8, "y": 3.9, "width": 2.8, "height": 1.0,
      "align": "center", "margin": 0, "font": { "size": 13, "color": "6B7684" } },

    { "type": "shape", "shape": "ellipse", "x": 4.73, "y": 2.4, "width": 0.8, "height": 0.8,
      "fill": "152C4E", "line": "152C4E", "text": "2",
      "align": "center", "valign": "middle", "margin": 0, "font": { "size": 18, "bold": true, "color": "FFFFFF" } },
    { "type": "text", "text": "Normalise", "x": 3.73, "y": 3.45, "width": 2.8, "height": 0.4,
      "align": "center", "margin": 0, "font": { "size": 17, "bold": true, "color": "152C4E" } },
    { "type": "text", "text": "What happens in this step", "x": 3.73, "y": 3.9, "width": 2.8, "height": 1.0,
      "align": "center", "margin": 0, "font": { "size": 13, "color": "6B7684" } }
  ]
}
```

Remaining two columns continue the same pattern: circles at `x` 7.66 and 10.59, labels at `x` 6.66
and 9.59.

### Table slide

`header: true` makes row 0 bold on `headerFill`. Keep to about 6 columns and 8 rows at this size.

```json
{
  "layout": "blank",
  "elements": [
    { "type": "text", "text": "Option comparison", "x": 0.8, "y": 0.7, "width": 11.7, "height": 0.85,
      "margin": 0, "font": { "size": 30, "bold": true, "color": "152C4E" } },
    { "type": "table", "x": 0.8, "y": 1.95, "width": 11.7, "height": 3.5,
      "header": true, "headerFill": "152C4E", "bodyFill": "FFFFFF",
      "rows": [
        ["Option", "Cost", "Time to ship", "Risk"],
        ["Rebuild", "High", "2 quarters", "Medium"],
        ["Extend", "Low", "3 weeks", "Low"]
      ],
      "font": { "size": 14 } },
    { "type": "text", "text": "Source: internal estimate, March 2026",
      "x": 0.8, "y": 5.75, "width": 11.7, "height": 0.4,
      "margin": 0, "font": { "size": 12, "color": "6B7684" } }
  ]
}
```

## Assembling a deck

A 10–14 slide deck that holds together: **Cover → Section → 2–3 content slides → Section → 2–3 content
slides → Stat row or Table → closing Section with the ask.** Alternate Split and 2×2 between content
slides, and flip the Split's image side every other use.

Then run `validate_presentation`, fix overflow warnings by cutting words before shrinking type, and
look at every rendered slide.
