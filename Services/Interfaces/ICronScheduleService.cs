using Athena.UI.Models;
using System;
using System.Collections.Generic;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// cron 表达式的唯一求值入口。实现内部使用 Cronos，但绝不把 Cronos 类型泄漏出去——
/// 调用方只看得到 <see cref="DateTimeOffset"/> 与字符串。
/// </summary>
public interface ICronScheduleService
{
    /// <summary>
    /// 校验标准五段 cron 表达式与时区。六段（含秒）表达式一律拒绝：触发引擎按整分钟检查，
    /// 秒级字段在这个引擎上是无法兑现的承诺。
    /// </summary>
    CronValidationResult Validate(string? expression, string? timeZoneId);

    /// <summary>
    /// 计算严格晚于 <paramref name="after"/> 的下一次触发时刻（UTC）。
    /// 表达式或时区非法时返回 null。
    /// </summary>
    DateTimeOffset? GetNextOccurrence(string expression, string timeZoneId, DateTimeOffset after);

    /// <summary>预览接下来的若干次触发时刻（UTC），供编辑器展示。</summary>
    IReadOnlyList<DateTimeOffset> Preview(string expression, string timeZoneId, DateTimeOffset after, int count = 5);

    /// <summary>把表达式翻译成当前界面语言的人话；无法归类时回显原始表达式。</summary>
    string Describe(string? expression);

    /// <summary>把 UTC 时刻按任务时区格式化为显示文本。</summary>
    string FormatInZone(DateTimeOffset instant, string? timeZoneId, string format = "yyyy-MM-dd HH:mm");

    /// <summary>解析时区 ID；失败时返回 null（不抛异常）。</summary>
    TimeZoneInfo? ResolveTimeZone(string? timeZoneId);
}
