using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Athena.UI.Services;

/// <summary>
/// 单个 SEARCH/REPLACE 块（行级，保留缩进）。
/// </summary>
internal sealed class DiffBlock
{
    public List<string> SearchLines { get; set; } = new();
    public List<string> ReplaceLines { get; set; } = new();
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
/// 纯函数式的 SEARCH/REPLACE 编辑引擎：负责解析、分级匹配与应用，不接触 IO，便于单测。
/// </summary>
internal static class DiffApplier
{
    private const string SearchStart = "<<<<<<< SEARCH";
    private const string Separator = "=======";
    private const string ReplaceEnd = ">>>>>>> REPLACE";

    private const int NearestHintMaxLines = 20000; // 超大文件跳过近似诊断
    private const int MaxMatchPreviews = 5;
    private const int RegionContextLines = 2; // 区域预览中落点前后的上下文行数
    private const int RegionMaxInsertedLines = 25; // 单块替换内容最多展示的行数
    private const int RegionMaxLineLength = 200; // 预览单行最长截断长度

    #region 解析

    /// <summary>
    /// 将 diffContent 解析为行级 SEARCH/REPLACE 块。换行符无关；遇到结构错误时返回带 Error 的结果。
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
            if (!lines[i].Trim().StartsWith(SearchStart, StringComparison.Ordinal)) { i++; continue; }
            blockNo++;
            i++; // 跳过 SEARCH 标记

            var search = new List<string>();
            while (i < lines.Length && !lines[i].Trim().StartsWith(Separator, StringComparison.Ordinal))
                search.Add(lines[i++]);
            if (i >= lines.Length)
            {
                result.Error = $"块 #{blockNo} 格式不完整：缺少分隔符 '======='。";
                return result;
            }
            i++; // 跳过分隔符

            var replace = new List<string>();
            while (i < lines.Length && !lines[i].Trim().StartsWith(ReplaceEnd, StringComparison.Ordinal))
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

            result.Blocks.Add(new DiffBlock { SearchLines = search, ReplaceLines = replace });
        }

        if (result.Blocks.Count == 0)
            result.Error = "未找到有效的 SEARCH/REPLACE 块。";

