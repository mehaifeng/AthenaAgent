using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Athena.UI.Services.ModelMetadata;

/// <summary>
/// 从 <c>GET /v1/models</c> 的响应里读出供应商自报的能力元数据。
///
/// 纯函数，不认识任何具体端点：OpenAI 协议只要求 id/object/created/owned_by，
/// 多出来的字段各家爱报不报，所以这里的规则是「认得就取，认不得就留空」，
/// 任何一个字段缺失都不影响其余字段，也永远不会让整个列表拉取失败。
///
/// 三条实测约束，都来自 OrcaRouter 真实响应（2026-09-15，196 个模型）：
/// - <c>architecture.output_modalities</c> 会是 <c>null</c>（196 个里 43 个），
///   不是缺键也不是空数组，所以必须显式区分 <see cref="JsonValueKind.Null"/>；
/// - <c>created</c> 是常量占位（191 个都是 1626777600），这里完全不读它；
/// - <c>context_length</c> / <c>max_completion_tokens</c> 也可能只出现在
///   <c>top_provider</c> 里（OpenRouter 形状），所以顶层缺失时回落到那里取。
/// </summary>
public static class ProviderReportedMetadataParser
{
    /// <summary>
    /// 解析整个 <c>data</c> 数组，返回按模型 ID 索引的自报元数据。
    /// 只登记真正报了内容的模型——空壳记录会让上层误以为"供应商说它什么都没有"。
    /// </summary>
    public static Dictionary<string, ProviderReportedModelMetadata> ParseCatalog(JsonElement root)
    {
        var reported = new Dictionary<string, ProviderReportedModelMetadata>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return reported;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (ReadId(item) is not { Length: > 0 } id) continue;
            if (ParseModel(item) is { } metadata) reported[id] = metadata;
        }

        return reported;
    }

    /// <summary>读取单条模型记录的 id；不是字符串或为空时返回 null。</summary>
    public static string? ReadId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        return item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
    }

    /// <summary>解析单条模型记录；一个可用字段都没有时返回 null。</summary>
    public static ProviderReportedModelMetadata? ParseModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var topProvider = item.TryGetProperty("top_provider", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : default;

        var metadata = new ProviderReportedModelMetadata
        {
            ContextLength = ReadPositiveInt64(item, "context_length") ?? ReadPositiveInt64(topProvider, "context_length"),
            MaxCompletionTokens = ReadPositiveInt64(item, "max_completion_tokens") ?? ReadPositiveInt64(topProvider, "max_completion_tokens"),
            SupportedEndpointTypes = ReadStringList(item, "supported_endpoint_types")
        };

        if (item.TryGetProperty("architecture", out var architecture) && architecture.ValueKind == JsonValueKind.Object)
        {
            metadata.InputModalities = ReadStringList(architecture, "input_modalities");
            metadata.OutputModalities = ReadStringList(architecture, "output_modalities");
        }

        return metadata.HasAnyValue ? metadata : null;
    }

    /// <summary>
    /// 读一个正整数。非正值一律当成"没报"：0 和负数在这里只可能是占位，
    /// 而把 0 当成真实的上下文窗口会让整条会话立刻判定超限。
    /// 数字被引号包起来的端点也能读（各家 JSON 类型并不统一）。
    /// </summary>
    private static long? ReadPositiveInt64(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var value)) return null;

        var parsed = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text) => text,
            _ => (long?)null
        };

        return parsed is > 0 ? parsed : null;
    }

    /// <summary>
    /// 读一个字符串数组。缺键、<c>null</c>、空数组一律返回 null——
    /// 三者对上层是同一件事（这个维度没有信息），而 <c>null</c> 是实测中最常见的那一种。
    /// </summary>
    private static List<string>? ReadStringList(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object
            || !owner.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = new List<string>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } text) items.Add(text);
        }

        return items.Count > 0 ? items : null;
    }
}
