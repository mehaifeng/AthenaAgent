using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>离开期间一座建筑发生了什么。</summary>
public enum PolisBuildingChangeKind
{
    Added,
    Removed,
    /// <summary>文件夹改了名：建筑原地换牌匾（11.2 / 11.3）。</summary>
    Renamed,
    Grew,
    Shrank,
    /// <summary>文件数没变，但有文件被改过（最后修改时间变新了）。</summary>
    Touched
}

public sealed record PolisBuildingChange(string Key, PolisBuildingChangeKind Kind, int FilesBefore, int FilesAfter, string? RenamedFrom = null);

/// <summary>藏品核对的结果：仍在原处、内容被改过、跟着找回到新位置，或者遗失。</summary>
public enum PolisItemRecoveryKind
{
    Unchanged,
    Modified,
    Moved,
    Lost
}

public sealed record PolisItemRecovery(string ItemId, PolisItemRecoveryKind Kind, string? FromPath, string? ToPath);

/// <summary>
/// 一份"你离开期间"的报告（11.3）：建筑的增删改名与体量变化，藏品的找回与遗失。由传令官播报，建筑一次到位。
/// </summary>
public sealed record PolisAwayReport(
    IReadOnlyList<PolisBuildingChange> Buildings,
    IReadOnlyList<PolisItemRecovery> Items)
{
    public static PolisAwayReport Empty { get; } = new(Array.Empty<PolisBuildingChange>(), Array.Empty<PolisItemRecovery>());

    public bool IsEmpty => Buildings.Count == 0 && Items.All(i => i.Kind == PolisItemRecoveryKind.Unchanged);
}

/// <summary>
/// 离线差异（设计稿 11.3）：把上次的索引快照和一次新的扫描对比。应用关着、或者你在别的城邦时都走这一条。
/// 运行期的监听只是提示，扫描结果才算数（11.1）；离线期间的改名没法直接知道，只能推断——
/// 文件夹按子项名字集合的相似度配对，藏品按内容指纹找回。
/// </summary>
public static class PolisDiff
{
    /// <summary>
    /// 两个文件夹算作同一个（改了名）的相似度下限：子项名字集合的 Jaccard 系数。
    /// 0.5 意味着改名的同时最多又换掉了三分之一的东西还认得出来；再低就宁可当成一删一增——
    /// 认错了会把一座建筑的牌匾挂到另一座上，而认不出只是多了一块空地。
    /// </summary>
    public const double RenameSimilarityThreshold = 0.5;

