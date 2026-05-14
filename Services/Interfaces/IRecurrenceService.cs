using System;
using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

public interface IRecurrenceService
{
    RecurrenceRule Normalize(RecurrenceRule? rule);
    RecurrenceRule Normalize(RecurrenceRuleInput? input, RecurrenceValidationResult validation);
    RecurrenceRule MigrateLegacyRule(string? legacyRecurrence, DateTime scheduleBoundary);
    RecurrenceValidationResult Validate(RecurrenceRule? rule);
    string GetSummary(RecurrenceRule? rule);
    DateTime? GetFirstTriggerTime(DateTime scheduleBoundary, RecurrenceRule? rule, DateTime now);
    DateTime? GetNextTriggerTime(DateTime scheduleBoundary, RecurrenceRule? rule, DateTime afterTime);
}
