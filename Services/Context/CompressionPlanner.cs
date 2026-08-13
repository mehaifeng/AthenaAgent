using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Athena.UI.Services.Context;

/// <summary>
/// Builds a zero-mutation compression plan from complete user rounds. It never changes
/// message flags; generation, validation and persistence are separate phases.
/// </summary>
public sealed class CompressionPlanner : ICompressionPlanner
{
    public CompressionPlanResult CreatePlan(CompressionPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ConversationId))
            return CompressionPlanResult.NotCompressible("Conversation identity is missing.");
        if (string.IsNullOrWhiteSpace(request.BaseContextFingerprint))
            return CompressionPlanResult.NotCompressible("Context fingerprint is missing.");

        // 摘要长度的上限。三者取小：用户设的上限、压缩模型一次能输出多长、阈值的 1/4
        // （避免摘要本身占掉预算的一大块）。真正的长度由材料除以压缩强度得出，见下面的收窄循环。
        var summaryCeiling = Math.Min(
            request.RequestedTargetSummaryTokens,
            Math.Min(
                request.CompressionModelPolicy.OutputReserveTokens,
                request.MainModelPolicy.CompressionThresholdTokens / 4));
        if (summaryCeiling < 128)
            return CompressionPlanResult.NotCompressible("Effective summary ceiling is below 128 tokens.");
        var summaryRatio = Math.Max(1, request.MainModelPolicy.SummaryRatio);

        var active = request.Messages.Where(message => !message.IsCompressed).ToArray();
        var groups = BuildRoundGroups(active);
        var complete = groups.Where(group => group.IsComplete).ToArray();
        var keepRecentRounds = Math.Max(1, request.KeepRecentRounds);
        if (complete.Length <= keepRecentRounds)
            return CompressionPlanResult.NotCompressible("No completed round exists outside the recent retention window.");

        // 从最宽的窗口开始收窄，直到这份材料在本地判定上可行为止。收窄降低压缩比、
        // 也降低收益，两者单调反向，所以第一个通过比例检查的窗口若收益不足，
        // 再窄只会更差——一趟扫描即可定论，全程零模型调用。
        var maxGroups = complete.Length - keepRecentRounds;
        var groupCount = maxGroups;
        CompressionFeasibilityVerdict? lastVerdict = null;
        var target = summaryCeiling;
        while (groupCount >= 1)
        {
            var candidateMaterial = complete.Take(groupCount)
                .SelectMany(group => group.Messages)
                .Select(ToMaterial)
                .ToArray();
            var materialTokens = CompressionValidator.EstimateMaterialTokens(candidateMaterial);
            // 摘要长度跟着材料走，而不是固定值。压 2,000 token 的材料却按 12,000 去要摘要，
            // 结果是压完反而更大——这正是旧的「摘要目标 Token」的失败方式。
            target = Math.Clamp((materialTokens + summaryRatio - 1) / summaryRatio, 128, summaryCeiling);
            lastVerdict = CompressionFeasibility.Evaluate(
                materialTokens,
                target,
                CompressionValidator.ExtractHardAnchors(candidateMaterial),
                CompressionFeasibility.RequiredBenefitTokens(request.PreCompressionEstimate),
                summaryRatio);
            if (lastVerdict.IsFeasible) break;
            // 收益不足是收窄造成的，继续收窄只会更少——立即停手。
            if (lastVerdict.ProjectedBenefitTokens > 0 && lastVerdict.RequiredRatio <= summaryRatio)
                break;
            groupCount--;
        }
        if (lastVerdict is not { IsFeasible: true })
            return CompressionPlanResult.NotCompressible(
                "No compressible window is feasible: " + (lastVerdict?.Reason ?? "no complete round available."));

        var compressGroups = complete.Take(groupCount).ToArray();
        var compressIds = compressGroups
            .SelectMany(group => group.Messages)
            .Select(message => message.Id)
            .ToArray();
        if (compressIds.Length == 0)
            return CompressionPlanResult.NotCompressible("No complete message group can be compressed safely.");

        var compressSet = new HashSet<string>(compressIds, StringComparer.Ordinal);
        var retainIds = active
            .Where(message => !compressSet.Contains(message.Id))
            .Select(message => message.Id)
            .ToArray();
        var material = compressGroups
            .SelectMany(group => group.Messages)
            .Select(ToMaterial)
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
            Math.Max(0, request.PreCompressionEstimate),
            target,
            request.MainModelPolicy,
            request.CompressionModelPolicy,
            request.PromptVersion));
    }

    private static IReadOnlyList<RoundGroup> BuildRoundGroups(IReadOnlyList<ChatMessage> messages)
    {
        var groups = new List<RoundGroup>();
        var prefix = new List<ChatMessage>();
        List<ChatMessage>? current = null;
        foreach (var message in messages)
        {
            if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null)
                    groups.Add(new RoundGroup(current, IsCompleteRound(current)));
                else if (prefix.Count > 0)
                    groups.Add(new RoundGroup(prefix, false));
                current = [message];
            }
            else if (current == null)
            {
                prefix.Add(message);
            }
            else
            {
                current.Add(message);
            }
        }
        if (current != null)
            groups.Add(new RoundGroup(current, IsCompleteRound(current)));
        else if (prefix.Count > 0)
            groups.Add(new RoundGroup(prefix, false));
        return groups;
    }

    private static bool IsCompleteRound(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count < 2
            || !string.Equals(messages[0].Role, "user", StringComparison.OrdinalIgnoreCase))
            return false;

        var declared = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var hasTerminalAssistant = false;

        foreach (var message in messages.Skip(1))
        {
            if (message.IsLoading || message.IsStreaming)
                return false;

            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(message.ToolCallId)
                    || !declared.Contains(message.ToolCallId)
                    || !resolved.Add(message.ToolCallId))
                    return false;
                continue;
            }

            if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(message.ToolCallsJson))
            {
                if (!declared.SetEquals(resolved)
                    || !TryReadToolCallIds(message.ToolCallsJson, out var ids))
                    return false;
                foreach (var id in ids)
                {
                    if (!declared.Add(id)) return false;
                }
                hasTerminalAssistant = false;
                continue;
            }

            if (!declared.SetEquals(resolved))
                return false;
            hasTerminalAssistant = true;
        }

        return hasTerminalAssistant && declared.SetEquals(resolved);
    }

    private static bool TryReadToolCallIds(string json, out IReadOnlyList<string> ids)
    {
        ids = Array.Empty<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() == 0)
                return false;
            var parsed = new List<string>();
            foreach (var call in document.RootElement.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object)
                    return false;
                var id = call.EnumerateObject()
                    .FirstOrDefault(property => string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                    .Value;
                if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                    return false;
                parsed.Add(id.GetString()!);
            }
            if (parsed.Count != parsed.Distinct(StringComparer.Ordinal).Count())
                return false;
            ids = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CompressionMaterialMessage ToMaterial(ChatMessage message) => new(
        message.Id,
        message.Role,
        message.Content,
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

    private sealed record RoundGroup(IReadOnlyList<ChatMessage> Messages, bool IsComplete);
}
