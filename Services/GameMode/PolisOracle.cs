using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 每日神谕（设计稿 7.3）：在神庙设立"每日神谕"，背后就是一个定时任务。游戏从不自己建任务——神庙只是新开一份委托、
/// 把一段请求预填进输入框，由你按发送；雅典娜调用 <c>create_task</c> 时审批照常弹出（它是只能由人批准的那一类），
/// 那次批准就是仪式。这里只回答两件事：这座城的神谕是哪个任务，以及请求里该带什么。
/// </summary>
public static class PolisOracle
{
    /// <summary>预填请求里建议的时刻：每天早上 8 点。</summary>
    public const string DefaultCronExpression = "0 8 * * *";

    /// <summary>认任务名用的词：中文"神谕"或英文"Oracle"（两种界面语言的预填请求各用一个）。</summary>
    private static readonly string[] NameMarkers = { "神谕", "oracle" };

    public static bool IsOracleName(string? name)
        => !string.IsNullOrWhiteSpace(name) && NameMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    public static bool IsOracle(CronTask task, string? workspaceId)
    {
        ArgumentNullException.ThrowIfNull(task);
        return string.Equals(task.WorkspaceId ?? string.Empty, workspaceId ?? string.Empty, StringComparison.Ordinal)
               && IsOracleName(task.Name);
    }

    /// <summary>
    /// 这座城的神谕：同一个工作区、名字带"神谕 / Oracle"的任务；有好几个时先取启用的，再取最早设立的。
    /// 认不出时返回 null——神庙显示"尚未设立"，不会出错。
    /// </summary>
    public static CronTask? Find(IEnumerable<CronTask> tasks, string? workspaceId)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return tasks
            .Where(t => IsOracle(t, workspaceId))
            .OrderByDescending(t => t.IsEnabled)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>最近一次成功的运行（神庙里"看最近一卷"）。</summary>
    public static CronTaskRunRecord? LatestScroll(CronTask oracle)
    {
        ArgumentNullException.ThrowIfNull(oracle);
        return oracle.RecentRuns
            .Where(r => r.State == CronRunState.Succeeded && !string.IsNullOrEmpty(r.ConversationId))
            .OrderByDescending(r => r.CompletedAt ?? r.StartedAt ?? r.ScheduledFor)
            .FirstOrDefault();
    }
}
