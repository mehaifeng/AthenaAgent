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

        var target = Math.Min(
            request.RequestedTargetSummaryTokens,
            Math.Min(
                request.CompressionModelPolicy.OutputReserveTokens,
                request.MainModelPolicy.CompressionThresholdTokens / 4));
        if (target < 128)
            return CompressionPlanResult.NotCompressible("Effective summary target is below 128 tokens.");

        var active = request.Messages.Where(message => !message.IsCompressed).ToArray();
        var groups = BuildRoundGroups(active);
        var complete = groups.Where(group => group.IsComplete).ToArray();
        var keepRecentRounds = Math.Max(1, request.KeepRecentRounds);
        if (complete.Length <= keepRecentRounds)
            return CompressionPlanResult.NotCompressible("No completed round exists outside the recent retention window.");

        var compressGroups = complete.Take(complete.Length - keepRecentRounds).ToArray();
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
