# `modify_system_file` 重构实施文档

> 目标：把当前残缺的 SEARCH/REPLACE 修改工具，重写为一个**准确、健壮、高效、可诊断**的文件编辑引擎。
> 影响文件：`Services/FileSystemService.cs`、`Models/FileOperationModels.cs`、`Services/Functions/FileSystemFunctions.cs`、`Services/Functions/FunctionRegistry.cs`、`Services/Interfaces/IFileSystemService.cs`。

---

## 1. 背景与现状问题

`modify_system_file` 是模型做局部编辑的主力工具，也是架构上刻意用来替代"全量覆盖"(`write_system_file`)的安全路径。但当前实现 ([FileSystemService.cs:419](../Services/FileSystemService.cs)) 是一个被注释标注为 *"Simplified"* / *"Fuzzy match omitted for brevity"* 的残缺版本，存在以下问题：

| # | 问题 | 位置 | 后果 |
|---|------|------|------|
| P1 | `fuzzyMatch` 参数被声明但**完全未实现**，永远走精确 `IndexOf` | `ApplyDiffBlock:421-428` | 缩进/空格/换行符的细微差异即失败，模型被迫回退到全量覆盖 |
| P2 | **CRLF 文件必然匹配失败**：SEARCH 被 `Join("\n")` 归一为 LF，原文是 CRLF | `ParseDiffBlocks:408,412` + `ModifyFileWithDiffAsync:179` | Windows 换行文件无法编辑 |
| P3 | `MultipleMatches` 冲突上下文从不被填充，是死代码 | `ApplyDiffBlock:425` vs `FileSystemFunctions:139` | 多处匹配时模型拿不到消歧信息，卡死 |
| P4 | 写入用 `Encoding.UTF8`，**每次注入 BOM** | `ModifyFileWithDiffAsync:193` | 反复修改污染无 BOM 文件（脚本、配置） |
| P5 | **非原子写入**，直接覆盖原文件 | `ModifyFileWithDiffAsync:193` | 写入中崩溃导致文件损坏/丢失 |
| P6 | 多块失败时错误信息不指明块序号 | `ModifyFileWithDiffAsync:185-190` | 模型难定位失败块 |
| P7 | 失败仅回 `"Search block not found"`，无近似位置提示 | `ApplyDiffBlock:429` | 模型只能盲目重试 |
| P8 | 空 SEARCH 块被静默丢弃 | `ParseDiffBlocks:413` | 模型不知道为何无效果 |

---

## 2. 设计目标

1. **准确度**：在容忍空白/缩进/换行噪声的同时，**严格控制歧义**——绝不在不确定时改错位置。
2. **完善性**：覆盖 CRLF/LF/BOM、多块、删除、批量替换、近似诊断等边界。
3. **效率**：单次读、单次原子写；行级匹配用首行索引把典型复杂度压到近似 O(N)。
4. **可诊断**：失败时给出可执行的反馈（最接近的位置 / 各候选位置 / 失败块号）。
5. **向后兼容**：保留 `fuzzyMatch` 语义，方法签名用可选参数扩展，不破坏现有调用方。

---

## 3. 架构总览

```
ModifyFileWithDiffAsync(path, diffContent, fuzzyMatch, replaceAll)
  │
  ├─ 1. 安全校验 (复用 ValidatePathAndSecurity)
  ├─ 2. 读原始字节 → 探测 { 编码, BOM, 主导EOL }            [FileEncodingProfile]
  ├─ 3. 解码并按 \n 规范化为 List<string> 行                 [内部规范空间]
  ├─ 4. ParseDiffBlocks → List<DiffBlock>（行级，保留缩进）
  ├─ 5. foreach block:                                       [DiffApplier]
  │       MatchBlock(lines, block, fuzzyMatch)
  │         Tier0 精确 → Tier1 行尾空格/EOL → Tier2 整行Trim(+重缩进)
  │         命中0 → 下一tier；全0 → 近似诊断失败
  │         命中1 → 应用；命中N → replaceAll? 全应用 : 歧义失败(填MultipleMatches)
  │       命中后对行列表做 splice（避免整文件重切分）
  ├─ 6. 重组：行列表 → 按原主导EOL拼接 → 按原编码/BOM编码为字节
  └─ 7. 原子写入：写临时文件 → File.Replace/Move 覆盖
```

