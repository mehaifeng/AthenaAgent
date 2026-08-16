# Athena 原生 PPTX 工具链实施设计

> 状态：实施基线（代码、工具注册、内置 Skill 与测试必须与本文同步）  
> 目标运行时：.NET 10 / C# / `System.IO.Compression` / LINQ to XML / SkiaSharp  
> 设计原则：零 Office、LibreOffice、Python、Node 或 Open XML SDK 运行时依赖；原文件永不原位修改。

## 1. 背景与结论

现有 docx、xlsx Skill 已由进程内 C# OOXML 服务支撑，而 pptx Skill 仍依赖 Python 脚本、XSD、Node、LibreOffice 和 Poppler。PPTX 的 OPC/ZIP 包管理并不比 XLSX 复杂，但 PresentationML 的版式继承、主题解析和文本排版明显更难。因此实现不能以“完整复刻 PowerPoint”为目标，而应提供边界清楚、可验证、可渐进扩展的原生工具面。

本次交付的完成定义是：

1. `inspect_presentation`：读取幻灯片顺序、尺寸、每页标题/文字/备注、形状边界、版式和特性摘要。
2. `create_presentation`：从受限 JSON 规格生成可独立打开的 `.pptx`，支持常用版式、文本、项目符号、基础形状、图片和表格。
3. `edit_presentation`：跨 DrawingML run 查找替换，按形状写文本，添加文本框，以及复制、删除、移动、重排幻灯片；始终输出副本。
4. `validate_presentation`：解析全部 XML，检查关系、内容类型、幻灯片清单、形状 ID、核心子元素顺序和常见图表损坏模式，并用 SkiaSharp 做保守的文本溢出估算。
5. 原生函数注册、依赖注入、工具 JSON Schema、内置 Skill 路由和自动化往返测试全部接通。
6. pptx Skill 的运行路径不再引用旧 Python/XSD/LibreOffice/Node 脚本链；旧脚本资产从分发源移除。

“完成”不表示实现任意 PowerPoint 功能。宏、OLE、SmartArt、动画、任意图表编辑和 PowerPoint 等价排版属于明确非目标；编辑时保留其未触及部件，验证时报告风险。

## 2. 参考实现与许可证边界

