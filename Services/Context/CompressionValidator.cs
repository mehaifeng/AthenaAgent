using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Athena.UI.Services.Context;

public sealed partial class CompressionValidator : ICompressionValidator
{
    public CompressionValidationResult Validate(
        CompressionPlan plan,
        CompressionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(plan.PlanId, candidate.PlanId, StringComparison.Ordinal)
            || plan.BaseRevision != candidate.BaseRevision
            || plan.PromptVersion != candidate.PromptVersion)
            return Result(CompressionValidationStatus.Stale, 0, plan.PreCompressionEstimate, 0, [], "Candidate does not match the current plan.");
        if (string.IsNullOrWhiteSpace(candidate.Summary)
            || candidate.Summary.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            || candidate.Summary.StartsWith("[error", StringComparison.OrdinalIgnoreCase))
            return Result(CompressionValidationStatus.Empty, 0, plan.PreCompressionEstimate, 0, [], "Candidate summary is empty or an error response.");

        var summaryTokens = ConversationContext.EstimateTokens(candidate.Summary);
        if (summaryTokens > plan.TargetSummaryTokens)
            return Result(CompressionValidationStatus.OverBudget, summaryTokens, plan.PreCompressionEstimate, 0, [], "Candidate exceeds the target summary budget.");

        var compressedTokens = plan.Material.Sum(EstimateMaterialTokens);
        var postEstimate = Math.Max(0, plan.PreCompressionEstimate - compressedTokens) + summaryTokens;
        var benefit = Math.Max(0, plan.PreCompressionEstimate - postEstimate);
        var requiredBenefit = Math.Max(1_024L, (long)Math.Ceiling(plan.PreCompressionEstimate * 0.20));
        if (benefit < requiredBenefit)
            return Result(CompressionValidationStatus.InsufficientBenefit, summaryTokens, postEstimate, benefit, [], "Candidate does not provide material compression benefit.");

        var anchors = ExtractHardAnchors(plan.Material);
        var missing = anchors
            .Where(anchor => !candidate.Summary.Contains(anchor.Value, StringComparison.Ordinal))
            .ToArray();
        if (missing.Length > 0)
            return Result(CompressionValidationStatus.MissingHardAnchors, summaryTokens, postEstimate, benefit, missing, "Candidate omitted deterministic hard anchors.");

        return Result(CompressionValidationStatus.Valid, summaryTokens, postEstimate, benefit, [], string.Empty);
    }

    internal static IReadOnlyList<CompressionHardAnchor> ExtractHardAnchors(
        IReadOnlyList<CompressionMaterialMessage> material)
    {
        var anchors = new Dictionary<string, CompressionHardAnchor>(StringComparer.Ordinal);
        void Add(string kind, string? value)
        {
            value = value?.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '"', '\'');
            if (!string.IsNullOrWhiteSpace(value)) anchors.TryAdd(kind + "\u001f" + value, new CompressionHardAnchor(kind, value));
        }

        foreach (var message in material)
        {
            foreach (Match match in UrlRegex().Matches(message.Content)) Add("url", match.Value);
            foreach (Match match in PathRegex().Matches(message.Content)) Add("path", match.Value);
            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
                foreach (Match match in ErrorCodeRegex().Matches(message.Content)) Add("error", match.Value);
            if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match match in NumberRegex().Matches(message.Content)) Add("number", match.Value);
                foreach (var sentence in ConstraintRegex().Split(message.Content))
                    if (ConstraintMarkerRegex().IsMatch(sentence)) Add("constraint", sentence);
            }
            Add("tool_call_id", message.ToolCallId);
            ReadToolCalls(message.ToolCallsJson, Add);
            foreach (var attachment in message.Attachments)
            {
                Add("attachment_id", attachment.Id);
                Add("attachment_path", attachment.StoredPath);
            }
        }
        return anchors.Values.ToArray();
    }

    internal static IReadOnlyList<CompressionHardAnchor> ExtractHardAnchorsFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var anchors = new Dictionary<string, CompressionHardAnchor>(StringComparer.Ordinal);
        void Add(string kind, string? value)
        {
            value = value?.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '"', '\'');
            if (!string.IsNullOrWhiteSpace(value))
                anchors.TryAdd(kind + "\u001f" + value, new CompressionHardAnchor(kind, value));
        }

        foreach (Match match in UrlRegex().Matches(text)) Add("url", match.Value);
        foreach (Match match in PathRegex().Matches(text)) Add("path", match.Value);
        foreach (Match match in ErrorCodeRegex().Matches(text)) Add("error", match.Value);
        foreach (Match match in NumberRegex().Matches(text)) Add("number", match.Value);
        foreach (Match match in StableReferenceRegex().Matches(text)) Add("reference_id", match.Value);
        foreach (var sentence in ConstraintRegex().Split(text))
            if (ConstraintMarkerRegex().IsMatch(sentence)) Add("constraint", sentence);
        return anchors.Values.ToArray();
    }

    private static void ReadToolCalls(string? json, Action<string, string?> add)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var call in document.RootElement.EnumerateArray())
            {
                foreach (var property in call.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String) continue;
                    if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase)) add("tool_call_id", property.Value.GetString());
                    if (string.Equals(property.Name, "name", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(property.Name, "functionName", StringComparison.OrdinalIgnoreCase))
                        add("command", property.Value.GetString());
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static long EstimateMaterialTokens(CompressionMaterialMessage message)
    {
        long total = ConversationContext.EstimateTokens(message.Content)
                     + ConversationContext.EstimateTokens(message.ToolCallsJson)
                     + ConversationContext.EstimateTokens(message.ReasoningContent)
                     + 8;
        total += message.Attachments.Sum(item =>
            (long)ConversationContext.AttachmentManifestTokenCost
            + (item.Kind == AttachmentKind.Image ? ConversationContext.EstimateImageTokens(item.Width, item.Height) : 0));
        return total;
    }

    private static CompressionValidationResult Result(
        CompressionValidationStatus status,
        long summaryTokens,
        long postEstimate,
        long benefit,
        IReadOnlyList<CompressionHardAnchor> missing,
        string error) => new(status, summaryTokens, postEstimate, benefit, missing, error);

    [GeneratedRegex(@"https?://[^\s<>()\""\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?<![\w])(?:[A-Za-z]:\\|/)[^\s,;]+")]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"\b(?:E[A-Z0-9_]{2,}|HTTP\s*[45]\d{2}|[45]\d{2})\b")]
    private static partial Regex ErrorCodeRegex();

    [GeneratedRegex(@"(?<![\w])-?\d+(?:\.\d+)?(?![\w])")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"[\r\n。！？!?]+")]
    private static partial Regex ConstraintRegex();

    [GeneratedRegex(@"\b(?:must|never|required|do not)\b|必须|不得|务必|禁止", RegexOptions.IgnoreCase)]
    private static partial Regex ConstraintMarkerRegex();

    [GeneratedRegex(@"\b(?:call|tool|task|att(?:achment)?)[-_][A-Za-z0-9._:-]+\b", RegexOptions.IgnoreCase)]
    private static partial Regex StableReferenceRegex();
}