核心拆成两个新内部组件，便于单测：
- **`FileEncodingProfile`**：探测并复刻文件的编码 / BOM / EOL 风格。
- **`DiffApplier`**：纯函数式的行级匹配 + 应用引擎（不碰 IO），输入 `List<string>` + blocks，输出新行列表或结构化失败。

---

## 4. 匹配引擎（核心）

### 4.1 分级降级策略（Tiered Matching）

每个 SEARCH 块按下列 tier **从严到松**尝试；**在第一个产生 ≥1 候选的 tier 停止**，再按候选数量决策。所有比较都在"按 `\n` 规范化的行空间"进行。

| Tier | 名称 | 比较规则 | 风险 | 是否需要 fuzzy |
|------|------|----------|------|----------------|
| 0 | Exact | 行内容完全相等 | 无 | 否（始终启用）|
| 1 | TrailingWs | 每行 `TrimEnd()` 后相等（容忍行尾空格 + EOL 差异）| 极低 | 是 |
| 2 | Trimmed | 每行 `Trim()` 后相等（容忍前导缩进差异），命中后**对 REPLACE 重缩进** | 中 | 是 |

> **不做 Tier 3（首尾锚点/模糊插值）**：会显著提升误匹配风险，与"绝不改错位置"目标冲突。如未来确有需要，应单独评审并要求命中唯一 + 用户确认。

`fuzzyMatch=false` 时只启用 Tier 0（精确）。

### 4.2 唯一性与歧义决策

在选定 tier 内收集**全部**候选起点：

- **0 个候选** → 进入下一 tier；全 tier 0 候选 → 走 §4.5 近似诊断失败。
- **1 个候选** → 应用替换。
- **N>1 个候选**：
  - `replaceAll=true` → 在该 tier 内对所有候选执行替换（从后往前应用，避免偏移失效）。
  - `replaceAll=false` → **歧义失败**，填充 `MultipleMatches`（见 §4.4），提示模型"增加上下文使 SEARCH 唯一，或设置 replaceAll=true"。

> 关键不变量：**只要某 tier 出现 >1 候选且未开 replaceAll，立即失败而非猜测**。这是准确度的根基。

### 4.3 首行索引优化（效率）

朴素行级匹配最坏 O(N_file × M_block)。优化：

1. 进入某 tier 前，按该 tier 的规范化函数为**文件首行**建倒排索引：`Dictionary<normalizedFirstLine, List<int>>`。
2. 只在 `block` 首行规范化命中的行号处，尝试完整逐行比较。
3. 典型情况下候选起点极少，整体接近 **O(N_file)**；索引随行列表 splice 后失效，按需对"剩余未处理块"懒重建（多块编辑通常 ≤ 几块，成本可忽略）。

### 4.4 重缩进算法（Tier 2 的关键，保证输出正确）

当在 Tier 2（整行 Trim）命中时，模型写的 REPLACE 缩进可能与文件实际缩进不一致。需把 REPLACE 重新对齐到文件实际缩进：

```
设：
  searchIndent  = SEARCH 块首行的前导空白
  matchedIndent = 文件中被匹配区域首行的前导空白
对 REPLACE 的每一行 line：
  若 line 以 searchIndent 为前缀 → 去掉该前缀，再补 matchedIndent
  否则（模型缩进风格不一致）→ 退化：去掉 line 自身前导空白中长度 = len(searchIndent) 的部分（按列宽，tab 记 1 列）后补 matchedIndent
空行（仅空白）保持为空行，不补缩进
```

这样即使模型整体缩进差了几格，落盘结果仍与周围代码对齐。Tier 0/1 命中时**不重缩进**（原样替换），因为此时缩进已逐字符匹配。

### 4.5 失败诊断（提升下一次成功率）

全 tier 未命中时，做一次有界的相似度扫描：

- 以 SEARCH 块的行数为窗口，在文件上滑动，计算每个窗口与 SEARCH 的**行级相似度**（规范化后 Jaccard/逐行相等比例，避免昂贵的全文 Levenshtein）。
- 取相似度最高且超过阈值（如 0.5）的窗口，返回提示：
  `未找到精确匹配。最接近的位置在第 {line} 行附近，文件实际内容为：\n{窗口原文(截断)}`
