using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Athena.UI.Services;

public class RecurrenceService : IRecurrenceService
{
    private readonly ILocalizationService? _localizationService;

    public RecurrenceService(ILocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
    }

    public RecurrenceRule Normalize(RecurrenceRule? rule)
    {
        if (rule == null)
        {
            return RecurrenceRule.None();
        }

        return new RecurrenceRule
        {
            Mode = rule.Mode,
            Interval = rule.Interval,
            Unit = rule.Unit,
            DaysOfWeek = rule.DaysOfWeek?
                .Distinct()
                .OrderBy(GetDayOrder)
                .ToList()
        };
    }

    public RecurrenceRule Normalize(RecurrenceRuleInput? input, RecurrenceValidationResult validation)
    {
        if (input == null || string.IsNullOrWhiteSpace(input.Mode))
        {
            return RecurrenceRule.None();
        }

        var normalized = new RecurrenceRule();
        switch (input.Mode.Trim().ToLowerInvariant())
        {
            case "none":
                normalized.Mode = RecurrenceMode.None;
                break;

            case "interval":
                normalized.Mode = RecurrenceMode.Interval;
                normalized.Interval = input.Interval;
                normalized.Unit = ParseUnit(input.Unit, validation);
                break;

            case "weekly_days":
                normalized.Mode = RecurrenceMode.WeeklyDays;
                normalized.Interval = input.Interval;
                normalized.DaysOfWeek = ParseDaysOfWeek(input.DaysOfWeek, validation);
                break;

            default:
                validation.Issues.Add(new RecurrenceValidationIssue
                {
                    Code = "unsupported_mode",
                    Message = $"Unsupported recurrence mode '{input.Mode}'. Supported modes: none, interval, weekly_days."
                });
                normalized.Mode = RecurrenceMode.None;
                break;
        }

        return Normalize(normalized);
    }