- [ShapeCrawler](https://github.com/ShapeCrawler/ShapeCrawler)（MIT）：.NET 高层对象模型、形状/占位符/图表行为的主要语义参考。
- [Open XML SDK](https://github.com/dotnet/Open-XML-SDK)（MIT）：ISO 29500 对应的强类型元素模型、子元素顺序和包关系的交叉验证来源。
- [python-pptx](https://github.com/scanny/python-pptx)（MIT）：PresentationML 最小部件、打包与文本结构的补充参考。
- [PptxGenJS](https://github.com/gitbrent/PptxGenJS)（MIT）：生成端最小可用 XML、图表常见兼容性问题的参考。
- [ECMA-376](https://ecma-international.org/publications-and-standards/standards/ecma-376/)：OOXML 权威规范，PresentationML 位于 Part 1。

本实现不引入或复制上述项目源码，只基于公开格式和行为编写 Athena 自有实现。若未来移植代码，必须先保留原版权声明并更新 `Docs/ThirdPartyNotices.md`。

## 3. 范围与非目标

### 3.1 支持格式

| 操作 | 输入 | 输出 |
|---|---|---|
| Inspect / Validate | `.pptx`、`.pptm`、`.potx`、`.potm` | 结构化 JSON 结果 |
| Edit | 上述任一格式 | 与输入扩展名相同的新文件 |
| Create | 无 | `.pptx` |

旧二进制 `.ppt` 不在范围内。工具必须给出可行动错误，不自动调用转换器。

### 3.2 创建能力

- 16:9、4:3 或自定义英寸画布。
- `title`、`section`、`titleAndContent`、`blank` 四种模板化构图。
- 标题、副标题、正文、项目符号及 0–8 级缩进。
- 自定义文本框、矩形、圆角矩形、椭圆、直线。
- PNG、JPEG、GIF、BMP 图片嵌入。
- 原生 DrawingML 表格。
- 主题色、默认拉丁/东亚字体、背景色。

### 3.3 编辑能力

- 跨 `<a:r>/<a:t>` 边界查找替换，保留命中起始 run 的格式。
- 按幻灯片 + 形状 ID/名称重写文本；按页设置标题。
- 添加直接格式化文本框。
- 幻灯片复制、删除、移动、全量重排。
- 未触及的 ZIP 部件逐字节保留。

复制幻灯片时，图片、图表、SmartArt 等关联部件保持共享引用。这样能安全复制复杂页面，但随后直接修改共享图表数据会同时影响源页和副本；本工具不提供图表数据编辑，因此结果中必须显式报告该行为。

### 3.4 明确非目标

- 完整的五跳样式继承编辑器。
- 与 PowerPoint 完全一致的断行、自动缩放和字体替换。
- 动画、切换、SmartArt、OLE、视频、3D、墨迹和任意扩展元素的创建/编辑。
- 原生图表设计器或任意图表数据改写。
- PDF/图片渲染器。真实视觉 QA 使用应用现有 PPTX 预览能力或用户本机 PowerPoint；验证工具只给出 `visualValidationRequired`。

## 4. 架构

```text
PresentationFunctions
  ├─ IFileSystemService              路径沙箱、读写配额
  └─ PptxPackageService
       ├─ OoxmlPackageService        ZIP 守卫、rels、原子写入
       ├─ DrawingTextEditor          跨 run 文本编辑
       ├─ PresentationElementBuilder 创建形状/图片/表格 XML
       └─ PresentationTextLayoutAnalyzer (SkiaSharp)
                                      保守溢出估算
```

代码按职责拆分：

- `Services/Presentations/PresentationSchema.cs`：命名空间、关系类型、内容类型和基础单位。
- `Services/Presentations/DrawingTextEditor.cs`：DrawingML 段落文本操作。
- `Services/Presentations/PptxPackageService.cs`：打开、检查、公共模型与辅助函数。
- `Services/Presentations/PptxPackageService.Create.cs`：新包与页面元素生成。
- `Services/Presentations/PptxPackageService.Edit.cs`：副本编辑和结构操作。
- `Services/Presentations/PptxPackageService.Validate.cs`：静态验证与布局告警。
- `Services/Presentations/PresentationTextLayoutAnalyzer.cs`：字体测量。
- `Services/Functions/PresentationFunctions.cs`：安全路径与工具结果封装。

## 5. 工具协议

### 5.1 `inspect_presentation`

参数：

```json
{
  "path": "deck.pptx",
  "startSlide": 1,
  "maxSlides": 40,
  "includeShapes": true,
  "includeNotes": true
}
```

返回：画布尺寸、总页数、母版/版式摘要、窗口内每页的标题、文本、备注、形状列表、图表/图片/表格数量、宏/动画/SmartArt 等特性，以及下一窗口位置。页码为 1-based；形状编辑使用 inspect 返回的数值 ID 或名称。

### 5.2 `create_presentation`

参数：`outputPath`、`presentationJson`、`overwrite=false`。

最小规格：

```json
{
  "layout": "wide",
  "theme": {
    "background": "FFFFFF",
    "primary": "203864",
    "accent": "F05A28",
    "font": "Arial",
    "eastAsiaFont": "Microsoft YaHei"
  },
  "slides": [
    { "layout": "title", "title": "标题", "subtitle": "副标题" },
    {
      "layout": "titleAndContent",
      "title": "结论",
      "bullets": ["第一点", { "text": "第二级", "level": 1 }],
      "elements": [
        { "type": "image", "path": "figure.png", "x": 7.2, "y": 1.5, "width": 5.2, "height": 4.8 }
      ]
    }
  ]
}
```

坐标和尺寸单位为英寸，字体/线宽为 point。颜色只接受 6/8 位十六进制且不能带 `#`。创建最多 500 页、每页最多 2,000 个元素；JSON 深度、图片大小和包大小仍受共享 OOXML 守卫限制。

通用元素：

- `text`：`text` 或 `paragraphs[]`，`x/y/width/height`，`font`、`align`、`valign`、`margin`、`fill`、`line`。
- `shape`：`shape=rect|roundRect|ellipse|line`，可带与文本元素相同的文字属性。
- `image`：`path`、边界、`altText`。
- `table`：`rows[][]`、边界、`header=true|false`，可设字体、表头色和正文色。

### 5.3 `edit_presentation`

参数：`inputPath`、不同的 `outputPath`、`operationsJson`、`overwrite=false`。

结构操作必须排在文本操作之前；所有页码按操作执行时的当前顺序解释：

```json
[
  { "duplicateSlide": 2, "after": 3 },
  { "moveSlide": 4, "to": 2 },
  { "deleteSlide": 6 },
  { "reorder": [3, 1, 2, 4, 5] },
  { "find": "旧词", "replace": "新词", "all": true, "slide": 2 },
  { "slide": 2, "shapeId": 7, "setText": "完整替换" },
  { "slide": 2, "setTitle": "新标题" },
  { "slide": 2, "addTextBox": { "text": "补充", "x": 1, "y": 6, "width": 4, "height": 0.5 } }
]
```

`reorder` 必须恰好包含当前全部页码且不重复。删除最后一页被拒绝。编辑完成后返回各类变更计数和共享复杂部件告警。

### 5.4 `validate_presentation`

返回 `valid`、`errors[]`、`warnings[]`、`overflowWarnings[]`、`features[]`、`dynamicValidationPerformed=false`、`visualValidationRequired=true`。

`valid` 只表示下列静态不变量成立：

- 必需部件存在且全部 XML 可安全解析。
- 所有内部 relationship 均能解析到存在的规范化目标。
- `[Content_Types].xml` 覆盖存在且引用的部件存在。
- `p:sldIdLst` 的 ID/rId 唯一，rId 指向 slide，slide 目标不重复。
- 每页具有合法核心骨架，非可视形状 ID 唯一。
- 核心容器子元素未违反已知 PresentationML 顺序。
- 图表轴引用闭合；堆叠柱/条形图没有已知会损坏 PowerPoint 的 `outEnd` 数据标签位置。

溢出是 warning，不影响 `valid`。SkiaSharp 分析直接格式、常用占位符默认值、边距、换行和 `normAutofit` 缩放；遇到主题字体、组变换、路径文字或复杂继承时标为“不确定”，不会给出确定无溢出的结论。

## 6. OOXML 包策略

### 6.1 创建的最小部件

- `[Content_Types].xml`、`_rels/.rels`
- `docProps/core.xml`、`docProps/app.xml`
- `ppt/presentation.xml`、`ppt/_rels/presentation.xml.rels`
- `ppt/presProps.xml`、`ppt/viewProps.xml`、`ppt/tableStyles.xml`
- `ppt/theme/theme1.xml`
- `ppt/slideMasters/slideMaster1.xml` 及 rels
- `ppt/slideLayouts/slideLayout1.xml` 及 rels
- `ppt/slides/slideN.xml` 及 rels
- 按需的 `ppt/media/*`

生成器只使用一个空白 layout；标题与正文都是页面上的直接形状，避免生成后依赖多级占位符继承。模板化是构图模板，不是伪造多套 master/layout。

### 6.2 编辑策略

1. 用共享 ZIP 守卫只读打开并做最低完整性检查。
2. 验证输入/输出扩展名相同，输出不能等于输入。
3. 原子写临时文件，在临时文件中用 `ZipArchiveMode.Update` 修改。
4. 只重写被修改的 XML/relationships/content-types；其他 entry 保持不变。
5. 临时文件关闭成功后再移动到输出路径。

删除页时删除 slide 与其 `.rels`，同时删除 presentation relationship 和 content-type override；关联资源不做激进垃圾回收，因为可能被其他页或扩展关系间接共享。验证器报告孤立复杂部件但不据此判 invalid。

### 6.3 安全

- 继承 `OoxmlPackageService` 的 ZIP slip、条目数、单部件大小、总解压大小和压缩比守卫。
- XML 禁止 DTD 和外部解析器。
- 外部 relationship 不下载、不解析，只作为特性报告。
- 所有工具路径先经过 `IFileSystemService`；写入受配额检查。
- JSON 限制深度、数组数、字符串长度；数值做有限值和范围校验。
- 不执行宏、嵌入对象、字段、动作或超链接。

## 7. 文本与版式策略

### 7.1 跨 run 编辑

同一 `<a:p>` 中可见 `<a:t>` 拼接成逻辑字符串，查找命中从后向前应用，避免偏移漂移。替换文字写入命中起始 run，后续被覆盖 run 只移除对应片段；空 `<a:t>` 和仅剩属性的空 run 被清理。首尾空白使用 `xml:space="preserve"`。

### 7.2 设置完整文本

保留 `a:bodyPr`、`a:lstStyle`、第一段的 `a:pPr` 和第一 run 的 `a:rPr`，只重建段落内容。换行输入按 `\n` 分为多个 `<a:p>`，不把项目符号字符写进文本。

### 7.3 溢出估算

将 EMU 转为 point，扣除文本框内边距，用 SkiaSharp 按实际/回退字体测量 token。CJK 按字符提供断行机会，拉丁文本优先按空白断行。行高默认 `1.2 × 字号`，并读取直接字号、段后距、`fontScale` 和 `lnSpcReduction`。下列情况降级为不确定：

- 组形状坐标变换；
- 旋转、竖排、弯曲/路径文字；
- 主题字体引用和未安装字体替代；
- 表格合并单元格、复杂段前段后间距；
- 版式/母版提供但页面未直接给出的 run 属性。

因此验证报告必须提示：静态估算不能代替逐页渲染。

## 8. 测试与验收

新增独立、无测试框架依赖的 `Athena.Presentation.Tests` 控制台测试项目，进入解决方案构建。测试必须覆盖：

1. 创建含 title/content/blank、基础形状、表格的多页 deck。
2. `validate` 返回 valid，包中所有 presentation relationship 和 content type 闭合。
3. `inspect` 的页数、标题、正文、表格/图片计数正确。
4. 跨两个 `<a:r>` 的文本替换成功且未破坏未触及格式。
5. 复制、移动、删除、重排得到预期顺序和唯一 slide ID/rId。
6. 源文件哈希在编辑后不变。
7. 非法扩展、同路径编辑、缺失部件、断裂 relationship、重复形状 ID 被拒绝或报告错误。
8. 明显超出文本框的文字产生 overflow warning；适配文本框不误报为确定错误。
9. 工具 JSON Schema 与委托参数完全匹配（由现有注册器断言覆盖）。

验收命令：

```bash
dotnet build
dotnet run --project Athena.Presentation.Tests -p:UseAppHost=false
Scripts/run-headless-tests.sh
```

如果本机有正在运行的 Athena 导致 apphost 锁定，遵循仓库规则用 `-p:UseAppHost=false` 构建测试目标。视觉验收另外用应用现有 PPTX 预览器逐页检查创建与编辑样本，不把 LibreOffice 重新引入 Skill 依赖。

## 9. 交付顺序与完成门

| 阶段 | 交付物 | 完成门 |
|---|---|---|
| A | Inspect + Validate + 包模型 | 能读取真实 deck；损坏关系能定位到 part |
| B | Edit + DrawingTextEditor | 往返编辑源文件哈希不变；结构操作闭合 |
| C | Create + 元素构建器 | 自建 deck 可再 inspect/validate/edit |
| D | Skia 溢出估算 + 图表规则 | 明显溢出与两类常见图表损坏有测试 |
| E | Functions/DI/Skill/清理 | 模型只看到原生工作流，不再要求外部工具 |
| F | 全量构建、独立测试、headless | 全绿；文档状态矩阵与代码一致 |

任一阶段不满足完成门时，目标保持 active，不以“已搭骨架”作为完成。

## 10. 后续扩展点

- 解析 `shape → layout placeholder → master → theme` 的有效字体/颜色，并把置信度写入 inspect。
- 在应用预览 WebView 上增加逐页截图 API，形成真正的原生视觉验证闭环。
- 图表采用模板骨架 + 数据缓存同步，而不是构建完整 chart DOM。
- 支持备注创建、图片裁剪、组形状绝对坐标展开和表格行列编辑。
- 从 ECMA XSD/SDK 元数据生成 PresentationML child-order 表，并在 CI 中验证生成结果未漂移。
