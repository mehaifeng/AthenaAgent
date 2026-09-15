using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.ModelMetadata;

/// <summary>Pure keyed merge for provider inventory. Profiles and role ownership stay outside inventory.</summary>
public static class ProviderModelInventoryMerger
{
    /// <param name="reported">
    /// 本次拉取中供应商自报的元数据，按模型 ID 索引。<c>null</c> 表示这一轮压根没解析到
    /// 原始 JSON（走了 SDK 回退），此时已有的自报值原样保留——否则一次网络抖动就会
    /// 把整个库存的上下文窗口打回"未知"，而那恰恰是用户最不会去看的地方。
    /// 非 null 时才是权威快照：本次没报的模型会被清掉，不留过期值。
    /// </param>
    public static IReadOnlyList<ProviderModelDescriptor> Merge(
        IEnumerable<ProviderModelDescriptor> existing,
        IEnumerable<string> discoveredIds,
        IReadOnlySet<string> referencedIds,
        Func<string, ModelCapability> classify,
        IReadOnlyDictionary<string, ProviderReportedModelMetadata>? reported = null)
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
                if (reported != null) current.Reported = Lookup(reported, id);
                merged.Add(current);
            }
            else
            {
                merged.Add(new ProviderModelDescriptor
                {
                    Id = id,
                    DisplayName = id,
                    Capability = classify(id),
                    IsAvailable = true,
                    Reported = reported == null ? null : Lookup(reported, id)
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

    private static ProviderReportedModelMetadata? Lookup(
        IReadOnlyDictionary<string, ProviderReportedModelMetadata> reported,
        string id)
        => reported.TryGetValue(id, out var metadata) ? metadata : null;
}
