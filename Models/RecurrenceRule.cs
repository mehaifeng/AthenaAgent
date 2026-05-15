using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Athena.UI.Models;

[JsonConverter(typeof(JsonStringEnumConverter<RecurrenceMode>))]
public enum RecurrenceMode
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("interval")]
    Interval,

    [JsonStringEnumMemberName("weekly_days")]
    WeeklyDays
}

[JsonConverter(typeof(JsonStringEnumConverter<RecurrenceUnit>))]
public enum RecurrenceUnit
{
    [JsonStringEnumMemberName("minute")]
    Minute,

    [JsonStringEnumMemberName("hour")]
    Hour,

    [JsonStringEnumMemberName("day")]
    Day,

    [JsonStringEnumMemberName("week")]
    Week
}

public class RecurrenceRule
{
    [JsonPropertyName("mode")]
    public RecurrenceMode Mode { get; set; } = RecurrenceMode.None;

    [JsonPropertyName("interval")]
    public int? Interval { get; set; }

    [JsonPropertyName("unit")]
    public RecurrenceUnit? Unit { get; set; }

    [JsonPropertyName("daysOfWeek")]
    public List<DayOfWeek>? DaysOfWeek { get; set; }

    [JsonIgnore]
    public bool IsRecurring => Mode != RecurrenceMode.None;

    public static RecurrenceRule None() => new() { Mode = RecurrenceMode.None };

    public RecurrenceRule Clone()
    {
        return new RecurrenceRule
        {
            Mode = Mode,
            Interval = Interval,
            Unit = Unit,
            DaysOfWeek = DaysOfWeek?.ToList()
        };
    }
}

public class RecurrenceRuleInput
{
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("interval")]
    public int? Interval { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("daysOfWeek")]
    public List<string>? DaysOfWeek { get; set; }
}

public class RecurrenceValidationIssue
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class RecurrenceValidationResult
{
    public bool IsValid => Issues.Count == 0;
    public List<RecurrenceValidationIssue> Issues { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> SupportedRecurrencePatterns { get; } =
    [
        "once",
        "every N minutes",
        "every N hours",
        "every N days",
        "every N weeks",
        "every N weeks on specific weekdays"
    ];
}
