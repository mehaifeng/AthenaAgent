using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Athena.UI.Services;

/// <summary>
/// 单个 SEARCH/REPLACE 块。SEARCH 是一段字面文本，可以是若干完整行，也可以是某一行内的片段。
/// </summary>
internal sealed class DiffBlock
{
    public string SearchText { get; set; } = string.Empty;
    public string ReplaceText { get; set; } = string.Empty;
}

/// <summary>
/// 解析 diffContent 的结果。
/// </summary>
internal sealed class DiffParseResult
{
    public List<DiffBlock> Blocks { get; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// 探测并复刻文件的编码特征（BOM / 主导换行风格），用于修改后保真落盘。
/// </summary>
internal sealed class FileEncodingProfile
{
    public bool HasUtf8Bom { get; init; }
    public string DominantEol { get; init; } = "\n"; // "\n" 或 "\r\n"

    public static FileEncodingProfile Detect(byte[] raw, string decodedText)
    {
        bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        int crlf = CountOccurrences(decodedText, "\r\n");
        int totalLf = decodedText.Count(c => c == '\n');
        int lfOnly = totalLf - crlf;
        return new FileEncodingProfile
        {
            HasUtf8Bom = bom,
            DominantEol = crlf > lfOnly ? "\r\n" : "\n"
        };
    }

    private static int CountOccurrences(string s, string sub)
    {
        int count = 0, idx = 0;
        while ((idx = s.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += sub.Length;
        }
        return count;
    }
}

/// <summary>
/// 纯函数式的 SEARCH/REPLACE 编辑引擎：解析、分级匹配、按字符跨度替换。不接触 IO，便于单测。
///
/// 匹配单位是「字符跨度」而不是「整行」：整行编辑只是其中起止恰好落在行边界的特例。
/// 这样机器生成的单行文件（压缩 JSON、minified 资源）才可编辑——那类文件整个文件就是一行，
/// 按行匹配意味着必须一字不差复述上万字符，实际不可完成。
///
/// 分级顺序（命中即停）：
///   1. Exact              整行对齐 + 逐字符相等
///   2. TrailingWhitespace 整行对齐 + 容忍行尾空白
///   3. Trimmed            整行对齐 + 容忍首尾空白（命中后对 REPLACE 重缩进）
///   4. Span               任意位置的逐字符相等（行内片段）
/// 前三层保持与行时代完全一致的语义；Span 只在前三层全部落空时兜底，且拒绝「整行仅差首尾空白」
/// 的候选——那类目标属于第 2/3 层，让它落到 Span 会把整行编辑悄悄降级成半行编辑。
/// </summary>
internal static class DiffApplier
{
    private const string SearchStart = "<<<<<<< SEARCH";
    private const string Separator = "=======";
    private const string ReplaceEnd = ">>>>>>> REPLACE";

    private const int MaxMatchPreviews = 5;
    private const int RegionContextLines = 2;   // 行预览中落点前后的上下文行数
    private const int RegionMaxInsertedLines = 25;
    private const int RegionMaxLineLength = 200; // 行预览单行最长截断长度
    private const int LongLineThreshold = 400;   // 超过此长度的行改用字符窗口预览
    private const int RegionCharContext = 40;    // 字符窗口预览的前后上下文字符数
    private const int RegionMaxInsertedChars = 200;
    private const int DiagnosticSampleChars = 40;

    #region 解析

    /// <summary>
    /// 将 diffContent 解析为 SEARCH/REPLACE 块。换行符无关；遇到结构错误时返回带 Error 的结果。
    /// 标记行按「Trim 后完全相等」识别——早期实现用的是 StartsWith，会让文件里任何以 7 个及以上
    /// 等号开头的行（markdown setext 标题下划线、纯等号分隔条）冒充分隔符，把 SEARCH 提前截断，
    /// 最终静默写坏文件却报告成功。
    /// </summary>
    public static DiffParseResult Parse(string diffContent)
    {
        var result = new DiffParseResult();
        if (string.IsNullOrEmpty(diffContent))
        {
            result.Error = "diffContent 为空。";
            return result;
        }

        var lines = diffContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        int i = 0, blockNo = 0;

        while (i < lines.Length)
        {
            if (!IsMarker(lines[i], SearchStart)) { i++; continue; }
            blockNo++;
            i++; // 跳过 SEARCH 标记

            var search = new List<string>();
            while (i < lines.Length && !IsMarker(lines[i], Separator))
                search.Add(lines[i++]);
            if (i >= lines.Length)
            {
                result.Error = $"块 #{blockNo} 格式不完整：缺少分隔符 '======='。";
                return result;
            }
            i++; // 跳过分隔符

            var replace = new List<string>();
            while (i < lines.Length && !IsMarker(lines[i], ReplaceEnd))
                replace.Add(lines[i++]);
            if (i >= lines.Length)
            {
                result.Error = $"块 #{blockNo} 格式不完整：缺少结束标记 '>>>>>>> REPLACE'。";
                return result;
            }
            i++; // 跳过 REPLACE 结束标记

            TrimFenceArtifacts(search);
            TrimFenceArtifacts(replace);

            if (search.Count == 0)
            {
                result.Error = $"块 #{blockNo} 的 SEARCH 内容为空；如需创建或追加文件，请改用 write_system_file。";
                return result;
            }

            result.Blocks.Add(new DiffBlock
            {
                SearchText = string.Join("\n", search),
                ReplaceText = string.Join("\n", replace)
            });
        }

        if (result.Blocks.Count == 0)
            result.Error = "未找到有效的 SEARCH/REPLACE 块。";

        return result;
    }

    private static bool IsMarker(string line, string marker) =>
        line.AsSpan().Trim().SequenceEqual(marker.AsSpan());

    /// <summary>移除栅栏排版产生的单层首/尾空行。</summary>
    private static void TrimFenceArtifacts(List<string> list)
    {
        if (list.Count > 0 && list[0].Length == 0) list.RemoveAt(0);
        if (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
    }

    #endregion

    #region 应用

    /// <summary>
    /// 依次应用所有块。全有全无：任一块失败即返回结构化错误，且不产出任何内容。
    /// 成功时新内容放在 <see cref="FileUpdateResult.ModifiedContent"/>（换行一律为 \n，由调用方还原风格）。
    /// </summary>
    public static FileUpdateResult Apply(string text, IReadOnlyList<DiffBlock> blocks, bool fuzzy, bool replaceAll)
    {
        string working = text;
        int applied = 0;
        var bestTier = DiffMatchTier.None;

        // 记录每个块的替换事件（起点、长度差）与最小起点处的最终插入文本，成功后据此生成落点预览。
        var blockEvents = new List<List<(int Start, int Delta)>>(blocks.Count);
        var firstStart = new List<int>(blocks.Count);
        var firstInserted = new List<string>(blocks.Count);
        var occurrenceCounts = new List<int>(blocks.Count);

        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            var match = FindMatches(working, block.SearchText, fuzzy);

            if (match.Tier == DiffMatchTier.None)
            {
                return new FileUpdateResult
                {
                    Success = false,
                    FailedBlockIndex = b + 1,
                    Message = $"块 #{b + 1}: 未找到匹配的 SEARCH 内容。",
                    NearestHint = DivergenceHint(working, block.SearchText)
                };
            }

            if (match.Spans.Count > 1 && !replaceAll)
            {
                return new FileUpdateResult
                {
                    Success = false,
                    FailedBlockIndex = b + 1,
                    MatchTier = match.Tier,
                    Message = $"块 #{b + 1}: SEARCH 内容匹配到 {match.Spans.Count} 处，无法确定唯一目标。",
                    MultipleMatches = BuildMatchPreviews(working, match.Spans)
                };
            }

            // 从后往前应用，保证较小起点的偏移不因前面的增删而失效。
            var events = new List<(int Start, int Delta)>(match.Spans.Count);
            int minStart = int.MaxValue;
            string? smallestReplacement = null;

            // 单跨度是绝对多数情况，直接三段拼接：只产生一份结果字符串。
            // 走 StringBuilder 会先复制一遍原文再 ToString 复制一遍，在大文件上白白翻倍。
            if (match.Spans.Count == 1)
            {
                var (start, length, replacement) = PrepareSpan(working, match.Spans[0], block, match.Tier);
                working = string.Concat(working.AsSpan(0, start), replacement, working.AsSpan(start + length));
                events.Add((start, replacement.Length - length));
                minStart = start;
                smallestReplacement = replacement;
            }
            else
            {
                var sb = new StringBuilder(working, working.Length + block.ReplaceText.Length * match.Spans.Count);
                foreach (var span in match.Spans.OrderByDescending(s => s.Start))
                {
                    var (start, length, replacement) = PrepareSpan(working, span, block, match.Tier);
                    sb.Remove(start, length).Insert(start, replacement);
                    events.Add((start, replacement.Length - length));
                    if (start < minStart) { minStart = start; smallestReplacement = replacement; }
                }
                working = sb.ToString();
            }
            blockEvents.Add(events);
            firstStart.Add(minStart);
            firstInserted.Add(smallestReplacement ?? string.Empty);
            occurrenceCounts.Add(match.Spans.Count);

            applied++;
            if ((int)match.Tier > (int)bestTier) bestTier = match.Tier;
        }

        // 把每个块的最小落点换算到最终文本的偏移：后应用的块若落点 ≤ 当前累计位置，其长度差就会平移该位置。
        var finalPos = new int[blocks.Count];
        for (int k = 0; k < blocks.Count; k++)
        {
            int pos = firstStart[k];
            for (int j = k + 1; j < blocks.Count; j++)
                foreach (var (s, d) in blockEvents[j])
                    if (s <= pos) pos += d;
            finalPos[k] = pos;
        }

        return new FileUpdateResult
        {
            Success = true,
            AppliedBlocks = applied,
            MatchTier = bestTier,
            ModifiedContent = working,
            Message = $"已成功应用 {applied} 个修改块。",
            RegionPreview = BuildRegionPreview(working, finalPos, firstInserted, occurrenceCounts)
        };
    }

    /// <summary>
    /// 解析出一处命中的实际替换参数：Trimmed 层需要重缩进；空 REPLACE 且整行命中时要连同行尾换行
    /// 一并删除，否则会留下一个空行（行时代 RemoveRange 的语义，必须保持）。
    /// </summary>
    private static (int Start, int Length, string Replacement) PrepareSpan(
        string text, (int Start, int Length) span, DiffBlock block, DiffMatchTier tier)
    {
        string replacement = tier == DiffMatchTier.Trimmed
            ? Reindent(block.ReplaceText, block.SearchText, text, span.Start)
            : block.ReplaceText;

        int start = span.Start, length = span.Length;
        if (replacement.Length == 0 && IsLineAligned(text, start, length))
        {
            if (start + length < text.Length && text[start + length] == '\n') length++;
            else if (start > 0 && text[start - 1] == '\n') { start--; length++; }
        }
        return (start, length, replacement);
    }

    #endregion

    #region 匹配

    private sealed class MatchResult
    {
        public DiffMatchTier Tier;
        public List<(int Start, int Length)> Spans = new();
    }

    private enum NormMode { None, TrimEnd, Trim }

    private static MatchResult FindMatches(string text, string search, bool fuzzy)
    {
        if (search.Length == 0) return new MatchResult { Tier = DiffMatchTier.None };

        var textStarts = BuildLineStarts(text);
        var searchStarts = BuildLineStarts(search);

        var exact = ScanAligned(text, textStarts, search, searchStarts, NormMode.None);
        if (exact.Count > 0) return new MatchResult { Tier = DiffMatchTier.Exact, Spans = exact };

        if (fuzzy)
        {
            var trailing = ScanAligned(text, textStarts, search, searchStarts, NormMode.TrimEnd);
            if (trailing.Count > 0) return new MatchResult { Tier = DiffMatchTier.TrailingWhitespace, Spans = trailing };

            var trimmed = ScanAligned(text, textStarts, search, searchStarts, NormMode.Trim);
            if (trimmed.Count > 0) return new MatchResult { Tier = DiffMatchTier.Trimmed, Spans = trimmed };
        }

        var span = ScanSpan(text, search);
        if (span.Count > 0) return new MatchResult { Tier = DiffMatchTier.Span, Spans = span };

        return new MatchResult { Tier = DiffMatchTier.None };
    }

    /// <summary>
    /// 整行对齐扫描：逐行比较，命中后返回覆盖这些整行的字符跨度。以首行短路，典型复杂度接近 O(N)。
    /// 全程用 <see cref="ReadOnlySpan{T}"/> 比较，不产生任何中间字符串。
    /// </summary>
    private static List<(int Start, int Length)> ScanAligned(
        string text, int[] textStarts, string search, int[] searchStarts, NormMode mode)
    {
        var spans = new List<(int, int)>();
        int m = searchStarts.Length, n = textStarts.Length;
        if (m == 0 || m > n) return spans;

        for (int i = 0; i + m <= n; i++)
        {
            bool ok = true;
            for (int j = 0; j < m; j++)
            {
                if (!Normalize(LineSpan(text, textStarts, i + j), mode)
                        .SequenceEqual(Normalize(LineSpan(search, searchStarts, j), mode)))
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                int start = textStarts[i];
                int end = LineEnd(text, textStarts, i + m - 1);
                spans.Add((start, end - start));
                i += m - 1; // 非重叠
            }
        }
        return spans;
    }

    /// <summary>
    /// 任意位置的逐字符扫描（行内片段兜底）。会剔除「整行仅差首尾空白」的候选：那属于前两层的
    /// 职责，落到这里会把整行编辑悄悄降级成半行编辑，留下孤立的缩进或行尾空白。
    /// </summary>
    private static List<(int Start, int Length)> ScanSpan(string text, string search)
    {
        var spans = new List<(int, int)>();
        int idx = 0;
        while ((idx = text.IndexOf(search, idx, StringComparison.Ordinal)) >= 0)
        {
            if (!IsWholeLineModuloWhitespace(text, idx, search.Length))
                spans.Add((idx, search.Length));
            idx += search.Length;
        }
        return spans;
    }

    private static ReadOnlySpan<char> Normalize(ReadOnlySpan<char> s, NormMode mode) => mode switch
    {
        NormMode.TrimEnd => s.TrimEnd(),
        NormMode.Trim => s.Trim(),
        _ => s
    };

    private static int[] BuildLineStarts(string text)
    {
        var list = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') list.Add(i + 1);
        return list.ToArray();
    }

    private static ReadOnlySpan<char> LineSpan(string s, int[] starts, int i)
    {
        int start = starts[i];
        return s.AsSpan(start, LineEnd(s, starts, i) - start);
    }

    private static int LineEnd(string s, int[] starts, int i) =>
        i + 1 < starts.Length ? starts[i + 1] - 1 : s.Length;

    private static bool IsLineAligned(string text, int start, int length) =>
        (start == 0 || text[start - 1] == '\n') &&
        (start + length == text.Length || text[start + length] == '\n');

    /// <summary>跨度两侧到所在行首/行尾之间是否只剩空白（即该跨度实质上就是整行）。</summary>
    private static bool IsWholeLineModuloWhitespace(string text, int start, int length)
    {
        for (int i = start - 1; i >= 0 && text[i] != '\n'; i--)
            if (!char.IsWhiteSpace(text[i])) return false;

        for (int i = start + length; i < text.Length && text[i] != '\n'; i++)
            if (!char.IsWhiteSpace(text[i])) return false;

        return true;
    }

    #endregion

    #region 重缩进

    /// <summary>
    /// Trimmed 层命中后，把 REPLACE 重新对齐到文件实际缩进，避免落盘缩进错乱。
    /// </summary>
    private static string Reindent(string replaceText, string searchText, string text, int matchStart)
    {
        string searchIndent = LeadingWhitespaceAt(searchText, 0);
        string matchedIndent = LeadingWhitespaceAt(text, matchStart);
        if (searchIndent == matchedIndent) return replaceText;
        if (replaceText.Length == 0) return replaceText;

        var lines = replaceText.Split('\n');
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.AsSpan().Trim().Length == 0) { result.Add(string.Empty); continue; } // 空行保持为空

            if (searchIndent.Length > 0 && line.StartsWith(searchIndent, StringComparison.Ordinal))
            {
                result.Add(matchedIndent + line.Substring(searchIndent.Length));
            }
            else
            {
                // 退化：剥离与 searchIndent 等长的前导空白后补上文件缩进。
                int strip = Math.Min(LeadingWhitespaceAt(line, 0).Length, searchIndent.Length);
                result.Add(matchedIndent + line.Substring(strip));
            }
        }
        return string.Join("\n", result);
    }

    private static string LeadingWhitespaceAt(string s, int from)
    {
        int i = from;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return s.Substring(from, i - from);
    }

    #endregion

    #region 诊断

    /// <summary>
    /// 未命中时给出可执行的修正线索：二分求出 SEARCH 能够定位到的最长前缀，报告它的位置以及
    /// 第一个分歧字符处「期望什么 / 实际是什么」。行时代的实现按整行相似度打分，单行 SEARCH
    /// 结构上永远拿不到提示（Trim 相等的行早在 Trimmed 层就命中了），模型只能盲猜。
    /// </summary>
    private static string? DivergenceHint(string text, string search)
    {
        if (search.Length == 0 || text.Length == 0) return null;

        // SEARCH 整体存在，却没有进入任何一层 → 只可能是被「整行仅差首尾空白」护栏挡下的严格模式。
        int wholeIdx = text.IndexOf(search, StringComparison.Ordinal);
        if (wholeIdx >= 0)
        {
            var (wl, wc) = Locate(text, wholeIdx);
            return $"SEARCH 文本出现在第 {wl} 行第 {wc} 列，但与该行之间只差首尾空白。" +
                   "请设置 fuzzyMatch=true，或把该行的空白原样补齐后重试。";
        }

        int lo = 0, hi = search.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (text.AsSpan().IndexOf(search.AsSpan(0, mid), StringComparison.Ordinal) >= 0) lo = mid;
            else hi = mid - 1;
        }

        if (lo == 0)
            return $"SEARCH 的第 1 个字符 \"{Escape(search.AsSpan(0, 1))}\" 就不在文件中，" +
                   "请先用 read_system_file 或 search_in_file 重新取一段真实文本作为 SEARCH。";

        int first = text.AsSpan().IndexOf(search.AsSpan(0, lo), StringComparison.Ordinal);
        int prefixOccurrences = CountOccurrences(text, search.Substring(0, lo));
        var (line, col) = Locate(text, first);

        var expected = search.AsSpan(lo, Math.Min(DiagnosticSampleChars, search.Length - lo));
        int actualLen = Math.Max(0, Math.Min(DiagnosticSampleChars, text.Length - (first + lo)));
        var actual = actualLen > 0 ? text.AsSpan(first + lo, actualLen) : ReadOnlySpan<char>.Empty;

        var sb = new StringBuilder();
        sb.Append($"SEARCH 的前 {lo} 个字符可以定位到第 {line} 行第 {col} 列");
        if (prefixOccurrences > 1) sb.Append($"（该前缀在文件中共 {prefixOccurrences} 处，此处为首处）");
        sb.Append($"；从第 {lo + 1} 个字符起分歧:\n");
        sb.Append($"  SEARCH 期望: \"{Escape(expected)}\"\n");
        sb.Append($"  文件实际是: \"{Escape(actual)}\"");
        return sb.ToString();
    }