    public RecurrenceRule MigrateLegacyRule(string? legacyRecurrence, DateTime scheduleBoundary)
    {
        var value = (legacyRecurrence ?? "none").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value) || value == "none")
        {
            return RecurrenceRule.None();
        }

        if (value is "daily" or "every day")
        {
            return new RecurrenceRule
            {
                Mode = RecurrenceMode.Interval,
                Interval = 1,
                Unit = RecurrenceUnit.Day
            };
        }

        if (value is "weekly" or "every week")
        {
            return new RecurrenceRule
            {
                Mode = RecurrenceMode.WeeklyDays,
                Interval = 1,
                DaysOfWeek = [scheduleBoundary.DayOfWeek]
            };
        }

        if (value.StartsWith("every ", StringComparison.Ordinal))
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval))
            {
                var unitValue = parts[2].TrimEnd('s');
                if (Enum.TryParse<RecurrenceUnit>(unitValue, true, out var unit))
                {
                    return new RecurrenceRule
                    {
                        Mode = RecurrenceMode.Interval,
                        Interval = interval,
                        Unit = unit
                    };
                }
            }
        }

        return RecurrenceRule.None();
    }

    public RecurrenceValidationResult Validate(RecurrenceRule? rule)
    {
        var validation = new RecurrenceValidationResult();
        var normalized = Normalize(rule);

        switch (normalized.Mode)
        {
            case RecurrenceMode.None:
                return validation;

            case RecurrenceMode.Interval:
                if (!normalized.Interval.HasValue || normalized.Interval.Value <= 0)
                {
                    validation.Issues.Add(new RecurrenceValidationIssue
                    {
                        Code = "invalid_interval",
                        Message = "Interval must be a positive integer."
                    });
                }

                if (!normalized.Unit.HasValue)
                {
                    validation.Issues.Add(new RecurrenceValidationIssue
                    {
                        Code = "missing_unit",
                        Message = "Interval recurrence requires a unit of minute, hour, day, or week."
                    });
                }
                break;

            case RecurrenceMode.WeeklyDays:
                if (!normalized.Interval.HasValue || normalized.Interval.Value <= 0)
                {
                    validation.Issues.Add(new RecurrenceValidationIssue
                    {
                        Code = "invalid_interval",
                        Message = "Weekly day recurrence requires a positive week interval."
                    });
                }

                if (normalized.DaysOfWeek == null || normalized.DaysOfWeek.Count == 0)
                {
                    validation.Issues.Add(new RecurrenceValidationIssue
                    {
                        Code = "missing_days_of_week",
                        Message = "Select at least one weekday for weekly day recurrence."
                    });
                }
                break;
        }

        return validation;
    }

    public string GetSummary(RecurrenceRule? rule)
    {
        var normalized = Normalize(rule);
        if (normalized.Mode == RecurrenceMode.None)
        {
            return GetString("Recurrence.NoneDisplay", "Once");
        }

        if (normalized.Mode == RecurrenceMode.Interval)
        {
            var interval = normalized.Interval ?? 1;
            var unit = normalized.Unit ?? RecurrenceUnit.Day;

            if (interval == 1)
            {
                return unit switch
                {
                    RecurrenceUnit.Minute => GetString("Recurrence.EveryMinute", "Every minute"),
                    RecurrenceUnit.Hour => GetString("Recurrence.EveryHour", "Every hour"),
                    RecurrenceUnit.Day => GetString("Recurrence.EveryDay", "Every day"),
                    RecurrenceUnit.Week => GetString("Recurrence.EveryWeek", "Every week"),
                    _ => GetString("Recurrence.NoneDisplay", "Once")
                };
            }

            return string.Format(
                GetString("Recurrence.EveryNUnits", "Every {0} {1}"),
                interval,
                GetUnitLabel(unit, interval));
        }

        var days = normalized.DaysOfWeek ?? [];
        if (normalized.Interval == 1 && IsWeekdaySet(days))
        {
            return GetString("Recurrence.WorkdaysDisplay", "Weekdays");
        }

        var dayList = string.Join(", ", days.OrderBy(GetDayOrder).Select(GetDayLabel));
        if ((normalized.Interval ?? 1) == 1)
        {
            return string.Format(
                GetString("Recurrence.WeeklyDaysEveryWeek", "Every week on {0}"),
                dayList);
        }

        return string.Format(
            GetString("Recurrence.WeeklyDaysEveryNWeeks", "Every {0} weeks on {1}"),
            normalized.Interval ?? 1,
            dayList);
    }

    public DateTime? GetFirstTriggerTime(DateTime scheduleBoundary, RecurrenceRule? rule, DateTime now)
    {
        return GetNextOccurrence(scheduleBoundary, Normalize(rule), now);
    }

    public DateTime? GetNextTriggerTime(DateTime scheduleBoundary, RecurrenceRule? rule, DateTime afterTime)
    {
        return GetNextOccurrence(scheduleBoundary, Normalize(rule), afterTime);
    }

    private DateTime? GetNextOccurrence(DateTime scheduleBoundary, RecurrenceRule rule, DateTime afterTime)
    {
        return rule.Mode switch
        {
            RecurrenceMode.None => scheduleBoundary > afterTime ? scheduleBoundary : null,
            RecurrenceMode.Interval => GetNextIntervalOccurrence(scheduleBoundary, rule, afterTime),
            RecurrenceMode.WeeklyDays => GetNextWeeklyDaysOccurrence(scheduleBoundary, rule, afterTime),
            _ => null
        };
    }

    private DateTime? GetNextIntervalOccurrence(DateTime scheduleBoundary, RecurrenceRule rule, DateTime afterTime)
    {
        if (!rule.Interval.HasValue || !rule.Unit.HasValue || rule.Interval.Value <= 0)
        {
            return null;
        }

        if (scheduleBoundary > afterTime)
        {
            return scheduleBoundary;
        }

        var stepTicks = GetIntervalTicks(rule.Interval.Value, rule.Unit.Value);
        if (stepTicks <= 0)
        {
            return null;
        }

        var elapsedTicks = afterTime.Ticks - scheduleBoundary.Ticks;
        var increments = (elapsedTicks / stepTicks) + 1;
        return scheduleBoundary.AddTicks(increments * stepTicks);
    }

    private DateTime? GetNextWeeklyDaysOccurrence(DateTime scheduleBoundary, RecurrenceRule rule, DateTime afterTime)
    {
        if (!rule.Interval.HasValue || rule.Interval.Value <= 0 || rule.DaysOfWeek == null || rule.DaysOfWeek.Count == 0)
        {
            return null;
        }

        var searchAfter = afterTime;
        var weekStart = StartOfWeek(scheduleBoundary.Date, DayOfWeek.Monday);
        var timeOfDay = scheduleBoundary.TimeOfDay;
        var cycleDays = rule.Interval.Value * 7;
        var searchDate = (searchAfter > scheduleBoundary ? searchAfter : scheduleBoundary).Date;
        var daysSinceStart = (int)(searchDate - weekStart).TotalDays;
        var cycleIndex = Math.Max(0, daysSinceStart / cycleDays);
        var orderedDays = rule.DaysOfWeek.OrderBy(GetDayOrder).ToList();

        for (var i = 0; i < 5200; i++)
        {
            var cycleWeekStart = weekStart.AddDays((cycleIndex + i) * cycleDays);
            foreach (var day in orderedDays)
            {
                var candidate = cycleWeekStart.AddDays(GetDayOffset(day)).Add(timeOfDay);
                if (candidate < scheduleBoundary)
                {
                    continue;
                }

                if (candidate > searchAfter)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static long GetIntervalTicks(int interval, RecurrenceUnit unit)
    {
        return unit switch
        {
            RecurrenceUnit.Minute => TimeSpan.FromMinutes(interval).Ticks,
            RecurrenceUnit.Hour => TimeSpan.FromHours(interval).Ticks,
            RecurrenceUnit.Day => TimeSpan.FromDays(interval).Ticks,
            RecurrenceUnit.Week => TimeSpan.FromDays(interval * 7d).Ticks,
            _ => 0
        };
    }

    private static DateTime StartOfWeek(DateTime date, DayOfWeek startOfWeek)
    {
        var diff = (7 + (date.DayOfWeek - startOfWeek)) % 7;
        return date.AddDays(-diff);
    }

    private static int GetDayOffset(DayOfWeek day)
    {
        return day == DayOfWeek.Sunday ? 6 : (int)day - 1;
    }

    private static int GetDayOrder(DayOfWeek day)
    {
        return day == DayOfWeek.Sunday ? 7 : (int)day;
    }

    private static bool IsWeekdaySet(IReadOnlyCollection<DayOfWeek> days)
    {
        return days.Count == 5
            && days.Contains(DayOfWeek.Monday)
            && days.Contains(DayOfWeek.Tuesday)
            && days.Contains(DayOfWeek.Wednesday)
            && days.Contains(DayOfWeek.Thursday)
            && days.Contains(DayOfWeek.Friday);
    }

    private RecurrenceUnit? ParseUnit(string? value, RecurrenceValidationResult validation)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            validation.Issues.Add(new RecurrenceValidationIssue
            {
                Code = "missing_unit",
                Message = "Recurrence unit is required for interval mode."
            });
            return null;
        }

        if (Enum.TryParse<RecurrenceUnit>(value, true, out var unit))
        {
            return unit;
        }

        validation.Issues.Add(new RecurrenceValidationIssue
        {
            Code = "unsupported_unit",
            Message = $"Unsupported recurrence unit '{value}'. Supported units: minute, hour, day, week."
        });
        return null;
    }

    private List<DayOfWeek> ParseDaysOfWeek(List<string>? values, RecurrenceValidationResult validation)
    {
        var result = new List<DayOfWeek>();
        if (values == null)
        {
            return result;
        }

        foreach (var value in values)
        {
            if (Enum.TryParse<DayOfWeek>(value, true, out var day))
            {
                result.Add(day);
            }
            else
            {
                validation.Issues.Add(new RecurrenceValidationIssue
                {
                    Code = "unsupported_day_of_week",
                    Message = $"Unsupported dayOfWeek '{value}'. Use English DayOfWeek names such as Monday or Friday."
                });
            }
        }

        return result
            .Distinct()
            .OrderBy(GetDayOrder)
            .ToList();
    }

    private string GetUnitLabel(RecurrenceUnit unit, int interval)
    {
        return (unit, interval == 1) switch
        {
            (RecurrenceUnit.Minute, true) => GetString("Recurrence.Unit.Minute", "minute"),
            (RecurrenceUnit.Minute, false) => GetString("Recurrence.Unit.Minutes", "minutes"),
            (RecurrenceUnit.Hour, true) => GetString("Recurrence.Unit.Hour", "hour"),
            (RecurrenceUnit.Hour, false) => GetString("Recurrence.Unit.Hours", "hours"),
            (RecurrenceUnit.Day, true) => GetString("Recurrence.Unit.Day", "day"),
            (RecurrenceUnit.Day, false) => GetString("Recurrence.Unit.Days", "days"),
            (RecurrenceUnit.Week, true) => GetString("Recurrence.Unit.Week", "week"),
            (RecurrenceUnit.Week, false) => GetString("Recurrence.Unit.Weeks", "weeks"),
            _ => GetString("Recurrence.Unit.Day", "day")
        };
    }

    private string GetDayLabel(DayOfWeek day)
    {
        return day switch
        {
            DayOfWeek.Monday => GetString("Day.Monday", "Monday"),
            DayOfWeek.Tuesday => GetString("Day.Tuesday", "Tuesday"),
            DayOfWeek.Wednesday => GetString("Day.Wednesday", "Wednesday"),
            DayOfWeek.Thursday => GetString("Day.Thursday", "Thursday"),
            DayOfWeek.Friday => GetString("Day.Friday", "Friday"),
            DayOfWeek.Saturday => GetString("Day.Saturday", "Saturday"),
            DayOfWeek.Sunday => GetString("Day.Sunday", "Sunday"),
            _ => day.ToString()
        };
    }

    private string GetString(string key, string defaultValue)
    {
        return _localizationService?.GetString(key, defaultValue) ?? defaultValue;
    }
}
