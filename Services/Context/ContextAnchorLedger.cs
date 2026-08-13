using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Athena.UI.Services.Context;

/// <summary>
/// 真实用量锚点的选取与维护。全部为纯函数：给定一串已落盘的测量和当前上下文，
/// 判断哪一条仍然可以当作精确值使用。
/// </summary>
public static class ContextAnchorLedger
{
    /// <summary>每个会话保留的锚点上限。只保留最长的若干条即可覆盖回溯需求。</summary>
    public const int MaxRetainedAnchors = 64;

    public static string ComputePrefixDigest(IEnumerable<string> messageIds)
    {
        var material = string.Join('\u001f', messageIds);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    public static string ComputePrefixDigest(IReadOnlyList<ContextMessage> messages, int prefixCount)
    {
        if (prefixCount < 0 || prefixCount > messages.Count)
            throw new ArgumentOutOfRangeException(nameof(prefixCount));
        return ComputePrefixDigest(messages.Take(prefixCount).Select(message => message.Id));
    }

    /// <summary>
    /// 选出仍然可用的最长锚点。三个条件缺一不可：regime 指纹一致、前缀不长于当前上下文、
    /// 前缀的消息 ID 序列逐条相同。任一不满足即返回 null，调用方回退到估算并在下一次响应重锚。
    /// </summary>
    public static ContextAnchorRecord? SelectLatestValid(
        IReadOnlyList<ContextAnchorRecord>? anchors,
        IReadOnlyList<ContextMessage> messages,
        string profileKey,
        string fixedOverheadFingerprint)
    {
        if (anchors == null || anchors.Count == 0 || string.IsNullOrEmpty(profileKey)) return null;

        // 由长到短：越长的前缀留给增量估算的未测量部分越少。
        foreach (var anchor in anchors
                     .Where(anchor => anchor.PrefixMessageCount > 0
                                      && anchor.PrefixMessageCount <= messages.Count
                                      && anchor.InputTokens > 0
                                      && string.Equals(anchor.ProfileKey, profileKey, StringComparison.Ordinal)
                                      && string.Equals(anchor.FixedOverheadFingerprint, fixedOverheadFingerprint, StringComparison.Ordinal))
                     .OrderByDescending(anchor => anchor.PrefixMessageCount))
        {
            if (string.Equals(
                    ComputePrefixDigest(messages, anchor.PrefixMessageCount),
                    anchor.PrefixDigest,
                    StringComparison.Ordinal))
                return anchor;
        }
        return null;
    }

    /// <summary>
    /// 写入一条新测量。同一前缀长度只保留最新一条（重发/重试会覆盖），
    /// 并按前缀长度保留最长的 <see cref="MaxRetainedAnchors"/> 条。
    /// </summary>
    public static List<ContextAnchorRecord> Append(
        IEnumerable<ContextAnchorRecord>? existing,
        ContextAnchorRecord anchor)
    {
        var merged = (existing ?? Enumerable.Empty<ContextAnchorRecord>())
            .Where(item => item.PrefixMessageCount != anchor.PrefixMessageCount
                           || !string.Equals(item.ProfileKey, anchor.ProfileKey, StringComparison.Ordinal))
            .Append(anchor)
            .OrderByDescending(item => item.PrefixMessageCount)
            .Take(MaxRetainedAnchors)
            .OrderBy(item => item.PrefixMessageCount)
            .ToList();
        return merged;
    }

    /// <summary>
    /// 回溯/分支后裁剪：丢弃前缀长于当前消息数的锚点。前缀内容是否仍一致由
    /// <see cref="SelectLatestValid"/> 的摘要比对负责，这里只做廉价的长度裁剪。
    /// </summary>
    public static List<ContextAnchorRecord> TrimTo(
        IEnumerable<ContextAnchorRecord>? existing,
        int messageCount)
        => (existing ?? Enumerable.Empty<ContextAnchorRecord>())
            .Where(anchor => anchor.PrefixMessageCount <= messageCount)
            .OrderBy(anchor => anchor.PrefixMessageCount)
            .ToList();
}
