using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Athena.UI.Services.GameMode;

/// <summary>那场会话里的一段话：谁说的、什么时候、说了什么（太长的只留头尾）。</summary>
public sealed record PolisCitizenExcerpt(int Index, string Role, DateTime Timestamp, string Text);

/// <summary>
/// 一位居民记得的事（M-C 起；2026-10-13 起是 <see cref="PolisTalk"/> 提示词里"你记得的"那一段）：你和他说话时，
/// 按你这一句从他那场会话里挑几段。纯函数，<c>Athena.Archive.Tests</c> 的 <c>polis citizen answers</c> 覆盖。
///
/// 规则：
/// 1. 只取那一场会话里你看得见的话：用户与雅典娜的气泡（隐藏的工具调用载体、工具结果、系统消息都不要）。
///    被压缩过的旧消息照样算——上下文压缩只是不再发给主模型，气泡还在，你问的往往正是那一段。
/// 2. 按问题挑：问题拆成词（汉字两两一组、拉丁词、数字），一段话里出现的词越多越先挑，同样多挑新的；
///    第一句请求和最后一句回答永远在候选里（问"办成了吗"这种不带关键词的问题，靠它们答）。最多 8 段、总共 ≤ 6000 字，
///    太长的一段只留头尾各 400 字。挑出来按原来的先后排。
/// 3. 提示词（<see cref="PolisTalk.BuildChat"/>）要他说到那件事时只凭这些片段，片段里没有的就说记不清——不编。
/// </summary>
public static class PolisCitizenAnswers
{
    public const int BudgetChars = 6000;
    public const int MaxExcerpts = 8;
    /// <summary>一段话太长时头尾各留多少字。</summary>
    public const int KeepEachEnd = 400;

    /// <summary>一条消息在气泡里看得见的正文：用户消息是它的内容；雅典娜的回答是正文段（没有段的旧消息用内容）。</summary>
    public static string VisibleText(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.Equals(message.Role, "assistant", StringComparison.Ordinal) && message.Segments.Count > 0)
        {
            var parts = message.Segments
                .Where(s => s.Kind == ChatMessageSegmentKind.Markdown && !string.IsNullOrWhiteSpace(s.Text))
                .Select(s => s.Text.Trim());
            return string.Join("\n\n", parts);
        }
        return message.Content?.Trim() ?? string.Empty;
    }

    /// <summary>规则 2 的分词：汉字两两一组（只有一个字就那一个字）、拉丁词与数字（小写，两个字符起）。</summary>
    public static IReadOnlySet<string> Tokens(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return tokens;
        var cjk = new StringBuilder();
        var word = new StringBuilder();
        void FlushCjk()
        {
            if (cjk.Length == 1) tokens.Add(cjk.ToString());
            for (var i = 0; i + 1 < cjk.Length; i++) tokens.Add(cjk.ToString(i, 2));
            cjk.Clear();
        }
        void FlushWord()
        {
            if (word.Length >= 2) tokens.Add(word.ToString().ToLowerInvariant());
            word.Clear();
        }
        foreach (var ch in text)
        {
            if (IsCjk(ch)) { FlushWord(); cjk.Append(ch); }
            else if (char.IsLetterOrDigit(ch)) { FlushCjk(); word.Append(ch); }
            else { FlushCjk(); FlushWord(); }
        }
        FlushCjk();
        FlushWord();
        return tokens;
    }

    /// <summary>规则 1、2：从那一场会话里挑几段话。</summary>
    public static IReadOnlyList<PolisCitizenExcerpt> SelectExcerpts(
        IReadOnlyList<ChatMessage> messages,
        string question,
        int budgetChars = BudgetChars,
        int maxExcerpts = MaxExcerpts)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var candidates = new List<(int Index, ChatMessage Message, string Text)>();
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.IsHidden) continue;
            if (m.Role is not ("user" or "assistant")) continue;
            var text = VisibleText(m);
            if (text.Length == 0) continue;
            candidates.Add((i, m, text));
        }
        if (candidates.Count == 0) return Array.Empty<PolisCitizenExcerpt>();

        var wanted = Tokens(question);
        var firstUser = candidates.Where(c => c.Message.Role == "user").Select(c => c.Index).DefaultIfEmpty(-1).First();
        var lastAssistant = candidates.Where(c => c.Message.Role == "assistant").Select(c => c.Index).DefaultIfEmpty(-1).Last();
        var scored = candidates
            .Select(c =>
            {
                var lower = c.Text.ToLowerInvariant();
                var hits = wanted.Count(t => lower.Contains(t, StringComparison.Ordinal));
                // 第一句请求与最后一句回答：没有一个词对得上时也在候选里，但排在对得上的后面
                var anchor = c.Index == firstUser || c.Index == lastAssistant ? 0.5 : 0;
                return (c.Index, c.Message, c.Text, Score: hits + anchor);
            })
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Index)
            .ToList();

        var picked = new List<PolisCitizenExcerpt>();
        var used = 0;
        foreach (var c in scored)
        {
            if (picked.Count >= maxExcerpts) break;
            var text = Clip(c.Text);
            if (used + text.Length > budgetChars)
            {
                // 预算剩得不多：这一段放不下就看下一段（更短的也许放得下）
                continue;
            }
            used += text.Length;
            picked.Add(new PolisCitizenExcerpt(c.Index, c.Message.Role, c.Message.Timestamp, text));
        }
        return picked.OrderBy(e => e.Index).ToList();
    }

    /// <summary>太长的一段只留头尾各 <see cref="KeepEachEnd"/> 字，中间写明省掉了多少。</summary>
    public static string Clip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= KeepEachEnd * 2 + 40) return text;
        var head = text[..KeepEachEnd];
        var tail = text[^KeepEachEnd..];
        if (char.IsHighSurrogate(head[^1])) head = head[..^1];
        if (char.IsLowSurrogate(tail[0])) tail = tail[1..];
        var omitted = text.Length - head.Length - tail.Length;
        return $"{head} … [{omitted.ToString(CultureInfo.InvariantCulture)} chars omitted] … {tail}";
    }

    /// <summary>
    /// 模型的回答压成能放进卡片的样子：去掉推理块之外的首尾空白、引号与 Markdown 的粗体，换行收成空格，最多 <paramref name="maxChars"/> 字。
    /// 空的算没有回答。
    /// </summary>
    public static string? Sanitize(string? text, int maxChars = 1200)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = string.Join(" ", lines).Replace("**", string.Empty, StringComparison.Ordinal).Trim().Trim('"', '“', '”', '「', '」');
        if (joined.Length == 0) return null;
        if (joined.Length <= maxChars) return joined;
        var cut = maxChars;
        if (char.IsHighSurrogate(joined[cut - 1])) cut--;
        return joined[..cut] + "…";
    }

    private static bool IsCjk(char c) => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';
}
