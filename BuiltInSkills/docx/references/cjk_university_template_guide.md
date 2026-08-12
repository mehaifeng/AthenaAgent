# Chinese university thesis template guide (中国高校论文模板指南)

Chinese thesis templates often differ from Western templates in style IDs, front matter, page-number regimes, fonts, and section structure. Inspect the actual package; do not assume `Heading1`, `Heading2`, and `Normal`.

## Identify style IDs

Run:

```text
$CLI analyze --input template.docx --json
```

For a complete style map, inspect `word/styles.xml` and record `w:styleId`, `w:name`, `w:type`, `w:basedOn`, and `w:outlineLvl`.

Common patterns include:

| Purpose | Possible style ID | Common display name |
|---|---|---|
| Body | `a`, `Normal`, custom | Normal / 正文 |
| Heading 1 | `1`, `Heading1`, custom | heading 1 / 章标题 |
| Heading 2 | `2`, `Heading2`, custom | heading 2 / 节标题 |
| Heading 3 | `3`, `Heading3`, custom | heading 3 / 小节标题 |
| TOC levels | `11`, `21`, `31`, `TOC1`... | toc 1, toc 2... |
| Header/footer | `a3`, `a4`, `Header`, `Footer` | header / footer |

`w:styleId` is the reference used by `w:pStyle`; `w:name` is only the display name. Match exact IDs first, then normalized names within the same style type, then use an explicit mapping.

Heading styles must carry `w:outlineLvl` (0 for level 1, 1 for level 2, and so on) if they are expected to appear in Word navigation and TOCs. The bundled `analyze` command recognizes outline-based headings even when IDs are numeric.

## Typical thesis zones

A university template may contain:

1. 封面 (cover)
2. 原创性声明 / 学术诚信声明
3. 中文摘要与关键词
4. English abstract and keywords
5. 目录 (TOC)
6. 正文 (body chapters)
7. 参考文献
8. 致谢
9. 附录

Many of these zones use different sections, headers/footers, page-number formats, or restart values. A common pattern is no number on the cover, Roman numerals for front matter, and Arabic numbers restarted at 1 for the body. Treat every `w:sectPr` as meaningful.

## Use Base-Replace for structural templates

If the template contains the zones above, use Scenario C Base-Replace rather than Overlay. Add unique marker paragraphs to a working copy around only the example body content:

```text
[[ATHENA_BODY_START]]
示例正文（将被替换）
[[ATHENA_BODY_END]]
```

Then run:

```text
$CLI apply-template --input thesis-content.docx --template marked-template.docx --output thesis.docx \
  --mode base-replace --start-marker "[[ATHENA_BODY_START]]" --end-marker "[[ATHENA_BODY_END]]"
```

Markers must each occur once and must not cross a section boundary. Keep the template's cover, declarations, TOC, references/back matter, final section properties, headers, footers, fields, and page-number regimes.

## CJK font rules

Set both Western and East Asian font attributes when CJK text is involved:

```xml
<w:rFonts w:ascii="Times New Roman"
          w:hAnsi="Times New Roman"
          w:eastAsia="宋体"
          w:cs="Times New Roman"/>
```

Common Chinese typefaces include 宋体/SimSun for body text, 黑体/SimHei for headings, 楷体/KaiTi and 仿宋/FangSong for specific institutional uses. The university's own template is authoritative; do not substitute fonts merely because they are more common.

Chinese size names map to half-points as follows:

| Chinese size | Points | `w:sz` |
|---|---:|---:|
| 小二 | 18 | 36 |
| 三号 | 16 | 32 |
| 小三 | 15 | 30 |
| 四号 | 14 | 28 |
| 小四 | 12 | 24 |
| 五号 | 10.5 | 21 |

Preserve `w:eastAsia`, language, and character spacing when cleaning direct formatting. Check font availability before rendering; fallback can change line and page breaks.

## Section and layout traps

- Copy `titlePg` when a section has a distinct first-page header/footer.
- Keep `oddPage` chapter starts when required by binding or university rules.
- Preserve `pgNumType` format and start values.
- Do not replace section breaks with blank paragraphs.
- A temporary two-column section needs an explicit transition into and out of columns.
- Keep the final body-level `w:sectPr` last in `w:body`.
- TOC fields require outline-aware heading styles; visible bold text is not enough.

## Validation checklist

After applying the template:

```text
$CLI validate --input thesis.docx --xsd "<skill-root>/assets/xsd/wml-subset.xsd"
$CLI validate --input thesis.docx --business
$CLI validate --input thesis.docx --gate-check marked-template.docx
$CLI diff --before thesis-content.docx --after thesis.docx
```

Render and inspect every page. Verify cover fidelity, declaration pages, abstract/TOC transitions, Roman-to-Arabic numbering, odd-page chapter starts, headers/footers, CJK glyphs, line wrapping, tables, references, and that body content appears exactly once.