    /// <summary>扫描结果 → 每座建筑的快照条目（带子项名字，供下次离线改名配对）。</summary>
    public static IReadOnlyList<PolisIndexBuilding> IndexBuildings(PolisScanResult scan, PolisSummary summary)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(summary);
        var children = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var file in scan.Files)
        {
            var parts = file.RelativePath.Split('/');
            if (parts.Length < 2) continue;
            if (!children.TryGetValue(parts[0], out var set)) children[parts[0]] = set = new SortedSet<string>(StringComparer.Ordinal);
            if (set.Count < PolisSaveFormat.MaxChildNamesPerBuilding) set.Add(parts[1]);
        }
        return summary.Buildings
            .Select(b => new PolisIndexBuilding(
                b.Key, b.Kind, b.FileCount, b.TotalBytes, b.LastModifiedUtc, b.Incomplete,
                children.TryGetValue(b.Key, out var names) ? names.ToList() : Array.Empty<string>()))
            .ToList();
    }

    /// <summary>
    /// 推断离线期间的文件夹改名：消失的 × 新出现的，两两算子项名字集合的相似度，从最像的开始一对一配上，
    /// 低于 <see cref="RenameSimilarityThreshold"/> 的不配。没有子项的文件夹无从比较，一律不配。
    /// 同样相似时按文件数更接近、再按名字排序，结果确定。
    /// </summary>
    public static IReadOnlyList<(string From, string To)> InferRenames(
        IReadOnlyList<PolisIndexBuilding> before,
        IReadOnlyList<PolisIndexBuilding> after,
        double threshold = RenameSimilarityThreshold)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var afterKeys = new HashSet<string>(after.Select(b => b.Key), StringComparer.Ordinal);
        var beforeKeys = new HashSet<string>(before.Select(b => b.Key), StringComparer.Ordinal);
        var gone = before.Where(b => !afterKeys.Contains(b.Key) && b.ChildNames.Count > 0).ToList();
        var fresh = after.Where(b => !beforeKeys.Contains(b.Key) && b.ChildNames.Count > 0).ToList();

        var candidates = new List<(PolisIndexBuilding From, PolisIndexBuilding To, double Similarity)>();
        foreach (var from in gone)
        {
            var fromSet = new HashSet<string>(from.ChildNames, StringComparer.Ordinal);
            foreach (var to in fresh)
            {
                var shared = to.ChildNames.Count(fromSet.Contains);
                var union = fromSet.Count + to.ChildNames.Distinct(StringComparer.Ordinal).Count() - shared;
                var similarity = union == 0 ? 0 : (double)shared / union;
                if (similarity >= threshold) candidates.Add((from, to, similarity));
            }
        }

        var usedFrom = new HashSet<string>(StringComparer.Ordinal);
        var usedTo = new HashSet<string>(StringComparer.Ordinal);
        var renames = new List<(string, string)>();
        foreach (var (from, to, _) in candidates
                     .OrderByDescending(c => c.Similarity)
                     .ThenBy(c => Math.Abs(c.From.FileCount - c.To.FileCount))
                     .ThenBy(c => c.From.Key, StringComparer.Ordinal)
                     .ThenBy(c => c.To.Key, StringComparer.Ordinal))
        {
            if (usedFrom.Contains(from.Key) || usedTo.Contains(to.Key)) continue;
            usedFrom.Add(from.Key);
            usedTo.Add(to.Key);
            renames.Add((from.Key, to.Key));
        }
        return renames;
    }

    /// <summary>建筑层面的差异：增、删、改名（已推断好的）、变大、变小、被改过。</summary>
    public static IReadOnlyList<PolisBuildingChange> CompareBuildings(
        IReadOnlyList<PolisIndexBuilding> before,
        IReadOnlyList<PolisIndexBuilding> after,
        IReadOnlyList<(string From, string To)> renames)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(renames);
        var beforeByKey = before.ToDictionary(b => b.Key, StringComparer.Ordinal);
        var afterByKey = after.ToDictionary(b => b.Key, StringComparer.Ordinal);
        var renamedFrom = renames.ToDictionary(r => r.To, r => r.From, StringComparer.Ordinal);
        var renamedAway = new HashSet<string>(renames.Select(r => r.From), StringComparer.Ordinal);
        var changes = new List<PolisBuildingChange>();

        foreach (var now in after.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            if (renamedFrom.TryGetValue(now.Key, out var oldKey))
            {
                changes.Add(new PolisBuildingChange(now.Key, PolisBuildingChangeKind.Renamed, beforeByKey[oldKey].FileCount, now.FileCount, oldKey));
                continue;
            }
            if (!beforeByKey.TryGetValue(now.Key, out var was))
            {
                changes.Add(new PolisBuildingChange(now.Key, PolisBuildingChangeKind.Added, 0, now.FileCount));
                continue;
            }
            if (now.FileCount > was.FileCount)
                changes.Add(new PolisBuildingChange(now.Key, PolisBuildingChangeKind.Grew, was.FileCount, now.FileCount));
            else if (now.FileCount < was.FileCount)
                changes.Add(new PolisBuildingChange(now.Key, PolisBuildingChangeKind.Shrank, was.FileCount, now.FileCount));
            else if (now.LastModifiedUtc > was.LastModifiedUtc)
                changes.Add(new PolisBuildingChange(now.Key, PolisBuildingChangeKind.Touched, was.FileCount, now.FileCount));
        }
        foreach (var was in before.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            if (!afterByKey.ContainsKey(was.Key) && !renamedAway.Contains(was.Key))
                changes.Add(new PolisBuildingChange(was.Key, PolisBuildingChangeKind.Removed, was.FileCount, 0));
        }
        return changes;
    }

    /// <summary>
    /// 核对藏品（11.2 / 11.3）。先按路径找：路径还在，就保持关联、按需更新指纹（内容变了挂上"刚被修改"）；
    /// 路径不在了，先试着把它穿过推断出的文件夹改名；再在同一座城邦里按内容指纹找（只对大小相同的文件算哈希）；
    /// 都找不到的标为遗失——保留记录和名字，绝不自动删除。<paramref name="fingerprintOf"/> 读磁盘（测试里是内存表），
    /// 文件不存在返回 null。<paramref name="cityFiles"/> 是这次扫描到的全部文件（相对路径 + 大小）。
    /// </summary>
    public static (IReadOnlyList<PolisItem> Items, IReadOnlyList<PolisItemRecovery> Report) ReconcileItems(
        IReadOnlyList<PolisItem> items,
        IReadOnlyList<PolisScannedFile> cityFiles,
        IReadOnlyList<(string From, string To)> renames,
        Func<string, PolisFingerprint?> fingerprintOf,
        int maxCandidatesPerItem = 24)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(cityFiles);
        ArgumentNullException.ThrowIfNull(renames);
        ArgumentNullException.ThrowIfNull(fingerprintOf);
        var bySize = cityFiles
            .GroupBy(f => f.Size)
            .ToDictionary(g => g.Key, g => g.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToList());
        var claimed = new HashSet<string>(items.Where(i => i.RelativePath != null && !i.Lost)
            .Select(i => i.RelativePath!), StringComparer.Ordinal);
        var result = new List<PolisItem>(items.Count);
        var report = new List<PolisItemRecovery>();

        foreach (var item in items)
        {
            if (item.RelativePath == null)
            {
                result.Add(item);
                continue;
            }

            var atPath = fingerprintOf(item.RelativePath);
            if (atPath != null)
            {
                var modified = item.Fingerprint != null && !item.Fingerprint.Matches(atPath);
                // 遗失的东西又回来了：原来的处理状态（收下 / 退回 / 待处理）原样复原
                result.Add(item with
                {
                    Fingerprint = atPath,
                    ModifiedSinceDelivery = item.ModifiedSinceDelivery || modified,
                    Lost = false
                });
                report.Add(new PolisItemRecovery(item.Id, modified ? PolisItemRecoveryKind.Modified : PolisItemRecoveryKind.Unchanged, item.RelativePath, item.RelativePath));
                continue;
            }

            string? found = null;
            foreach (var (from, to) in renames)
            {
                if (!item.RelativePath.StartsWith(from + "/", StringComparison.Ordinal)) continue;
                var moved = to + item.RelativePath[from.Length..];
                var there = fingerprintOf(moved);
                if (there != null && (item.Fingerprint == null || item.Fingerprint.Matches(there)))
                {
                    found = moved;
                    break;
                }
            }

            if (found == null && item.Fingerprint is { } wanted && bySize.TryGetValue(wanted.Size, out var sameSize))
            {
                foreach (var candidate in sameSize.Where(p => !claimed.Contains(p)).Take(maxCandidatesPerItem))
                {
                    var there = fingerprintOf(candidate);
                    // 只有大小、没有哈希的指纹不足以认领别处的文件：同样大小的文件太常见
                    if (there != null && wanted.Sha256 != null && wanted.Matches(there))
                    {
                        found = candidate;
                        break;
                    }
                }
            }

            if (found != null)
            {
                claimed.Add(found);
                result.Add(item with { RelativePath = found, Lost = false });
                report.Add(new PolisItemRecovery(item.Id, PolisItemRecoveryKind.Moved, item.RelativePath, found));
                continue;
            }

            result.Add(item.Lost ? item : item with { Lost = true });
            report.Add(new PolisItemRecovery(item.Id, PolisItemRecoveryKind.Lost, item.RelativePath, null));
        }
        return (result, report);
    }
}