- 成本上界：窗口比较是 O(N_file × M_block × 常数)，仅在失败路径执行且 M_block 通常很小，可接受；可对超大文件（> 阈值行）跳过此诊断只回普通错误。

歧义失败时 `MultipleMatches` 每项格式：`第 {lineNumber} 行: {该行 Trim 后预览(截断80字符)}`，最多列前 5 个并标注总数。

---

## 5. 编码 / 换行 / 原子写入

### 5.1 `FileEncodingProfile` 探测

```csharp
internal sealed class FileEncodingProfile
{
    public bool HasUtf8Bom { get; init; }
    public string DominantEol { get; init; } = "\n"; // "\n" or "\r\n"

    public static FileEncodingProfile Detect(byte[] raw, string decodedText)
    {
        bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        int crlf = CountOccurrences(decodedText, "\r\n");
        int lfOnly = CountOccurrences(decodedText, "\n") - crlf;
        return new FileEncodingProfile
        {
            HasUtf8Bom = bom,
            DominantEol = crlf > lfOnly ? "\r\n" : "\n"
        };
    }
}
```

- 解码统一用 UTF-8（与现有读路径一致）；读时 `\r\n`/`\r` 先规范化为 `\n` 进入匹配空间。
- 写时：行列表用 `profile.DominantEol` 重新拼接；BOM 由 `new UTF8Encoding(profile.HasUtf8Bom)` 控制——**修复 P4**。

### 5.2 原子写入（修复 P5）

```csharp
private static async Task AtomicWriteAsync(string fullPath, byte[] bytes)
{
    var dir = Path.GetDirectoryName(fullPath)!;
    var tmp = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
    await File.WriteAllBytesAsync(tmp, bytes);
    // 优先 File.Replace（保留原文件 ACL/属性），跨卷或原文件缺失时退化为 Move
    try { File.Replace(tmp, fullPath, null); }
    catch (Exception) { File.Move(tmp, fullPath, overwrite: true); }
}
```

- 整个修改在内存完成后才落盘，任一块失败则不写（保持现有原子语义并强化为崩溃安全）。
- `WriteFileAsync` 也建议同步换成无 BOM 的 `UTF8Encoding(false)` + 原子写，保持一致（可选，列为附带项）。

---

## 6. 解析器加固（`ParseDiffBlocks`）

修复 P2/P8，并增强容错：

1. **行级解析**：按行扫描，识别 `<<<<<<< SEARCH` / `=======` / `>>>>>>> REPLACE` 三段；SEARCH 与 REPLACE 各自存为 `List<string>`（保留原始缩进，不再 `Join("\n").Trim()`）。
2. **EOL 无关**：解析前把 `diffContent` 的 `\r\n`/`\r` 统一为 `\n`。
3. **栅栏宽容**：标记行用 `TrimEnd()` 后 `StartsWith` 判断（容忍标记后多余空格），但要求行首即为标记（避免误吞代码里的 `=======`）。
4. **空 SEARCH 显式报错**（P8）：返回结构化错误"SEARCH 块为空；如需创建/追加文件请用 write_system_file"，而非静默丢弃。
5. **块完整性校验**：缺 `=======` 或 `>>>>>>> REPLACE` 时返回明确的"块 N 格式不完整"错误。

---

## 7. 数据模型与 API 变更

### 7.1 `Models/FileOperationModels.cs`

```csharp
public enum DiffMatchTier { None = 0, Exact, TrailingWhitespace, Trimmed }

public class FileUpdateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    internal string? ModifiedContent { get; set; }
    public int? LineNumber { get; set; }
    public List<string>? MultipleMatches { get; set; }
    public int AppliedBlocks { get; set; }

    // 新增
    public int? FailedBlockIndex { get; set; }      // 多块时定位失败块（1-based）
    public DiffMatchTier MatchTier { get; set; }    // 命中层级，便于日志/可观测
    public string? NearestHint { get; set; }        // 失败时的近似位置提示
}
```

### 7.2 `IFileSystemService`

```csharp
Task<FileUpdateResult> ModifyFileWithDiffAsync(
    string absolutePath, string diffContent, bool fuzzyMatch = true, bool replaceAll = false);
```

### 7.3 `FileSystemFunctions.ModifySystemFileAsync`