    private static List<string> BuildMatchPreviews(string text, List<(int Start, int Length)> spans)
    {
        var list = new List<string>();
        foreach (var span in spans.Take(MaxMatchPreviews))
        {
            var (line, col) = Locate(text, span.Start);
            int from = Math.Max(0, span.Start - RegionCharContext);
            int to = Math.Min(text.Length, span.Start + span.Length + RegionCharContext);
            var context = Escape(text.AsSpan(from, to - from));
            list.Add($"第 {line} 行第 {col} 列: {(from > 0 ? "…" : "")}{context}{(to < text.Length ? "…" : "")}");
        }
        if (spans.Count > MaxMatchPreviews)
            list.Add($"... 共 {spans.Count} 处");
        return list;
    }

    private static (int Line, int Col) Locate(string text, int offset)
    {
        int line = 1, lineStart = 0;
        int limit = Math.Min(offset, text.Length);
        for (int i = 0; i < limit; i++)
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        return (line, offset - lineStart + 1);
    }

    /// <summary>已有行起点表时按二分定位，避免对大文件做 O(offset) 的线性扫描。</summary>
    private static (int Line, int Col) Locate(int[] starts, int offset)
    {
        int idx = Array.BinarySearch(starts, offset);
        if (idx < 0) idx = Math.Max(0, ~idx - 1);
        return (idx + 1, offset - starts[idx] + 1);
    }

