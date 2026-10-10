using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>传令官播报的一行是什么事。每一种在 locale 里有一句（<c>GameMode.Report.*</c> / <c>GameMode.Herald.*</c>）。</summary>
public enum PolisHeraldKind
{
    /// <summary>一个定时任务跑完了（成功）。</summary>
    CronSucceeded,
    /// <summary>一个定时任务失败了——失败始终要报，与任务的完成提示开关无关。</summary>
    CronFailed,
    /// <summary>应用关着时错过了计划时刻，按 Skip 策略记了一次跳过。</summary>
    CronSkipped,
    /// <summary>运行到一半应用退出了。</summary>
    CronInterrupted,
    /// <summary>每日神谕送来了一卷（同时作为一件成果放在你脚下）。</summary>
    OracleScroll,
    /// <summary>上次退出时还在进行的委托：停在了半路。</summary>
    CommissionInterrupted,
    BuildingAdded,
    BuildingRemoved,
    BuildingRenamed,
    BuildingGrew,
    BuildingShrank,
    BuildingTouched,
    ItemLost,
    ItemMoved,
    ItemModified
}

/// <summary>
/// 一行播报。<see cref="Subject"/> 是任务名 / 委托标题 / 文件夹名 / 藏品名；<see cref="ConversationId"/> 不为空时这一行可点，
/// 点了就选中那次会话（走会话树的同一条路径）。<see cref="ItemId"/> 是神谕送来的那一卷。
/// </summary>
public sealed record PolisHeraldEntry(
    PolisHeraldKind Kind,
    string? Subject,
    string? Detail = null,
    int Count = 0,
    string? ConversationId = null,
    DateTimeOffset? At = null,
    string? ItemId = null);

/// <summary>
/// 传令官（设计稿 7.3、10.3）：你回来时，他播报离开期间发生的事——定时任务的结果、被打断的委托、外部改动（建筑与藏品）。
/// 纯函数：只看这座城的定时任务、委托快照和离线差异报告。"离开期间"以存档里上次离开的时刻为界；第一次来没有这个时刻，
/// 就不报定时任务（奠基揭幕正在讲这座城是什么）。顺序：定时任务（新的在前）→ 被打断的委托 → 建筑 → 藏品。
/// </summary>
public static class PolisHerald
{
    /// <summary>一次最多播几条定时任务的结果：几个月没回来时，几百条记录对谁都没有用。</summary>
    public const int MaxCronLines = 6;

    public static IReadOnlyList<PolisHeraldEntry> Compose(
        IReadOnlyList<CronTask> tasks,
        string? workspaceId,
        DateTimeOffset? lastVisitedAt,
        IReadOnlyList<PolisCommission> commissions,
        PolisAwayReport? report,
        Func<string, string?> itemTitle,
        Func<CronTask, bool> isOracle)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(commissions);
        ArgumentNullException.ThrowIfNull(itemTitle);
        ArgumentNullException.ThrowIfNull(isOracle);
        var lines = new List<PolisHeraldEntry>();

        if (lastVisitedAt is { } since)
        {
            var runs = tasks
                .Where(t => string.Equals(t.WorkspaceId ?? string.Empty, workspaceId ?? string.Empty, StringComparison.Ordinal))
                .SelectMany(t => t.RecentRuns.Select(r => (Task: t, Run: r)))
                .Where(x => x.Run.IsTerminal && (x.Run.CompletedAt ?? x.Run.ScheduledFor) is { } done && done > since)
                .OrderByDescending(x => x.Run.CompletedAt ?? x.Run.ScheduledFor)
                .ThenBy(x => x.Run.RunId, StringComparer.Ordinal)
                .Take(MaxCronLines);
            foreach (var (task, run) in runs)
            {
                var at = run.CompletedAt ?? run.ScheduledFor;
                var kind = run.State switch
                {
                    CronRunState.Succeeded when isOracle(task) => PolisHeraldKind.OracleScroll,
                    CronRunState.Succeeded => PolisHeraldKind.CronSucceeded,
                    CronRunState.Failed => PolisHeraldKind.CronFailed,
                    CronRunState.Skipped => PolisHeraldKind.CronSkipped,
                    _ => PolisHeraldKind.CronInterrupted
                };
                var detail = run.State == CronRunState.Failed ? Shorten(run.Error) : null;
                lines.Add(new PolisHeraldEntry(kind, task.Name, detail, 0, run.ConversationId, at));
            }
        }

        foreach (var c in commissions
                     .Where(c => string.Equals(c.WorkspaceId ?? string.Empty, workspaceId ?? string.Empty, StringComparison.Ordinal))
                     .Where(c => c.Interrupted && !c.Running)
                     .OrderByDescending(c => c.UpdatedAt)
                     .ThenBy(c => c.ConversationId, StringComparer.Ordinal))
        {
            lines.Add(new PolisHeraldEntry(PolisHeraldKind.CommissionInterrupted, c.Title, null, 0, c.ConversationId, c.UpdatedAt));
        }

        if (report != null)
        {
            foreach (var change in report.Buildings)
            {
                var kind = change.Kind switch
                {
                    PolisBuildingChangeKind.Added => PolisHeraldKind.BuildingAdded,
                    PolisBuildingChangeKind.Removed => PolisHeraldKind.BuildingRemoved,
                    PolisBuildingChangeKind.Renamed => PolisHeraldKind.BuildingRenamed,
                    PolisBuildingChangeKind.Grew => PolisHeraldKind.BuildingGrew,
                    PolisBuildingChangeKind.Shrank => PolisHeraldKind.BuildingShrank,
                    _ => PolisHeraldKind.BuildingTouched
                };
                var count = change.Kind switch
                {
                    PolisBuildingChangeKind.Grew => change.FilesAfter - change.FilesBefore,
                    PolisBuildingChangeKind.Shrank => change.FilesBefore - change.FilesAfter,
                    _ => 0
                };
                lines.Add(new PolisHeraldEntry(kind, change.Key, change.RenamedFrom, count));
            }
            foreach (var item in report.Items)
            {
                var kind = item.Kind switch
                {
                    PolisItemRecoveryKind.Lost => PolisHeraldKind.ItemLost,
                    PolisItemRecoveryKind.Moved => PolisHeraldKind.ItemMoved,
                    PolisItemRecoveryKind.Modified => PolisHeraldKind.ItemModified,
                    _ => (PolisHeraldKind?)null
                };
                if (kind is { } k) lines.Add(new PolisHeraldEntry(k, itemTitle(item.ItemId) ?? item.ItemId, null, 0, null, null, item.ItemId));
            }
        }
        return lines;
    }

    /// <summary>失败原因只取第一行的前 60 个字：传令官说的是"出了什么事"，细节在那次会话里。</summary>
    private static string? Shorten(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var line = error.Split('\n', 2)[0].Trim();
        return line.Length <= 60 ? line : line[..59] + "…";
    }
}
