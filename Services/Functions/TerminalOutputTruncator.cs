using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Athena.UI.Services.Functions;

/// <summary>
/// 终端输出的智能压缩。与「硬截断」不同：先去掉低信息量内容（ANSI 颜色码、连续空行、
/// 重复行/重复块），再对剩余文本做首尾保留式截断，所有省略都显式标注，让模型知道自己
/// 看到的是不完整摘要、需要缩小范围重试，而不是盲目重复同一命令。
/// </summary>
public static partial class TerminalOutputTruncator
{
    // ANSI CSI 转义序列（\x1b[ 参数区 + 结尾字节），覆盖颜色/光标控制等。
    [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiSequenceRegex();

    public readonly record struct ProcessResult(string Text, int OmittedChars, int CollapsedLines);

    /// <summary>
    /// 处理一段终端输出：换行归一化 → 清理 ANSI → 折叠空行 → 折叠重复行/块 → 首尾保留式截断。
    /// </summary>
    public static ProcessResult Process(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return new ProcessResult(string.Empty, 0, 0);
        if (maxChars <= 0)
            return new ProcessResult(string.Empty, text.Length, 0);

        // 换行归一化（PowerShell 的 CRLF 与 Unix 的 LF 统一），便于行级折叠与统计。
        var cleaned = text.Replace("\r\n", "\n").Replace('\r', '\n');
        cleaned = AnsiSequenceRegex().Replace(cleaned, string.Empty);
        // 3 个以上连续换行（空行团）折叠为 1 个空行。
        cleaned = Regex.Replace(cleaned, "\n{3,}", "\n\n");

        var lines = cleaned.Split('\n');
        // 去掉末尾空行（结尾换行产生），不影响内容。
        int count = lines.Length;
        while (count > 0 && string.IsNullOrWhiteSpace(lines[count - 1])) count--;

        int collapsedLines = 0;
        int droppedChars = 0;

        // 阶段 1：行级 RLE —— 折叠任意位置的连续重复行（>=3 次）。
        // 覆盖「同一行/同一报错逐文件重复数百次」中无块结构、仅行连续重复的情形。
        var rleLines = new List<string>(count);
        {
            int i = 0;
            while (i < count)
            {
                int j = i;
                while (j < count && lines[j] == lines[i]) j++;
                int run = j - i;
                if (run >= 3)
                {
                    rleLines.Add(lines[i]);
                    rleLines.Add($"[重复内容已折叠：同一行重复 {run - 1} 次]");
                    collapsedLines += run - 1;
                    droppedChars += (lines[i].Length + 1) * (run - 1);
                }
                else
                {
                    for (int k = i; k < j; k++) rleLines.Add(lines[k]);
                }
                i = j;
            }
        }

        // 阶段 2：块级周期折叠 —— 整段输出按最小周期重复 >=3 次（可含尾部不完整块）时，
        // 把 p 行块折叠为 1 份 + 标注。覆盖「7 行报错块 × 896 次」这类整段周期性重复。
        string collapsedText;
        {
            int n = rleLines.Count;
            int p = FindPeriod(rleLines, n);
            if (p > 0)
            {
                int repeats = (n - n % p) / p;
                var sb = new StringBuilder(p * 16);
                for (int i = 0; i < p; i++) sb.Append(rleLines[i]).Append('\n');
                sb.Append(p == 1
                    ? $"[重复内容已折叠：同一行重复 {repeats - 1} 次]\n"
                    : $"[重复内容已折叠：上述 {p} 行内容重复 {repeats} 次]\n");
                collapsedText = sb.ToString();
                for (int i = p; i < n; i++) droppedChars += rleLines[i].Length + 1;
                collapsedLines += n - p;
            }
            else
            {
                var sb = new StringBuilder(cleaned.Length);
                foreach (var line in rleLines) sb.Append(line).Append('\n');
                collapsedText = sb.ToString();
            }
        }

        var result = collapsedText.TrimEnd('\n');
        if (result.Length <= maxChars)
            return new ProcessResult(result, droppedChars, collapsedLines);

        // 首尾各保留一半：头部通常是实际内容（如 grep 匹配），尾部通常是错误/退出上下文。
        int half = maxChars / 2;
        var head = TrimSurrogate(result.Substring(0, half), isHead: true);
        var tail = TrimSurrogate(result.Substring(result.Length - half), isHead: false);
        int cutDropped = result.Length - head.Length - tail.Length - 2; // 2 = 中间换行
        var marker = $"[输出过长已截断：省略 {cutDropped} 字符，如需完整信息请缩小搜索范围后重试]";
        return new ProcessResult(
            $"{head}\n{marker}\n{tail}",
            droppedChars + cutDropped,
            collapsedLines);
    }

    /// <summary>找覆盖整段输出的最小重复周期；找不到（或不足 3 个完整周期）返回 0。</summary>
    private static int FindPeriod(IReadOnlyList<string> lines, int count)
    {
        // 至少 3 个完整重复周期才折叠；周期上限 256 行——更长的「块」几乎不可能是纯重复，
        // 且每个候选周期最多一次 O(count) 扫描，成本可控。
        int maxPeriod = Math.Min(count / 3, 256);
        for (int p = 1; p <= maxPeriod; p++)
        {
            int full = count - count % p;
            bool isPeriodic = true;
            for (int i = 0; i < full; i++)
            {
                if (lines[i] != lines[i % p])
                {
                    isPeriodic = false;
                    break;
                }
            }
            if (isPeriodic)
                return p;
        }
        return 0;
    }

    /// <summary>截断边界对齐：不把 UTF-16 代理对切成半个，避免出现孤立代理字符。</summary>
    private static string TrimSurrogate(string s, bool isHead)
    {
        if (isHead)
            return s.Length > 0 && char.IsHighSurrogate(s[^1]) ? s[..^1] : s;
        return s.Length > 0 && char.IsLowSurrogate(s[0]) ? s[1..] : s;
    }
}