        return result;
    }

    /// <summary>移除栅栏排版产生的单层首/尾空行。</summary>
    private static void TrimFenceArtifacts(List<string> list)
    {
        if (list.Count > 0 && list[0].Length == 0) list.RemoveAt(0);
        if (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
    }

    #endregion

    #region 应用

    /// <summary>
    /// 将一组块就地应用到 <paramref name="lines"/>（仅在全部成功时修改）。任一块失败即返回结构化错误且不改动入参。
    /// </summary>
    public static FileUpdateResult Apply(List<string> lines, IReadOnlyList<DiffBlock> blocks, bool fuzzy, bool replaceAll)
    {
        // 先在副本上演算，确保多块原子性（中途失败不留下半成品）。
        var working = new List<string>(lines);
        int applied = 0;
        var bestTier = DiffMatchTier.None;

        // 记录每个块的替换事件（起点、行数差）与最小起点处的最终插入行，成功后据此生成区域预览。
        var blockEvents = new List<List<(int Start, int Delta)>>(blocks.Count);
        var firstStart = new List<int>(blocks.Count);
        var firstInserted = new List<List<string>>(blocks.Count);

        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            var match = FindMatches(working, block.SearchLines, fuzzy);

            if (match.Tier == DiffMatchTier.None)
            {
                return new FileUpdateResult
                {
                    Success = false,
                    FailedBlockIndex = b + 1,
                    Message = $"块 #{b + 1}: 未找到匹配的 SEARCH 内容。",
                    NearestHint = NearestHint(working, block.SearchLines)
                };
            }

            if (match.Starts.Count > 1 && !replaceAll)
            {
                return new FileUpdateResult
                {
                    Success = false,
                    FailedBlockIndex = b + 1,
                    MatchTier = match.Tier,
                    Message = $"块 #{b + 1}: SEARCH 内容匹配到 {match.Starts.Count} 处，无法确定唯一目标。",
                    MultipleMatches = BuildMatchPreviews(working, match.Starts)
                };
            }

            // 从后往前应用，保证较小起点的索引不因前面的增删而失效。
            var events = new List<(int Start, int Delta)>(match.Starts.Count);
            int minStart = int.MaxValue;
            List<string>? smallestReplacement = null;
            foreach (var start in match.Starts.OrderByDescending(s => s))
            {
                var replacement = match.Tier == DiffMatchTier.Trimmed
                    ? Reindent(block.ReplaceLines, block.SearchLines[0], working[start])
                    : block.ReplaceLines;
                working.RemoveRange(start, block.SearchLines.Count);
                working.InsertRange(start, replacement);
                events.Add((start, replacement.Count - block.SearchLines.Count));
                if (start < minStart) { minStart = start; smallestReplacement = replacement; }
            }
            blockEvents.Add(events);
            firstStart.Add(minStart);
            firstInserted.Add(smallestReplacement!);

            applied++;
            if ((int)match.Tier > (int)bestTier) bestTier = match.Tier;
        }

        // 全部成功，提交回入参。
        lines.Clear();
        lines.AddRange(working);

        // 计算每个块最小起点在最终行空间中的位置：后应用的块若落点 ≤ 当前累计位置，
        // 其行数差就会平移该位置。插入/删除保持相对顺序不变，因此逐块累加即得最终位置，
        // 即使块的落点顺序与文件顺序不一致也成立。
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
            Message = $"已成功应用 {applied} 个修改块。",
            RegionPreview = BuildRegionPreview(working, finalPos, firstInserted, blockEvents.Select(e => e.Count).ToList())
        };
    }

    #endregion

    #region 匹配

    private sealed class MatchResult
    {
        public DiffMatchTier Tier;
        public List<int> Starts = new();
    }

    /// <summary>分级降级匹配：精确 → 行尾空格容错 → 整行 Trim；在首个产生候选的层级停止。</summary>
    private static MatchResult FindMatches(List<string> lines, List<string> search, bool fuzzy)
    {
        var exact = ScanTier(lines, search, NormalizeExact);
        if (exact.Count > 0) return new MatchResult { Tier = DiffMatchTier.Exact, Starts = exact };
        if (!fuzzy) return new MatchResult { Tier = DiffMatchTier.None };

        var trailing = ScanTier(lines, search, NormalizeTrimEnd);
        if (trailing.Count > 0) return new MatchResult { Tier = DiffMatchTier.TrailingWhitespace, Starts = trailing };

        var trimmed = ScanTier(lines, search, NormalizeTrim);
        if (trimmed.Count > 0) return new MatchResult { Tier = DiffMatchTier.Trimmed, Starts = trimmed };

        return new MatchResult { Tier = DiffMatchTier.None };
    }

    /// <summary>
    /// 在给定规范化函数下扫描所有非重叠匹配起点。以首行短路，典型复杂度接近 O(N)。
    /// </summary>
    private static List<int> ScanTier(List<string> lines, List<string> search, Func<string, string> norm)
    {
        var starts = new List<int>();
        int m = search.Count;
        if (m == 0 || m > lines.Count) return starts;

        var normSearch = new string[m];
        for (int k = 0; k < m; k++) normSearch[k] = norm(search[k]);
        string firstKey = normSearch[0];

        for (int i = 0; i + m <= lines.Count; i++)
        {
            if (norm(lines[i]) != firstKey) continue;
            bool ok = true;
            for (int j = 1; j < m; j++)
            {
                if (norm(lines[i + j]) != normSearch[j]) { ok = false; break; }
            }
            if (ok)
            {
                starts.Add(i);
                i += m - 1; // 非重叠
            }
        }
        return starts;
    }

    private static string NormalizeExact(string s) => s;
    private static string NormalizeTrimEnd(string s) => s.TrimEnd();
    private static string NormalizeTrim(string s) => s.Trim();

    #endregion

    #region 重缩进与诊断

    /// <summary>
    /// Tier 2 命中后，将 REPLACE 重新对齐到文件实际缩进，避免落盘缩进错乱。
    /// </summary>
    private static List<string> Reindent(List<string> replace, string searchFirst, string matchedFirst)
    {
        string searchIndent = LeadingWhitespace(searchFirst);
        string matchedIndent = LeadingWhitespace(matchedFirst);
        if (searchIndent == matchedIndent) return replace;

        var result = new List<string>(replace.Count);
        foreach (var line in replace)
        {
            if (line.Trim().Length == 0) { result.Add(string.Empty); continue; } // 空行保持为空

            if (searchIndent.Length > 0 && line.StartsWith(searchIndent, StringComparison.Ordinal))
            {
                result.Add(matchedIndent + line.Substring(searchIndent.Length));
            }
            else
            {
                // 退化：剥离与 searchIndent 等长的前导空白后补上文件缩进。
                int strip = Math.Min(LeadingWhitespace(line).Length, searchIndent.Length);
                result.Add(matchedIndent + line.Substring(strip));
            }
        }
        return result;
    }

    private static string LeadingWhitespace(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return s.Substring(0, i);
    }

    private static List<string> BuildMatchPreviews(List<string> lines, List<int> starts)
    {
        var list = new List<string>();
        foreach (var s in starts.Take(MaxMatchPreviews))
            list.Add($"第 {s + 1} 行: {Preview(lines[s])}");
        if (starts.Count > MaxMatchPreviews)
            list.Add($"... 共 {starts.Count} 处");
        return list;
    }

    private static string Preview(string line)
    {
        var t = line.Trim();
        return t.Length <= 80 ? t : t.Substring(0, 80) + "…";
    }

    /// <summary>
    /// 为每个成功应用的块生成「落点区域预览」：上下文 + 新内容 + 上下文，行号前缀与
    /// read_system_file 的格式一致（"N | 内容"），便于模型对照读取结果自检编辑位置。
    /// </summary>
    private static string? BuildRegionPreview(List<string> finalLines, int[] finalPos, List<List<string>> inserted, List<int> occurrenceCounts)
    {
        if (finalPos.Length == 0) return null;
        var sb = new StringBuilder();
        for (int b = 0; b < finalPos.Length; b++)
        {
            int pos = finalPos[b];
            int lineNo = pos + 1;
            var lines = inserted[b];
            int count = occurrenceCounts[b];

            if (b > 0) sb.Append('\n');
            sb.Append(count > 1
                ? $"块 #{b + 1} 修改后区域（第 {lineNo} 行起，共替换 {count} 处）:\n"
                : $"块 #{b + 1} 修改后区域（第 {lineNo} 行起）:\n");

            for (int i = Math.Max(0, pos - RegionContextLines); i < pos; i++)
                sb.Append($"{i + 1} | {Clip(finalLines[i])}\n");

            int shown = Math.Min(lines.Count, RegionMaxInsertedLines);
            for (int i = 0; i < shown; i++)
                sb.Append($"{lineNo + i} | {Clip(lines[i])}\n");
            if (lines.Count > RegionMaxInsertedLines)
                sb.Append($"…（该处替换共 {lines.Count} 行，仅显示前 {RegionMaxInsertedLines} 行）\n");

            int afterEnd = Math.Min(finalLines.Count, pos + lines.Count + RegionContextLines);
            for (int i = pos + lines.Count; i < afterEnd; i++)
                sb.Append($"{i + 1} | {Clip(finalLines[i])}\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string Clip(string s) => s.Length <= RegionMaxLineLength ? s : s.Substring(0, RegionMaxLineLength) + "…";

    /// <summary>
    /// 未命中时，做一次有界的行级相似度扫描，返回最接近窗口的位置与实际内容。
    /// </summary>
    private static string? NearestHint(List<string> lines, List<string> search)
    {
        int m = search.Count;
        if (m == 0 || lines.Count == 0 || lines.Count > NearestHintMaxLines) return null;

        var normSearch = new string[m];
        for (int k = 0; k < m; k++) normSearch[k] = search[k].Trim();

        int bestStart = -1;
        double bestScore = 0;
        for (int i = 0; i + m <= lines.Count; i++)
        {
            int eq = 0;
            for (int j = 0; j < m; j++)
                if (lines[i + j].Trim() == normSearch[j]) eq++;
            double score = (double)eq / m;
            if (score > bestScore) { bestScore = score; bestStart = i; }
        }

        if (bestStart < 0 || bestScore < 0.5) return null;

        var sb = new StringBuilder($"最接近的位置在第 {bestStart + 1} 行附近，文件实际内容为:\n");
        for (int j = 0; j < m && bestStart + j < lines.Count; j++)
            sb.Append(lines[bestStart + j]).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    #endregion
}
