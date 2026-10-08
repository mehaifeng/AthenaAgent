using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Athena.UI.Services.Context;

/// <summary>
/// 工具结果清理（microcompact）：超过压缩阈值时，先把旧工具结果在<b>请求投影</b>里换成一行占位说明，
/// 存档原文不动。全部为纯函数，写法参照 <see cref="ContextAnchorLedger"/>：给定活跃消息、
/// 已清理集合和保留额度，算出这一次新增清理的消息 ID；状态（已清理集合）由调用方持有并持久化。
/// <para>
/// 三条规则让它不打架：
/// <list type="bullet">
/// <item>保留区按字符、从最新往回数；保留区之外的<b>全部</b>一次清掉——不算要清多少、不设目标降幅、不设最小批次。</item>
/// <item>被清理的集合只增不减，所以请求前缀只在超阈值的那一刻变化，不会清了又恢复、反复打掉提示缓存。</item>
/// <item>横跨保留额度边界的那一条算<b>保留</b>：最新的一条工具结果永远不会被清掉，
/// 否则模型刚拿到的结果就会在同一轮里被换成占位说明。</item>
/// </list>
/// </para>
/// </summary>
public static class ToolResultClearing
{
    public const string UnknownToolName = "unknown";

    /// <summary>
    /// 这一次应当新增清理的工具消息 ID（由旧到新）。没有新增可清项时返回空列表——
    /// 调用方据此判断「清理已经做到头了」，下一次仍超阈值就该进入全量压缩。
    /// </summary>
    public static IReadOnlyList<string> SelectNewlyClearable(
        IReadOnlyList<ContextMessage> activeMessages,
        IReadOnlyCollection<string>? alreadyCleared,
        long keepRecentChars)
    {
        ArgumentNullException.ThrowIfNull(activeMessages);
        var cleared = alreadyCleared == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(alreadyCleared, StringComparer.Ordinal);
        var keep = Math.Max(0, keepRecentChars);

        var newly = new List<string>();
        long retained = 0;
        for (var i = activeMessages.Count - 1; i >= 0; i--)
        {
            var message = activeMessages[i];
            if (!IsToolResult(message) || cleared.Contains(message.Id)) continue;

            // 此前累计的保留量已达额度：这一条及更早的都在保留区之外。
            // 判定放在累加之前，所以横跨边界的那一条仍算保留。
            if (retained >= keep)
            {
                newly.Add(message.Id);
                continue;
            }
            retained += message.Content?.Length ?? 0;
        }

        newly.Reverse();
        return newly;
    }

    /// <summary>占位文本（不落盘，投影时生成）。工具名取不到时写 <see cref="UnknownToolName"/>。</summary>
    public static string BuildPlaceholder(string? toolName, int originalChars)
        => $"[旧工具结果已清理以节省上下文：tool={(string.IsNullOrWhiteSpace(toolName) ? UnknownToolName : toolName)}，原长 {originalChars} 字符。如仍需要，请重新调用该工具。]";

    /// <summary>
    /// 工具调用 ID → 工具名，取自 assistant 消息的 <c>ToolCallsJson</c>。
    /// 解析失败的消息整条跳过（对应的占位说明退回 <see cref="UnknownToolName"/>），不抛异常。
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildToolNameIndex(IReadOnlyList<ContextMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return BuildToolNameIndex(messages
            .Where(message => string.Equals(message.Role, "assistant", StringComparison.Ordinal))
            .Select(message => message.ToolCallsJson));
    }

