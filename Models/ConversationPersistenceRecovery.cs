using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Athena.UI.Models;

public static class ConversationPersistenceRecovery
{
    /// <summary>
    /// 修复旧版可能产生的“消息已压缩但摘要缺失”状态。宁可恢复原消息并增加上下文，
    /// 也不能让消息与摘要同时缺席。方法幂等。
    /// </summary>
    public static bool Repair(ConversationHistoryItem item, DateTime? repairedAt = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var hasSummary = !string.IsNullOrWhiteSpace(item.ContextSummary);
        var hasCompressedMessages = item.Messages.Any(message => message.IsCompressed);

        if (!hasSummary && hasCompressedMessages)
        {
            foreach (var message in item.Messages) message.IsCompressed = false;
            item.CompressionHistory.Clear();
            Touch(item, repairedAt);
            return true;
        }

        if (!hasSummary)
            return false;

        if (TryGetValidatedCompressionIds(item, out var compressedIds))
        {
            var changed = false;
            foreach (var message in item.Messages)
            {
                var shouldBeCompressed = compressedIds.Contains(message.Id);
                if (message.IsCompressed == shouldBeCompressed) continue;
                message.IsCompressed = shouldBeCompressed;
                changed = true;
            }
            if (changed) Touch(item, repairedAt);
            return changed;
        }

        if (!hasCompressedMessages)
        {
            // Sending both an unverifiable legacy summary and every original message duplicates
            // history. Quarantine the text for diagnostics/user recovery but never inject it.
            item.OrphanedLegacySummary = item.ContextSummary;
            item.ContextSummary = null;
            item.CompressionHistory.Clear();
            Touch(item, repairedAt);
            return true;
        }

        // The primary summary + compressed message state is internally safe. Invalid undo records
        // must not be allowed to delete or reactivate messages, so discard only the unsafe stack.
        if (item.CompressionHistory.Count > 0)
        {
            item.CompressionHistory.Clear();
            Touch(item, repairedAt);
            return true;
        }
        return false;
    }

    private static bool TryGetValidatedCompressionIds(
        ConversationHistoryItem item,
        out HashSet<string> ids)
    {
        ids = new HashSet<string>(StringComparer.Ordinal);
        if (item.CompressionHistory.Count == 0) return false;
        var knownIds = item.Messages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var checkpoint in item.CompressionHistory)
        {
            if (checkpoint.MessageIds.Count == 0
                || checkpoint.MessageIds.Distinct(StringComparer.Ordinal).Count() != checkpoint.MessageIds.Count)
                return false;
            foreach (var id in checkpoint.MessageIds)
            {
                if (string.IsNullOrWhiteSpace(id) || !knownIds.Contains(id) || !ids.Add(id))
                    return false;
            }
        }

        var latest = item.CompressionHistory[^1];
        if (!string.IsNullOrWhiteSpace(latest.SummaryAfter)
            && string.Equals(latest.SummaryAfter, item.ContextSummary, StringComparison.Ordinal))
            return true;
        return !string.IsNullOrWhiteSpace(latest.SummaryAfterHash)
               && string.Equals(
                   latest.SummaryAfterHash,
                   Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.ContextSummary!))).ToLowerInvariant(),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void Touch(ConversationHistoryItem item, DateTime? repairedAt)
    {
        item.Revision = Math.Max(1, item.Revision + 1);
        item.UpdatedAt = repairedAt ?? DateTime.UtcNow;
    }
}