    private static int CountOccurrences(string text, string sub)
    {
        if (sub.Length == 0) return 0;
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0) { count++; idx += sub.Length; }
        return count;
    }

    private static string Escape(ReadOnlySpan<char> s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    #endregion

    #region 落点预览

    /// <summary>
    /// 为每个成功应用的块生成落点预览。短行走行预览（"N | 内容"，与 read_system_file 同格式）；
    /// 落在超长行内的编辑走字符窗口——在单行 9600 字符的文件上，「前后各两行」等于把整个文件吐回去。
    /// </summary>
    private static string? BuildRegionPreview(string finalText, int[] finalPos, List<string> inserted, List<int> occurrenceCounts)
    {
        if (finalPos.Length == 0) return null;
        var starts = BuildLineStarts(finalText);
        var sb = new StringBuilder();

        for (int b = 0; b < finalPos.Length; b++)
        {
            int pos = Math.Clamp(finalPos[b], 0, finalText.Length);
            string ins = inserted[b];
            var (line, col) = Locate(starts, pos);

            if (b > 0) sb.Append('\n');
            sb.Append(occurrenceCounts[b] > 1
                ? $"块 #{b + 1} 修改后区域（第 {line} 行第 {col} 列起，共替换 {occurrenceCounts[b]} 处）:\n"
                : $"块 #{b + 1} 修改后区域（第 {line} 行第 {col} 列起）:\n");

            int lineIdx = line - 1;
            bool longLine = LineEnd(finalText, starts, Math.Min(lineIdx, starts.Length - 1)) - starts[Math.Min(lineIdx, starts.Length - 1)] > LongLineThreshold;

            if (col == 1 && !longLine) AppendLinePreview(sb, finalText, starts, lineIdx, ins);
            else AppendCharPreview(sb, finalText, pos, ins);
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static void AppendLinePreview(StringBuilder sb, string text, int[] starts, int lineIdx, string ins)
    {
        var insLines = ins.Length == 0 ? Array.Empty<string>() : ins.Split('\n');

        for (int i = Math.Max(0, lineIdx - RegionContextLines); i < lineIdx; i++)
            sb.Append($"{i + 1} | {Clip(LineSpan(text, starts, i))}\n");

        int shown = Math.Min(insLines.Length, RegionMaxInsertedLines);
        for (int i = 0; i < shown; i++)
            sb.Append($"{lineIdx + 1 + i} | {Clip(insLines[i].AsSpan())}\n");
        if (insLines.Length > RegionMaxInsertedLines)
            sb.Append($"…（该处替换共 {insLines.Length} 行，仅显示前 {RegionMaxInsertedLines} 行）\n");

        int afterStart = lineIdx + insLines.Length;
        int afterEnd = Math.Min(starts.Length, afterStart + RegionContextLines);
        for (int i = afterStart; i < afterEnd; i++)
            sb.Append($"{i + 1} | {Clip(LineSpan(text, starts, i))}\n");
    }

    private static void AppendCharPreview(StringBuilder sb, string text, int pos, string ins)
    {
        int insLen = Math.Min(ins.Length, Math.Max(0, text.Length - pos));
        int from = Math.Max(0, pos - RegionCharContext);
        int to = Math.Min(text.Length, pos + insLen + RegionCharContext);

        if (from > 0) sb.Append('…');
        sb.Append(Escape(text.AsSpan(from, pos - from)));
        sb.Append('⟦').Append(Escape(ClipSpan(ins.AsSpan(), RegionMaxInsertedChars)));
        if (ins.Length > RegionMaxInsertedChars) sb.Append($"…（该处共替换入 {ins.Length} 字符）");
        sb.Append('⟧');
        sb.Append(Escape(text.AsSpan(pos + insLen, to - pos - insLen)));
        if (to < text.Length) sb.Append('…');
        sb.Append('\n');
    }

    private static string Clip(ReadOnlySpan<char> s) =>
        s.Length <= RegionMaxLineLength ? s.ToString() : string.Concat(s.Slice(0, RegionMaxLineLength).ToString(), "…");

    private static ReadOnlySpan<char> ClipSpan(ReadOnlySpan<char> s, int max) =>
        s.Length <= max ? s : s.Slice(0, max);

    #endregion
}
