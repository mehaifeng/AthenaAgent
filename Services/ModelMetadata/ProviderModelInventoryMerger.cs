using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.ModelMetadata;

/// <summary>Pure keyed merge for provider inventory. Profiles and role ownership stay outside inventory.</summary>
public static class ProviderModelInventoryMerger
{
    public static IReadOnlyList<ProviderModelDescriptor> Merge(
        IEnumerable<ProviderModelDescriptor> existing,
        IEnumerable<string> discoveredIds,
        IReadOnlySet<string> referencedIds,
        Func<string, ModelCapability> classify)
    {
        var existingById = existing
            .GroupBy(model => model.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var discovered = discoveredIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(id => id, StringComparer.Ordinal);
        var merged = new List<ProviderModelDescriptor>();

        foreach (var id in discovered)
        {
            if (existingById.TryGetValue(id, out var current))
            {
                current.IsAvailable = true;
                if (string.IsNullOrWhiteSpace(current.DisplayName)) current.DisplayName = id;
                if (current.Capability == ModelCapability.Unknown) current.Capability = classify(id);
                merged.Add(current);
            }
            else
            {
                merged.Add(new ProviderModelDescriptor
                {
                    Id = id,
                    DisplayName = id,
                    Capability = classify(id),
                    IsAvailable = true
                });
            }
        }

        foreach (var current in existingById.Values
                     .Where(model => model.IsManual || referencedIds.Contains(model.Id))
                     .Where(model => merged.All(candidate => !string.Equals(candidate.Id, model.Id, StringComparison.Ordinal)))
                     .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(model => model.Id, StringComparer.Ordinal))
        {
            current.IsAvailable = current.IsManual;
            merged.Add(current);
        }

        return merged;
    }
}
