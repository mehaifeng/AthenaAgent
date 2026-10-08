using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.Context;

/// <summary>
/// 全量压缩的计划：压缩集 = 全部未压缩、已完成的消息，没有「保留最近 N 轮」，也不要求轮次完整。
/// 进行中的这一轮也一并压进去——工具调用与工具结果同时消失，不会留下悬空的 tool_call_id；
/// 用户意图由摘要里代码追加的 <c>[latest_user_requests]</c> 保住，续写消息让模型接着干。
/// <para>
/// 这里只做身份与指纹检查、挑出压缩集、把材料过一遍工具结果清理投影。没有压缩比门槛、没有收益门槛、
/// 没有收窄循环：旧的「轮次窗口 + 比例/收益门」在一轮约 27.5 万 token 时规划不出任何窗口，
/// 每轮都弹「压缩未成功」（<c>Docs/TechDebt.md</c> 第 2 条）。整个规划全程零模型调用，且不改动任何消息标记。
/// </para>
/// </summary>
public sealed class CompressionPlanner : ICompressionPlanner
{
    /// <summary>压缩集为空时的原因。调用方据此给出「没有可压缩的历史」而不是一句故障报告。</summary>
    public const string NoHistoryReason = "There is no history to compress.";

    public CompressionPlanResult CreatePlan(CompressionPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ConversationId))
            return CompressionPlanResult.NotCompressible("Conversation identity is missing.");
        if (string.IsNullOrWhiteSpace(request.BaseContextFingerprint))
            return CompressionPlanResult.NotCompressible("Context fingerprint is missing.");

        // 摘要长度的上限。三者取小：用户设的上限、压缩模型一次能输出多长、阈值的 1/4
        // （避免摘要本身占掉预算的一大块）。它直接作为压缩请求的 max_output_tokens。
        var summaryCeiling = Math.Min(
            request.RequestedSummaryMaxTokens,
            Math.Min(
                request.CompressionModelPolicy.OutputReserveTokens,
                request.MainModelPolicy.CompressionThresholdTokens / 4));
        if (summaryCeiling < 128)
            return CompressionPlanResult.NotCompressible("Effective summary ceiling is below 128 tokens.");

        var active = request.Messages.Where(message => !message.IsCompressed).ToArray();
        var compressible = active.Where(IsCompressible).ToArray();
        if (compressible.Length == 0)
            return CompressionPlanResult.NotCompressible(NoHistoryReason);

        var compressIds = compressible.Select(message => message.Id).ToArray();
        var compressSet = new HashSet<string>(compressIds, StringComparer.Ordinal);
        var retainIds = active
            .Where(message => !compressSet.Contains(message.Id))
            .Select(message => message.Id)
            .ToArray();

        // 材料先过清理投影：已清理的工具结果以占位文本进入材料，省压缩成本，也让摘要不去复述早已丢弃的原文。
        var cleared = request.ClearedToolResultIds is { Count: > 0 } ids
            ? new HashSet<string>(ids, StringComparer.Ordinal)
            : null;
        var toolNames = cleared == null
            ? null
            : ToolResultClearing.BuildToolNameIndex(active.Select(message => message.ToolCallsJson));
        var clearedInSet = new List<string>();
        var material = compressible
            .Select(message =>
            {
                if (cleared != null && IsToolResult(message) && cleared.Contains(message.Id))
                {
                    clearedInSet.Add(message.Id);
                    var name = message.ToolCallId != null && toolNames!.TryGetValue(message.ToolCallId, out var found) ? found : null;
                    return ToMaterial(message, ToolResultClearing.BuildPlaceholder(name, message.Content?.Length ?? 0));
                }
                return ToMaterial(message, message.Content);
            })
            .ToArray();

        return CompressionPlanResult.Ready(new CompressionPlan(
            Guid.NewGuid().ToString("N"),
            request.ConversationId,
            request.BaseRevision,
            request.BaseContextFingerprint,
            request.TriggerMode,
            string.IsNullOrWhiteSpace(request.ExistingSummary) ? null : request.ExistingSummary,
            compressIds,
            retainIds,
            material,
            Math.Max(0, request.PreCompressionTokens),
            summaryCeiling,
            request.MainModelPolicy,
            request.CompressionModelPolicy,
            request.PromptVersion,
            string.IsNullOrWhiteSpace(request.FocusInstruction) ? null : request.FocusInstruction.Trim(),
            clearedInSet));
    }

    /// <summary>
    /// 能进压缩集的消息：user / assistant / tool 三种角色，且已经完成（不在加载或流式中）。
    /// 与 <c>UpdateConversationContext</c> 的取舍保持一致——它不放进请求的消息（其它角色、
    /// 内容全空的助手消息）压缩了也没有意义，反而会让「压缩集的 ID 必须在活跃集里」的提交检查失配。
    /// </summary>
    private static bool IsCompressible(ChatMessage message)
    {
        if (message.IsLoading || message.IsStreaming) return false;
        if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
            || string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)) return false;
        return !string.IsNullOrEmpty(message.Content)
               || !string.IsNullOrEmpty(message.ToolCallsJson)
               || !string.IsNullOrEmpty(message.ReasoningContent)
               || message.Attachments.Count > 0
               || !string.IsNullOrEmpty(message.OutputAudioReferenceId);
    }

    private static bool IsToolResult(ChatMessage message)
        => string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase);

    private static CompressionMaterialMessage ToMaterial(ChatMessage message, string content) => new(
        message.Id,
        message.Role,
        content,
        message.ToolCallId,
        message.ToolCallsJson,
        message.ReasoningContent,
        message.Timestamp,
        message.Attachments.Select(attachment => new CompressionAttachmentReference(
            attachment.Id,
            attachment.Kind,
            attachment.FileName,
            attachment.StoredPath,
            attachment.MimeType,
            attachment.SizeBytes,
            attachment.Width,
            attachment.Height)).ToArray());
}
