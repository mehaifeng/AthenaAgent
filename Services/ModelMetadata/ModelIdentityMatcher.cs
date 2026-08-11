using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Athena.UI.Services.ModelMetadata;

/// <summary>纯本地、确定性的外来模型身份匹配器。</summary>
public sealed class ModelIdentityMatcher
{
    public const int MatcherRulesVersion = 3;

    private static readonly Dictionary<string, string> PresetAuthors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenAI"] = "openai",
        ["Anthropic"] = "anthropic",
        ["Google"] = "google",
        ["Gemini"] = "google",
        ["DeepSeek"] = "deepseek",
        ["Alibaba"] = "qwen",
        ["DashScope"] = "qwen",
        ["Zhipu"] = "z-ai",
        ["Minimax"] = "minimax"
    };

    private static readonly Dictionary<string, string> HostAuthors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["api.openai.com"] = "openai",
        ["api.anthropic.com"] = "anthropic",
        ["generativelanguage.googleapis.com"] = "google",
        ["api.deepseek.com"] = "deepseek",
        ["dashscope.aliyuncs.com"] = "qwen",
        ["open.bigmodel.cn"] = "z-ai",
        ["api.minimaxi.com"] = "minimax"
    };

    private static readonly string[] ConflictFeatures =
    [
        "mini", "pro", "max", "flash", "lite", "coder", "vision", "reasoner",
        "preview", "latest", "free", "thinking", "online", "awq", "gguf", "int4"
    ];

    // Serving-tier suffixes that change latency rather than model identity/capability.
    // Keep this list deliberately narrow; architectural variants remain hard evidence.
    private static readonly string[] DeliveryOnlySuffixes = ["highspeed"];

    public ModelMatchResult Match(
        ExternalModelIdentity identity,
        OpenRouterCatalogSnapshot snapshot,
        ProviderModelMetadataProfile? profile = null,
        bool isCatalogStale = false)
    {
        if (profile?.BindingMode == ModelMetadataBindingMode.CustomOnly)
            return Result(ModelMatchStatus.CustomOnly, snapshot, isCatalogStale);

        if (profile?.BindingMode == ModelMetadataBindingMode.PinnedOpenRouter)
        {
            var pinned = snapshot.Models.FirstOrDefault(model =>
                string.Equals(model.Id, profile.PinnedOpenRouterModelId, StringComparison.Ordinal));
            return pinned == null
                ? Result(ModelMatchStatus.PinnedModelMissing, snapshot, isCatalogStale)
                : Matched(pinned, "M0", 100, snapshot, isCatalogStale);
        }

        var external = identity.ExternalModelId.Trim();
        if (external.Length == 0) return Result(ModelMatchStatus.Unmatched, snapshot, isCatalogStale);

        var layers = new (string Name, int Score, Func<OpenRouterModelMetadata, bool> Predicate)[]
        {
            ("M1", 99, model => string.Equals(external, model.Id, StringComparison.Ordinal)),
            ("M2", 98, model => !string.IsNullOrEmpty(model.CanonicalSlug) && string.Equals(external, model.CanonicalSlug, StringComparison.Ordinal)),
            ("M3", 97, model => MatchesProtocolUnwrapped(external, model)),
            ("M4", 96, model => MatchesExplicitAuthorAndSlug(external, model)),
            // Aggregating providers often expose a qualified upstream identity with
            // casing differences or prepend serving namespaces such as "Pro/" or
            // "LoRA/". Match any qualified path suffix against OpenRouter's own
            // identities, but only when the layer resolves to one catalog record.
            ("M4N", 95, model => MatchesNormalizedCatalogReference(external, model)),
            // OpenRouter preserves the upstream Hugging Face identity in raw metadata.
            // Use it to bridge upstream/OpenRouter author aliases without maintaining
            // provider- or model-specific tables.
            ("M4H", 95, model => MatchesRawUpstreamReference(external, model)),
            ("M5", 94, model => MatchesStrongHints(identity, external, model)),
            ("M6", 92, model => MatchesSafeNormalized(identity, external, model)),
            // Third-party providers commonly expose the upstream model's bare slug
            // without an OpenRouter author namespace. A normalized slug equality is
            // deterministic when it resolves to exactly one live catalog record, so
            // it belongs in the automatic layers rather than the fuzzy candidate list.
            ("M7", 91, model => MatchesUniqueBareSlugAlias(external, model)),
            ("M8", 90, model => MatchesUniqueDeliveryAlias(external, model))
        };

        foreach (var layer in layers)
        {
            var matches = snapshot.Models
                .Where(model => !IsExpired(model) && layer.Predicate(model))
                .ToList();
            if (matches.Count == 1) return Matched(matches[0], layer.Name, layer.Score, snapshot, isCatalogStale);
            if (matches.Count > 1)
            {
                return new ModelMatchResult(
                    ModelMatchStatus.Ambiguous, null, layer.Name, layer.Score, null, null, false,
                    matches.Select(model => new ModelMatchCandidate(model.Id, layer.Score, layer.Name, [])).ToList(),
                    [], snapshot.CatalogRevision, isCatalogStale, false);
            }
        }

        var candidates = new List<ModelMatchCandidate>();
        var allConflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in snapshot.Models.Where(model => !IsExpired(model)))
        {
            var conflicts = FindHardConflicts(identity, external, model);
            foreach (var conflict in conflicts) allConflicts.Add(conflict);
            if (conflicts.Count > 0) continue;
            var similarity = Similarity(external, model.Id);
            if (similarity < 0.60) continue;
            var score = (int)Math.Round(75 + 14 * (similarity - 0.60) / 0.40, MidpointRounding.AwayFromZero);
            candidates.Add(new ModelMatchCandidate(model.Id, Math.Clamp(score, 75, 89), $"Fuzzy-v{MatcherRulesVersion}", []));
        }

        candidates = candidates.OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.OpenRouterModelId, StringComparer.Ordinal)
            .Take(8).ToList();
        return new ModelMatchResult(
            candidates.Count > 1 && candidates[0].Score - candidates[1].Score < 8
                ? ModelMatchStatus.Ambiguous
                : ModelMatchStatus.Unmatched,
            null, null, candidates.FirstOrDefault()?.Score,
            candidates.Skip(1).FirstOrDefault()?.Score,
            candidates.Count > 1 ? candidates[0].Score - candidates[1].Score : null,
            false, candidates, allConflicts.ToList(), snapshot.CatalogRevision, isCatalogStale, false);
    }

    private static ModelMatchResult Matched(OpenRouterModelMetadata model, string layer, int score, OpenRouterCatalogSnapshot snapshot, bool stale) =>
        new(ModelMatchStatus.Matched, model.Id, layer, score, null, null, true,
            [new ModelMatchCandidate(model.Id, score, layer, [])], [], snapshot.CatalogRevision, stale, IsExpired(model));

    private static ModelMatchResult Result(ModelMatchStatus status, OpenRouterCatalogSnapshot snapshot, bool stale) =>
        new(status, null, null, null, null, null, false, [], [], snapshot.CatalogRevision, stale, false);

    private static bool MatchesProtocolUnwrapped(string external, OpenRouterModelMetadata model)
    {
        if (!external.StartsWith("models/", StringComparison.Ordinal)) return false;
        var value = external["models/".Length..];
        return string.Equals(value, model.Id, StringComparison.Ordinal)
            || string.Equals(value, model.CanonicalSlug, StringComparison.Ordinal);
    }

    private static bool MatchesExplicitAuthorAndSlug(string external, OpenRouterModelMetadata model)
    {
        var slash = external.IndexOf('/');
        if (slash <= 0) return false;
        var author = external[..slash];
        var slug = external[(slash + 1)..];
        return TrySplitModel(model, out var modelAuthor, out var modelSlug)
            && string.Equals(author, modelAuthor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(slug, modelSlug, StringComparison.Ordinal);
    }

    private static bool MatchesNormalizedCatalogReference(string external, OpenRouterModelMetadata model)
    {
        return MatchesQualifiedReference(external, model.Id)
            || MatchesQualifiedReference(external, model.CanonicalSlug);
    }

    private static bool MatchesRawUpstreamReference(string external, OpenRouterModelMetadata model)
    {
        var upstreamId = ReadRawString(model, "hugging_face_id");
        if (string.IsNullOrWhiteSpace(upstreamId)
            || (!HasRouteVariant(external) && HasRouteVariant(model.Id)))
        {
            return false;
        }
        return MatchesQualifiedReference(external, upstreamId);
    }

    private static bool MatchesQualifiedReference(string external, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || !reference.Contains('/')) return false;
        var normalizedReference = Normalize(reference);
        return EnumerateQualifiedIdentitySuffixes(external)
            .Any(candidate => string.Equals(Normalize(candidate), normalizedReference, StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateQualifiedIdentitySuffixes(string external)
    {
        var segments = UnwrapProtocolPrefix(external)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Keep at least author/slug. Dropping down to a bare leaf would erase hard
        // author evidence and is already handled separately by M7.
        for (var index = 0; index <= segments.Length - 2; index++)
        {
            yield return string.Join('/', segments[index..]);
        }
    }

    private static string? ReadRawString(OpenRouterModelMetadata model, string propertyName)
    {
        if (model.Raw is not { } raw || raw.ValueKind != System.Text.Json.JsonValueKind.Object
            || !raw.TryGetProperty(propertyName, out var value)
            || value.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }
        return value.GetString();
    }

    private static bool HasRouteVariant(string value)
    {
        var leaf = Slug(UnwrapProtocolPrefix(value));
        return leaf.Contains(':');
    }

    private static bool MatchesStrongHints(ExternalModelIdentity identity, string external, OpenRouterModelMetadata model)
    {
        if (!TryGetStrongAuthor(identity, out var author)
            || !TrySplitModel(model, out var modelAuthor, out var modelSlug)) return false;
        return string.Equals(author, modelAuthor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(external, modelSlug, StringComparison.Ordinal);
    }

    private static bool MatchesSafeNormalized(ExternalModelIdentity identity, string external, OpenRouterModelMetadata model)
    {
        if (!TryGetStrongAuthor(identity, out var author)
            || !TrySplitModel(model, out var modelAuthor, out _)) return false;
        return string.Equals(author, modelAuthor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Normalize($"{author}/{external}"), Normalize(model.Id), StringComparison.Ordinal);
    }

    private static bool MatchesUniqueBareSlugAlias(string external, OpenRouterModelMetadata model)
    {
        var unwrapped = UnwrapProtocolPrefix(external);
        // An explicit author is evidence and must never be discarded. M4/M6 handle
        // author-qualified IDs; falling back to the leaf here could cross authors.
        if (unwrapped.Contains('/')) return false;
        if (!TrySplitModel(model, out _, out var modelSlug)) return false;

        return string.Equals(Normalize(unwrapped), Normalize(modelSlug), StringComparison.Ordinal);
    }

    private static bool MatchesUniqueDeliveryAlias(string external, OpenRouterModelMetadata model)
    {
        var unwrapped = UnwrapProtocolPrefix(external);
        if (unwrapped.Contains('/') || !TrySplitModel(model, out _, out var modelSlug)) return false;

        var normalized = Normalize(unwrapped);
        foreach (var suffix in DeliveryOnlySuffixes)
        {
            var marker = $"-{suffix}";
            if (normalized.EndsWith(marker, StringComparison.Ordinal)
                && string.Equals(normalized[..^marker.Length], Normalize(modelSlug), StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryGetStrongAuthor(ExternalModelIdentity identity, out string author)
    {
        var preset = identity.ProviderPreset != null && PresetAuthors.TryGetValue(identity.ProviderPreset, out var p) ? p : null;
        var host = identity.BaseUrlHost != null && HostAuthors.TryGetValue(identity.BaseUrlHost, out var h) ? h : null;
        if (preset != null && host != null && string.Equals(preset, host, StringComparison.OrdinalIgnoreCase))
        {
            author = preset;
            return true;
        }
        author = string.Empty;
        return false;
    }

    private static List<string> FindHardConflicts(ExternalModelIdentity identity, string external, OpenRouterModelMetadata model)
    {
        var conflicts = new List<string>();
        if (external.Contains('/') && TrySplitModel(model, out var modelAuthor, out _))
        {
            var externalAuthor = external[..external.IndexOf('/')];
            if (!string.Equals(externalAuthor, modelAuthor, StringComparison.OrdinalIgnoreCase)) conflicts.Add("author");
        }
        var a = FeatureSet(external);
        var b = FeatureSet(model.Id);
        foreach (var feature in ConflictFeatures)
        {
            if (a.Contains(feature) != b.Contains(feature) && (a.Contains(feature) || b.Contains(feature)))
                conflicts.Add(feature);
        }
        foreach (var scale in new[] { "7b", "32b", "72b" })
        {
            if (a.Contains(scale) != b.Contains(scale) && (a.Contains(scale) || b.Contains(scale))) conflicts.Add("scale");
        }
        return conflicts.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string UnwrapProtocolPrefix(string value) =>
        value.StartsWith("models/", StringComparison.Ordinal) ? value["models/".Length..] : value;

    private static double Similarity(string external, string candidate)
    {
        var a = Slug(external);
        var b = Slug(candidate);
        var aTokens = TokenSet(a);
        var bTokens = TokenSet(b);
        var union = aTokens.Union(bTokens).Count();
        var jaccard = union == 0 ? 0 : (double)aTokens.Intersect(bTokens).Count() / union;
        var edit = 1.0 - (double)Levenshtein(a, b) / Math.Max(1, Math.Max(a.Length, b.Length));
        var af = FeatureSet(a);
        var bf = FeatureSet(b);
        var featureUnion = af.Union(bf).Count();
        var feature = featureUnion == 0 ? 0.5 : (double)af.Intersect(bf).Count() / featureUnion;
        return 0.45 * jaccard + 0.35 * edit + 0.20 * feature;
    }

    private static bool TrySplitModel(OpenRouterModelMetadata model, out string author, out string slug)
    {
        var id = model.Id;
        var slash = id.IndexOf('/');
        if (slash <= 0) { author = slug = string.Empty; return false; }
        author = id[..slash];
        slug = id[(slash + 1)..];
        return true;
    }

    private static string Normalize(string value)
    {
        var normalized = value.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var separator = false;
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c) || c is ':' or '/')
            {
                builder.Append(c);
                separator = false;
            }
            else if (!separator)
            {
                builder.Append('-');
                separator = true;
            }
        }
        return builder.ToString().Trim('-');
    }

    private static string Slug(string value) => value.Contains('/') ? value[(value.LastIndexOf('/') + 1)..] : value;
    private static HashSet<string> TokenSet(string value) => Normalize(value).Split(['-', '/', ':'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    private static HashSet<string> FeatureSet(string value) => TokenSet(value);
    private static bool IsExpired(OpenRouterModelMetadata model) => model.ExpirationDate is { } expiration && expiration <= DateTimeOffset.UtcNow;

    private static int Levenshtein(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