```csharp
public async Task<FunctionResult> ModifySystemFileAsync(
    string path, string diffContent, bool fuzzyMatch = true, bool replaceAll = false)
{
    // ...参数校验同现状...
    var result = await _fileSystemService.ModifyFileWithDiffAsync(path, diffContent, fuzzyMatch, replaceAll);
    if (result.Success)
    {
        await TryUpdateKnowledgeBaseVectorsAsync(path);
        return FunctionResult.SuccessResult(result.Message,
            new { path, appliedBlocks = result.AppliedBlocks, matchTier = result.MatchTier.ToString() });
    }

    var sb = new StringBuilder(result.Message);
    if (result.FailedBlockIndex is int bi) sb.Append($"\n失败块: #{bi}");
    if (!string.IsNullOrEmpty(result.NearestHint)) sb.Append($"\n{result.NearestHint}");
    if (result.MultipleMatches is { Count: > 0 } mm)
        sb.Append("\n冲突上下文（请补充上下文使 SEARCH 唯一，或设置 replaceAll=true）:\n")
          .Append(string.Join("\n", mm.Select(m => $"- {m}")));
    return FunctionResult.FailureResult(sb.ToString());
}
```

### 7.4 工具 schema/描述（`FunctionRegistry.cs`）

```csharp
RegisterFunction("modify_system_file", fileSystemFunctions.ModifySystemFileAsync,
    "Modifies an existing file using one or more SEARCH/REPLACE blocks. The SEARCH block must uniquely "
  + "identify the target. Whitespace, indentation and line-ending (LF/CRLF) differences are tolerated by "
  + "default. To delete code, leave the REPLACE side empty. If a SEARCH block matches multiple locations, "
  + "the edit fails and reports each location—add surrounding context to disambiguate, or set replaceAll=true "
  + "to change every occurrence. For creating a new file or replacing entire content, use write_system_file instead.",
    new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Path to the file." },
            diffContent = new { type = "string", description =
                "One or more blocks in the format:\n<<<<<<< SEARCH\n<existing lines>\n=======\n<new lines>\n>>>>>>> REPLACE" },
            fuzzyMatch = new { type = "boolean", description =
                "Tolerate whitespace/indentation/line-ending differences in the SEARCH block. Defaults to true. "
              + "Set false to require a byte-exact match.", @default = true },
            replaceAll = new { type = "boolean", description =
                "Replace every occurrence of the SEARCH block instead of requiring a unique match. Defaults to false.",
                @default = false }
        },
        required = new[] { "path", "diffContent" }
    });
```

> 注意：`replaceAll` 形参名为 camelCase，已被上一轮的 `Canonicalize` 参数绑定改造覆盖，模型即便输出 `replace_all` 也能正确绑定。

---

## 8. 核心代码骨架（`DiffApplier`）

```csharp
internal static class DiffApplier
{
    public static FileUpdateResult Apply(
        List<string> lines, IReadOnlyList<DiffBlock> blocks, bool fuzzy, bool replaceAll)
    {
        int applied = 0;
        DiffMatchTier bestTier = DiffMatchTier.None;

        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            var match = FindMatches(lines, block.SearchLines, fuzzy);

            if (match.Tier == DiffMatchTier.None)
                return Fail(b + 1, "未找到匹配的 SEARCH 块。", NearestHint(lines, block.SearchLines));

            if (match.Starts.Count > 1 && !replaceAll)
                return Ambiguous(b + 1, lines, match);

            // 从后往前替换，保证前面的起点偏移不失效
            foreach (var start in match.Starts.OrderByDescending(s => s))
            {
                var replacement = match.Tier == DiffMatchTier.Trimmed
                    ? Reindent(block.ReplaceLines, block.SearchLines[0], lines[start])
                    : block.ReplaceLines;
                lines.RemoveRange(start, block.SearchLines.Count);
                lines.InsertRange(start, replacement);
            }
            applied++;
            bestTier = (DiffMatchTier)Math.Max((int)bestTier, (int)match.Tier);
        }

        return new FileUpdateResult
        {
            Success = true, AppliedBlocks = applied, MatchTier = bestTier,
            Message = $"已应用 {applied} 个修改块"
        };
    }

    // FindMatches: 依次 Tier0/1/2，用首行倒排索引收集候选，返回首个非空 tier 的全部起点
    // Reindent:    §4.4 重缩进
    // NearestHint: §4.5 相似度扫描
    // Ambiguous:   填充 MultipleMatches（每项 "第 N 行: <预览>"）
}
```

