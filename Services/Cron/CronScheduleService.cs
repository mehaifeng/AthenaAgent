using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Cronos;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Athena.UI.Services.Cron;

/// <summary>
/// 标准五段 cron 的求值实现。
///
/// 两条不可动摇的规则：
/// 1. 只接受恰好五段（分 时 日 月 周）。六段（含秒）会被拒绝——触发引擎按整分钟检查，
///    接受秒字段等于承诺一件做不到的事。同理拒绝 "@daily" 这类宏，保持"五段"是字面规则。
/// 2. 下一次触发严格晚于给定时刻（Cronos 的 inclusive 默认为 false，正是所需语义）。
///    如果这里改成 inclusive:true，一次触发会在同一分钟内被反复领取。
///
/// DST 采用 Cronos 官方语义，不自行发明策略（headless/archive 测试把行为钉死）：
/// - 春季缺失时刻（如 America/New_York 3 月某日 02:00 不存在）：在时钟跳变的瞬间触发，不跳过。
/// - 秋季重复时刻（01:30 出现两次）：固定时刻表达式只触发一次（夏令时那次）。
/// - 间隔型表达式（如 "0 * * * *"）在重复的那个小时里两次都触发。
/// </summary>
public sealed class CronScheduleService : ICronScheduleService
{
    private const int MaxPreviewCount = 50;

    private readonly ILocalizationService? _localizationService;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, CronExpression> _parseCache = new(StringComparer.Ordinal);

    public CronScheduleService(ILocalizationService? localizationService = null, ILogger? logger = null)
    {
        _localizationService = localizationService;
        _logger = logger;
    }

    public CronValidationResult Validate(string? expression, string? timeZoneId)
    {
        var result = new CronValidationResult();

        var normalizedExpression = NormalizeExpression(expression);
        if (string.IsNullOrEmpty(normalizedExpression))
        {
            result.AddIssue("missing_cron_expression", "A cron expression is required. Use five fields: minute hour day-of-month month day-of-week.");
        }
        else
        {
            var fieldCount = normalizedExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (normalizedExpression.StartsWith('@'))
            {
                result.AddIssue(
                    "macro_not_supported",
                    $"Cron macros such as '{normalizedExpression}' are not supported. Write the five explicit fields instead (for example \"0 0 * * *\" instead of \"@daily\").");
            }
            else if (fieldCount == 6)
            {
                result.AddIssue(
                    "seconds_not_supported",
                    "Six-field cron expressions (with seconds) are rejected: the scheduler checks once per whole minute, so a seconds field cannot be honoured. Drop the seconds field and use five fields.");
            }
            else if (fieldCount != 5)
            {
                result.AddIssue(
                    "invalid_field_count",
                    $"A cron expression must have exactly 5 fields (minute hour day-of-month month day-of-week); got {fieldCount}.");
            }
            else if (!TryParse(normalizedExpression, out _, out var parseError))
            {
                result.AddIssue("invalid_cron_expression", parseError ?? "The cron expression could not be parsed.");
            }
            else
            {
                result.NormalizedExpression = normalizedExpression;
            }
        }

        var normalizedZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? TimeZoneInfo.Local.Id : timeZoneId.Trim();
        var zone = ResolveTimeZone(normalizedZoneId);
        if (zone == null)
        {
            result.AddIssue("invalid_time_zone", $"Unknown time zone '{normalizedZoneId}'. Use an IANA id such as 'Asia/Shanghai' or a Windows id such as 'China Standard Time'.");
        }
        else
        {
            result.NormalizedTimeZoneId = zone.Id;
        }

        if (!result.IsValid)
        {
            result.NormalizedExpression = null;
            result.NormalizedTimeZoneId = null;
        }

        return result;
    }

