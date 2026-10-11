using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace Athena.UI.Services.GameMode;

/// <summary>一次测绘（扫描 + 核对）的全部结果：页面要画的城、要写回的存档与快照、离开期间的报告。</summary>
public sealed record PolisCityState(
    PolisFixtureDocument City,
    PolisSaveDocument Save,
    PolisIndexDocument Index,
    PolisAwayReport Report,
    PolisScanResult Scan);

/// <summary>
/// 城邦的组装（设计稿 10.3"先显示，再核对"）。两条路：
/// <list type="bullet">
/// <item><see cref="FromSnapshot"/>：只用上次的索引快照与账本画出城，零 I/O，让画面立刻可见；</item>
/// <item><see cref="Reconcile"/>：拿一次新的扫描核对差异——推断离线改名、更新账本、找回藏品、写出新的快照与报告。</item>
/// </list>
/// 都是纯函数（指纹读取由调用方注入），可以在后台线程上跑、可以取消。
/// </summary>
public static class PolisCityBuilder
{
    public static PolisFixtureDocument FromSnapshot(string workspaceName, PolisIndexDocument index, PolisSaveDocument save, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(save);
        var buildings = index.Buildings
            .Select(b => new PolisBuilding(
                b.Key,
                b.Kind,
                b.FileCount,
                b.TotalBytes,
                new Dictionary<PolisFileKind, int>(),
                b.LastModifiedUtc,
                PolisBuildings.StateFor(b.LastModifiedUtc, now),
                PolisBuildings.SizeClassFor(Math.Max(0, b.FileCount)),
                b.Incomplete))
            .ToList();
        var summary = new PolisSummary(buildings, index.Agora ?? new PolisAgoraSummary(0, 0, null, false));
        // 快照里的建筑就是上次账本里还在的那些：Update 不会给它们换地，也不会无中生有
        var layout = PolisLayoutLedger.Update(save.Ledger, buildings);
        var scan = new PolisScanResult(
            index.Buildings.Select(b => new PolisTopLevelDirectory(b.Key, b.LastModifiedUtc)).ToList(),
            Array.Empty<PolisScannedFile>(),
            new HashSet<string>(index.Buildings.Where(b => b.Incomplete).Select(b => b.Key), StringComparer.Ordinal),
            index.Truncated,
            Array.Empty<string>(),
            0,
            index.Buildings.Sum(b => b.FileCount));
        return PolisFixture.Compose(workspaceName, scan, summary, layout, replay: null, now, synthetic: false);
    }

    public static PolisCityState Reconcile(
        string workspaceName,
        PolisScanResult scan,
        PolisSaveDocument save,
        PolisIndexDocument? previousIndex,
        Func<string, PolisFingerprint?> fingerprintOf,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(fingerprintOf);

        var summary = PolisBuildings.Summarize(scan, now);
        var indexBuildings = PolisDiff.IndexBuildings(scan, summary);
        cancellationToken.ThrowIfCancellationRequested();

        var renames = previousIndex == null
            ? Array.Empty<(string, string)>()
            : PolisDiff.InferRenames(previousIndex.Buildings, indexBuildings);
        var ledger = save.Ledger;
        foreach (var (from, to) in renames) ledger = PolisLayoutLedger.Rename(ledger, from, to);
        var layout = PolisLayoutLedger.Update(ledger, summary.Buildings);
        var buildingChanges = previousIndex == null
            ? Array.Empty<PolisBuildingChange>()
            : PolisDiff.CompareBuildings(previousIndex.Buildings, indexBuildings, renames);
        cancellationToken.ThrowIfCancellationRequested();

        var (items, itemReport) = PolisDiff.ReconcileItems(save.Items, scan.Files, renames, path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return fingerprintOf(path);
        });

        var city = PolisFixture.Compose(workspaceName, scan, summary, layout, replay: null, now, synthetic: false);
        var newSave = save with { Ledger = layout.Ledger, Items = items };
        var index = new PolisIndexDocument
        {
            ScannedAtUtc = now,
            Buildings = indexBuildings,
            Agora = summary.Agora,
            Truncated = scan.Truncated,
            ItemFingerprints = items.Where(i => i.Fingerprint != null).ToDictionary(i => i.Id, i => i.Fingerprint!, StringComparer.Ordinal)
        };
        return new PolisCityState(city, newSave, index, new PolisAwayReport(buildingChanges, itemReport), scan);
    }
}

/// <summary>藏品的内容指纹：大小 + SHA-256。只读取有上限的大小，读不了就当文件不在。</summary>
public static class PolisFingerprints
{
    /// <summary>超过这个大小的文件只记大小：为了找回一件藏品把几个 G 的视频整份读一遍不值得。</summary>
    public const long MaxHashedBytes = 64L * 1024 * 1024;

    public static PolisFingerprint? Compute(string fullPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return null;
            if (info.Length > MaxHashedBytes) return new PolisFingerprint(info.Length, null);
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
            using var sha = SHA256.Create();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return new PolisFingerprint(info.Length, Convert.ToHexString(sha.Hash!).ToLowerInvariant());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 正被独占写着、没有权限：这一刻读不到，当它不在；下一次核对再试
            return null;
        }
    }

    /// <summary>按工作区相对路径取指纹的函数（<see cref="PolisCityBuilder.Reconcile"/> 用）。</summary>
    public static Func<string, PolisFingerprint?> ForWorkspace(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var comparison = PolisPaths.PlatformComparison;
        return relative =>
        {
            var full = PolisPaths.ToFull(workspaceRoot, relative, comparison);
            return full == null ? null : Compute(full, cancellationToken);
        };
    }
}