    /// <summary>同上，直接吃各条 assistant 消息的 <c>ToolCallsJson</c>（供只有 <c>ChatMessage</c> 的调用方使用）。</summary>
    public static IReadOnlyDictionary<string, string> BuildToolNameIndex(IEnumerable<string?> toolCallsJsons)
    {
        ArgumentNullException.ThrowIfNull(toolCallsJsons);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var toolCallsJson in toolCallsJsons)
        {
            if (string.IsNullOrEmpty(toolCallsJson)) continue;
            try
            {
                using var document = JsonDocument.Parse(toolCallsJson);
                if (document.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var call in document.RootElement.EnumerateArray())
                {
                    var id = ReadString(call, "Id");
                    var name = ReadString(call, "FunctionName");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) names[id] = name;
                }
            }
            catch (JsonException)
            {
                // 残缺的 ToolCallsJson 只会让这条消息的工具名退回 unknown，请求构造本身另有失败路径。
            }
        }
        return names;
    }

    /// <summary>这条工具消息在请求里应当呈现的占位文本。</summary>
    public static string BuildPlaceholder(ContextMessage toolMessage, IReadOnlyDictionary<string, string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolMessage);
        ArgumentNullException.ThrowIfNull(toolNames);
        var name = toolMessage.ToolCallId != null && toolNames.TryGetValue(toolMessage.ToolCallId, out var found)
            ? found
            : null;
        return BuildPlaceholder(name, toolMessage.Content?.Length ?? 0);
    }

    /// <summary>
    /// 已清理集合的摘要，并入请求的固定开销指纹：清理改变了请求内容却不改变消息 ID，
    /// 不并进指纹，<see cref="ContextAnchorLedger"/> 就会把清理前的测量当作精确值。
    /// 空集合返回空串，所以从未清理过的会话指纹与改动前完全一致。
    /// </summary>
    public static string ComputeDigest(IEnumerable<string>? clearedIds)
    {
        if (clearedIds == null) return string.Empty;
        var ordered = clearedIds.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0) return string.Empty;
        var material = string.Join('\u001f', ordered);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    /// <summary>并入新清理的 ID，保持只增不减、去重、顺序稳定。</summary>
    public static List<string> Merge(IEnumerable<string>? existing, IEnumerable<string> added)
    {
        ArgumentNullException.ThrowIfNull(added);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<string>();
        foreach (var id in (existing ?? []).Concat(added))
        {
            if (!string.IsNullOrEmpty(id) && seen.Add(id)) merged.Add(id);
        }
        return merged;
    }

    /// <summary>
    /// 只保留仍存在的消息 ID。回退/分叉后，被截掉的消息不应再留在集合里；
    /// 全量压缩提交后，被压缩的 ID 也从这里移除（它们已经不在请求里了）。
    /// </summary>
    public static List<string> Prune(IEnumerable<string>? cleared, IEnumerable<string> liveMessageIds)
    {
        ArgumentNullException.ThrowIfNull(liveMessageIds);
        var live = new HashSet<string>(liveMessageIds, StringComparer.Ordinal);
        return (cleared ?? [])
            .Where(id => !string.IsNullOrEmpty(id) && live.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>检查器「概览」页用：已清理几条、原文多少字符、占位后还剩多少字符。</summary>
    public static ClearingSummary Summarize(IReadOnlyList<ContextMessage> activeMessages, IReadOnlyCollection<string>? cleared)
    {
        ArgumentNullException.ThrowIfNull(activeMessages);
        if (cleared == null || cleared.Count == 0) return ClearingSummary.None;
        var clearedSet = cleared as ISet<string> ?? new HashSet<string>(cleared, StringComparer.Ordinal);
        var names = BuildToolNameIndex(activeMessages);
        int count = 0;
        long original = 0, placeholder = 0;
        foreach (var message in activeMessages)
        {
            if (!IsToolResult(message) || !clearedSet.Contains(message.Id)) continue;
            count++;
            original += message.Content?.Length ?? 0;
            placeholder += BuildPlaceholder(message, names).Length;
        }
        return new ClearingSummary(count, original, placeholder);
    }

    private static bool IsToolResult(ContextMessage message)
        => string.Equals(message.Role, "tool", StringComparison.Ordinal);

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }
        return null;
    }
}

/// <summary>已清理工具结果的统计；<see cref="SavedChars"/> 是请求里实际少掉的字符数。</summary>
public readonly record struct ClearingSummary(int Count, long OriginalChars, long PlaceholderChars)
{
    public static ClearingSummary None => new(0, 0, 0);
    public long SavedChars => Math.Max(0, OriginalChars - PlaceholderChars);
}