    public DateTimeOffset? GetNextOccurrence(string expression, string timeZoneId, DateTimeOffset after)
    {
        var normalized = NormalizeExpression(expression);
        if (string.IsNullOrEmpty(normalized)) return null;
        if (!TryParse(normalized, out var parsed, out _) || parsed == null) return null;

        var zone = ResolveTimeZone(timeZoneId);
        if (zone == null) return null;

        try
        {
            // inclusive 保持默认的 false：必须严格晚于 after，否则同一次触发会被重复领取。
            var next = parsed.GetNextOccurrence(after, zone);
            return next?.ToUniversalTime();
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "Failed to compute next cron occurrence for {Expression} in {TimeZone}", normalized, timeZoneId);
            return null;
        }
    }

    public IReadOnlyList<DateTimeOffset> Preview(string expression, string timeZoneId, DateTimeOffset after, int count = 5)
    {
        if (count <= 0) return Array.Empty<DateTimeOffset>();
        count = Math.Min(count, MaxPreviewCount);

        var occurrences = new List<DateTimeOffset>(count);
        var cursor = after;
        for (var i = 0; i < count; i++)
        {
            var next = GetNextOccurrence(expression, timeZoneId, cursor);
            if (next == null) break;
            occurrences.Add(next.Value);
            cursor = next.Value;
        }

        return occurrences;
    }

    public string Describe(string? expression)
    {
        var normalized = NormalizeExpression(expression);
        if (string.IsNullOrEmpty(normalized)) return L("Cron.Describe.Unset", "Not scheduled");

        var fields = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return normalized;

        var (minute, hour, dayOfMonth, month, dayOfWeek) = (fields[0], fields[1], fields[2], fields[3], fields[4]);

        // 每 N 分钟：*/N * * * *
        if (IsStep(minute, out var minuteStep) && hour == "*" && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            return minuteStep == 1
                ? L("Cron.Describe.EveryMinute", "Every minute")
                : string.Format(CultureInfo.CurrentCulture, L("Cron.Describe.EveryNMinutes", "Every {0} minutes"), minuteStep);
        }

        // 每 N 小时的第 m 分钟：m */N * * *
        if (IsFixedNumber(minute, out var fixedMinute) && IsStep(hour, out var hourStep)
            && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            return hourStep == 1
                ? string.Format(CultureInfo.CurrentCulture, L("Cron.Describe.HourlyAtMinute", "Hourly at minute {0}"), fixedMinute)
                : string.Format(CultureInfo.CurrentCulture, L("Cron.Describe.EveryNHours", "Every {0} hours at minute {1}"), hourStep, fixedMinute);
        }

        // 每小时的第 m 分钟：m * * * *
        if (IsFixedNumber(minute, out var everyHourMinute) && hour == "*" && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            return string.Format(CultureInfo.CurrentCulture, L("Cron.Describe.HourlyAtMinute", "Hourly at minute {0}"), everyHourMinute);
        }

        if (!IsFixedNumber(minute, out var atMinute) || !IsFixedNumber(hour, out var atHour))
        {
            return normalized;
        }

        var clock = $"{atHour:D2}:{atMinute:D2}";

        // 每天：m h * * *
        if (dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            return string.Format(CultureInfo.CurrentCulture, L("Cron.Describe.Daily", "Every day at {0}"), clock);
        }

        // 按星期：m h * * <dow>
        if (dayOfMonth == "*" && month == "*" && dayOfWeek != "*")
        {
            var days = ParseDayOfWeekField(dayOfWeek);
            if (days == null) return normalized;

            if (days.SequenceEqual(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }))
            {
                return string.Format(CultureInfo.CurrentCulture, L("Cron.Describe.Weekdays", "Weekdays at {0}"), clock);
            }

            if (days.SequenceEqual(new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }))
            {
                return string.Format(CultureInfo.CurrentCulture, L("Cron.Describe.Weekends", "Weekends at {0}"), clock);
            }

            var dayNames = string.Join(L("Cron.Describe.DaySeparator", ", "), days.Select(DayName));
            return string.Format(CultureInfo.CurrentCulture, L("Cron.Describe.WeeklyOn", "Every {0} at {1}"), dayNames, clock);
        }

        // 每月某日：m h d * *
        if (IsFixedNumber(dayOfMonth, out var monthDay) && month == "*" && dayOfWeek == "*")
        {
            return string.Format(CultureInfo.CurrentCulture, L("Cron.Describe.MonthlyOnDay", "Day {0} of every month at {1}"), monthDay, clock);
        }

        return normalized;
    }

    public string FormatInZone(DateTimeOffset instant, string? timeZoneId, string format = "yyyy-MM-dd HH:mm")
    {
        var zone = ResolveTimeZone(timeZoneId) ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(instant.ToUniversalTime(), zone);
        return local.ToString(format, CultureInfo.CurrentCulture);
    }

    public TimeZoneInfo? ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Local;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>折叠多余空白；不做语义变换。</summary>
    public static string NormalizeExpression(string? expression)
        => string.IsNullOrWhiteSpace(expression)
            ? string.Empty
            : string.Join(' ', expression.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private bool TryParse(string normalizedExpression, out CronExpression? parsed, out string? error)
    {
        if (_parseCache.TryGetValue(normalizedExpression, out var cached))
        {
            parsed = cached;
            error = null;
            return true;
        }

        try
        {
            // CronFormat.Standard 是五段；六段输入在这里天然被拒（上层已给出更具体的说明）。
            var expression = CronExpression.Parse(normalizedExpression, CronFormat.Standard);
            _parseCache[normalizedExpression] = expression;
            parsed = expression;
            error = null;
            return true;
        }
        catch (CronFormatException ex)
        {
            parsed = null;
            error = ex.Message;
            return false;
        }
    }

    private static bool IsFixedNumber(string field, out int value)
        => int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool IsStep(string field, out int step)
    {
        step = 0;
        if (field == "*") { step = 1; return true; }
        if (!field.StartsWith("*/", StringComparison.Ordinal)) return false;
        return int.TryParse(field[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out step) && step > 0;
    }

    /// <summary>解析 day-of-week 字段（支持 0-7、逗号列表与连续区间）；无法解析时返回 null。</summary>
    private static List<DayOfWeek>? ParseDayOfWeekField(string field)
    {
        var days = new SortedSet<int>();
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Contains('-', StringComparison.Ordinal))
            {
                var bounds = part.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (bounds.Length != 2) return null;
                if (!TryParseDayToken(bounds[0], out var from) || !TryParseDayToken(bounds[1], out var to)) return null;
                if (from > to) return null;
                for (var day = from; day <= to; day++) days.Add(day % 7);
            }
            else
            {
                if (!TryParseDayToken(part, out var day)) return null;
                days.Add(day % 7);
            }
        }

        if (days.Count == 0) return null;

        // 以周一为起点排序，让 "1-5" 读作"周一到周五"而不是"周日, 周一…"。
        return days
            .Select(day => (DayOfWeek)day)
            .OrderBy(day => day == DayOfWeek.Sunday ? 7 : (int)day)
            .ToList();
    }

    private static bool TryParseDayToken(string token, out int day)
    {
        day = 0;
        var trimmed = token.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out day))
        {
            return day is >= 0 and <= 7;
        }

        day = trimmed.ToUpperInvariant() switch
        {
            "SUN" => 0,
            "MON" => 1,
            "TUE" => 2,
            "WED" => 3,
            "THU" => 4,
            "FRI" => 5,
            "SAT" => 6,
            _ => -1
        };

        return day >= 0;
    }

    private string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => L("Day.Monday", "Monday"),
        DayOfWeek.Tuesday => L("Day.Tuesday", "Tuesday"),
        DayOfWeek.Wednesday => L("Day.Wednesday", "Wednesday"),
        DayOfWeek.Thursday => L("Day.Thursday", "Thursday"),
        DayOfWeek.Friday => L("Day.Friday", "Friday"),
        DayOfWeek.Saturday => L("Day.Saturday", "Saturday"),
        DayOfWeek.Sunday => L("Day.Sunday", "Sunday"),
        _ => day.ToString()
    };

    private string L(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;
}