`DiffBlock` 改为行级：

```csharp
internal sealed class DiffBlock
{
    public List<string> SearchLines { get; set; } = new();
    public List<string> ReplaceLines { get; set; } = new();
}
```

---

## 9. 边界用例对照表

| 场景 | 期望行为 |
|------|----------|
| SEARCH 与文件仅行尾空格不同 | Tier 1 命中，原样替换 |
| SEARCH 整体缩进比文件少 2 空格 | Tier 2 命中 + 重缩进，落盘缩进正确 |
| CRLF 文件 + LF 的 SEARCH | 规范化后命中，落盘保持 CRLF |
| 文件带 BOM | 落盘保留 BOM；无 BOM 文件落盘不新增 BOM |
| SEARCH 在文件中出现 3 次，replaceAll=false | 失败，列出 3 处行号 + 预览 |
| 同上 replaceAll=true | 全部替换，从后往前应用 |
| REPLACE 为空 | 删除匹配区域 |
| SEARCH 为空 | 显式报错，引导用 write_system_file |
| 多块，其中第 2 块未命中 | 整体失败、不落盘、报 `失败块 #2` + 近似提示 |
| 完全找不到 | 失败 + "最接近第 N 行" 提示 |
| fuzzyMatch=false 且有空格差异 | 失败（严格精确语义）|

---

## 10. 测试计划

在 `Athena.Archive.Tests`（或新增 `FileSystemServiceTests`）中，针对**纯函数 `DiffApplier` / `ParseDiffBlocks` / `FileEncodingProfile`** 做单测（无需触盘）：

1. 三级匹配各自命中与降级路径。
2. 重缩进：tab/空格混合、负缩进、空行保持。
3. CRLF/LF/BOM 往返保真。
4. 多候选歧义 + replaceAll 两条分支。
5. 删除（空 REPLACE）、空 SEARCH 报错。
6. 多块原子性：中途失败不改变行列表。
7. 首行索引优化与朴素实现结果一致性（随机化对拍）。
8. 近似诊断在典型"差一点"输入上返回正确行号。

集成层：对 `ModifyFileWithDiffAsync` 做一次端到端（建临时文件→修改→断言内容/编码/EOL），验证原子写与安全校验仍生效。

---

## 11. 实施分期

- **Phase 1（正确性，必做）**：行级解析(P2/P8) + Tier 0/1 匹配(P1) + MultipleMatches 填充(P3) + 无 BOM 原子写(P4/P5) + 失败块号(P6)。这一期即可让工具从"经常失败"变为"可靠"。
- **Phase 2（准确度提升）**：Tier 2 + 重缩进(§4.4) + replaceAll。
- **Phase 3（诊断体验）**：近似位置提示(§4.5) + MatchTier 可观测日志。
- 每期独立可发布、可回归。

---

## 12. 风险与规避

| 风险 | 规避 |
|------|------|
| Tier 2 误匹配改错位置 | 严格唯一性闸门：>1 候选即失败；重缩进只在唯一命中后执行 |
| 大文件相似度诊断耗时 | 设行数上界，超限跳过诊断只回普通错误 |
| 原子写跨卷失败 | `File.Replace` 失败回退 `File.Move(overwrite)` |
| 行列表 splice 后索引失效 | 每块独立重新匹配；从后往前应用多候选 |
| 与现有调用方不兼容 | 新参数全为可选；`FileUpdateResult` 仅新增字段 |
| 改动较大引入回归 | 核心逻辑纯函数化 + 对拍单测；分期上线 |

---

## 附：与现有体系的衔接点

- 安全：继续走 `ValidatePathAndSecurity(fullPath, isWriteOperation:true, dataSize: newBytes.Length)`，在落盘前用最终字节数校验写配额。
- 知识库向量同步：成功后由 `FileSystemFunctions` 触发 `TryUpdateKnowledgeBaseVectorsAsync`（现状已有，不变）。
- 参数绑定：`replaceAll` 等 camelCase 形参依赖已落地的 `FunctionRegistry.Canonicalize`，兼容模型的 snake_case 输出。
