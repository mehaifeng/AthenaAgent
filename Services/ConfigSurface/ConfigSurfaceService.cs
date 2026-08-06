using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Athena.UI.Services.ConfigSurface;

/// <summary>
/// 声明式配置面服务：视图 = ConfigFieldCatalog 的投影 + 手工摘要；修改 = 目录驱动的类型安全赋值。
/// 目录之外的任何字段（模型目录、元数据快照、布局、文件策略等）既不可见也不可改。
/// </summary>
public sealed class ConfigSurfaceService : IConfigSurfaceService
{
    private const string Redacted = "(redacted)";
    private const string EmptyValue = "(empty)";

    private readonly IOpenRouterModelMetadataCatalog? _metadataCatalog;

    public ConfigSurfaceService(IOpenRouterModelMetadataCatalog? metadataCatalog = null)
    {
        _metadataCatalog = metadataCatalog;
    }

    public IReadOnlyList<string> Sections => ConfigFieldCatalog.Sections;

    public IReadOnlyList<string> ModifiableKeys =>
        ConfigFieldCatalog.Fields.Where(f => !f.ReadOnly).Select(f => f.Key).ToArray();

    public object BuildView(AppConfig config, string? section)
    {
        var sectionName = ConfigFieldCatalog.ResolveSection(section);
        var sections = ConfigFieldCatalog.Fields
            .Where(field => sectionName == null
                            || string.Equals(field.Section, sectionName, StringComparison.Ordinal))
            .GroupBy(field => field.Section)
            .Select(group => new
            {
                name = group.Key,
                fields = group.Select(field => RenderField(config, field)).ToList()
            })
            .ToList();

        return new
        {
            sections,
            summary = BuildSummary(config)
        };
    }

    public (bool Success, string Message, object? Data) Apply(AppConfig config, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || !ConfigFieldCatalog.TryGet(key, out var field))
        {
            return (false, $"不允许修改配置项: {key}。可用键: {DescribeKeys()}", null);
        }

        if (field.ReadOnly)
        {
            return (false, $"配置项 {field.Key} 是只读的，不允许修改。", null);
        }

        var (ok, coerced, error) = Coerce(field, value);
        if (!ok)
        {
            return (false, $"值格式错误: {value} 无法转换为 {field.Key}。{error}", null);
        }

        field.Set(config, coerced);
        return (true, $"已更新 {field.Key} = {RenderValue(field.Get(config))}", new { key = field.Key, value = RenderValue(field.Get(config)), updated = true });
    }

    // —— 视图投影 ——

    private object RenderField(AppConfig config, ConfigFieldDescriptor field)
    {
        var raw = field.Get(config);
        var value = RenderValue(raw);
        return new
        {
            key = field.Key,
            type = field.Type.ToString(),
            value = field.Sensitive ? Redact(raw) : value,
            description = field.Description,
            note = BuildNote(config, field),
            allowedValues = field.AllowedValues,
            range = BuildRange(field),
            modifiable = !field.ReadOnly
        };
    }

    private static (string Min, string? Max)? BuildRange(ConfigFieldDescriptor field)
    {
        if (field.Range is { } r) return ($"{r.Min}", r.Max.HasValue ? $"{r.Max}" : null);
        if (field.NumberRange is { } n) return ($"{n.Min}", n.Max.HasValue ? $"{n.Max}" : null);
        return null;
    }

    /// <summary>枚举渲染为名字、字符串原样、列表渲染为数组；其余（bool/数字）原样。</summary>
    private static object? RenderValue(object? raw) => raw switch
    {
        null => null,
        Enum e => e.ToString(),
        string s => s,
        ICollection<string> list => list.ToList(),
        _ => raw
    };

    private static object? Redact(object? raw) => raw switch
    {
        null => null,
        string s => string.IsNullOrEmpty(s) ? EmptyValue : Redacted,
        _ => Redacted
    };

    /// <summary>角色模型字段附加当前绑定的 provider 展示名，方便模型对照 summary.providers。</summary>
    private static string? BuildNote(AppConfig config, ConfigFieldDescriptor field)
    {
        if (!field.Key.EndsWith(".Model", StringComparison.Ordinal) || field.Section != "AI")
            return null;
        var roleKey = field.Key[..^".Model".Length];
        var role = FindRole(config, roleKey);
        if (role == null) return null;
        var provider = config.AiModels.Providers.FirstOrDefault(p =>
            string.Equals(p.Id, role.ProviderId, StringComparison.Ordinal));
        return provider == null ? "(no provider bound)" : $"bound provider: {provider.DisplayName} ({provider.ProviderPreset})";
    }

    private static ModelRoleSettings? FindRole(AppConfig config, string roleKey) =>
        roleKey switch
        {
            "MainConversation" => config.AiModels.MainConversation,
            "TitleGeneration" => config.AiModels.TitleGeneration,
            "ContextCompression" => config.AiModels.ContextCompression,
            "Approval" => config.AiModels.Approval,
            "Embedding" => config.AiModels.Embedding,
            "BrowserAgent" => config.AiModels.BrowserAgent,
            "SubAgent" => config.AiModels.SubAgent,
            "KnowledgeMaintenance" => config.AiModels.KnowledgeMaintenance,
            "ImageRecognition" => config.AiModels.ImageRecognition,
            _ => null
        };

    private object BuildSummary(AppConfig config)
    {
        var catalog = _metadataCatalog?.Current;
        return new
        {
            providers = config.AiModels.Providers.Select(provider => new
            {
                id = provider.Id,
                preset = provider.ProviderPreset,
                displayName = provider.DisplayName,
                modelCount = provider.Models.Count,
                modelsRefreshedAt = provider.ModelsRefreshedAt,
                apiKeySet = !string.IsNullOrWhiteSpace(provider.ApiKey)
            }).ToList(),
            roleBindings = new[]
            {
                ("MainConversation", config.AiModels.MainConversation),
                ("TitleGeneration", config.AiModels.TitleGeneration),
                ("ContextCompression", config.AiModels.ContextCompression),
                ("Approval", config.AiModels.Approval),
                ("Embedding", config.AiModels.Embedding),
                ("BrowserAgent", config.AiModels.BrowserAgent),
                ("SubAgent", config.AiModels.SubAgent),
                ("KnowledgeMaintenance", config.AiModels.KnowledgeMaintenance),
                ("ImageRecognition", config.AiModels.ImageRecognition)
            }.Select(binding =>
            {
                var provider = config.AiModels.Providers.FirstOrDefault(p =>
                    string.Equals(p.Id, binding.Item2.ProviderId, StringComparison.Ordinal));
                return new
                {
                    role = binding.Item1,
                    providerId = binding.Item2.ProviderId,
                    provider = provider?.DisplayName,
                    model = binding.Item2.Model
                };
            }).ToList(),
            runtime = new
            {
                configSchemaVersion = config.ConfigSchemaVersion,
                openRouterCatalogModelCount = catalog?.Models.Count,
                openRouterCatalogRefreshedAt = catalog?.FetchedAtUtc,
                openRouterCatalogStale = _metadataCatalog?.IsStale,
                modelMetadataProfileCount = config.AiModels.ModelMetadataProfiles.Count,
                mcpServerCount = config.McpServers.Count,
                mcpServers = config.McpServers.Select(server => new { server.Name, server.Transport, server.Enabled }).ToList(),
                disabledSkillCount = config.DisabledSkillKeys.Count
            }
        };
    }

    // —— 修改应用 ——

    private static (bool Ok, object? Value, string? Error) Coerce(ConfigFieldDescriptor field, string raw)
    {
        var text = raw.Trim();
        switch (field.Type)
        {
            case ConfigFieldType.Boolean:
                return bool.TryParse(text, out var boolean)
                    ? (true, boolean, null)
                    : (false, null, "预期 true 或 false。");

            case ConfigFieldType.Integer:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    return (false, null, "预期整数。");
                if (integer > int.MaxValue || integer < int.MinValue)
                    return (false, null, "超出整数范围。");
                if (!InRange(field.Range, integer))
                    return (false, null, $"取值范围: {DescribeRange(field.Range)}。");
                return (true, (int)integer, null);

            case ConfigFieldType.Long:
                return CoerceLong(field, text);

            case ConfigFieldType.NullableLong:
                if (text.Length == 0 || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
                    return (true, null, null);
                return CoerceLong(field, text);

            case ConfigFieldType.Number:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    return (false, null, "预期数字。");
                if (field.NumberRange is { } numberRange
                    && (number < numberRange.Min || (numberRange.Max.HasValue && number > numberRange.Max.Value)))
                    return (false, null, $"取值范围: {DescribeNumberRange(field.NumberRange)}。");
                return (true, number, null);

            case ConfigFieldType.String:
                if (field.AllowedValues is { Count: > 0 } allowed
                    && allowed.All(value => !string.Equals(value, text, StringComparison.OrdinalIgnoreCase)))
                    return (false, null, $"合法取值: {string.Join(", ", allowed)}。");
                return (true, text, null);

            case ConfigFieldType.Enum:
                if (field.AllowedValues is not { Count: > 0 } enumValues
                    || enumValues.All(value => !string.Equals(value, text, StringComparison.OrdinalIgnoreCase)))
                    return (false, null, $"合法取值: {string.Join(", ", field.AllowedValues ?? [])}。");
                return (true, text, null);

            case ConfigFieldType.StringList:
                var items = ParseStringList(text);
                if (items == null)
                    return (false, null, "预期 JSON 字符串数组或逗号分隔列表。");
                return (true, items, null);

            default:
                return (false, null, $"未知字段类型: {field.Type}。");
        }
    }

    private static List<string>? ParseStringList(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return [];
        try
        {
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(trimmed);
                if (parsed == null) return null;
                return parsed.Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return trimmed.Split(',')
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToList();
    }

    private static (bool Ok, object? Value, string? Error) CoerceLong(ConfigFieldDescriptor field, string text)
    {
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
            return (false, null, "预期整数。");
        if (!InRange(field.Range, longValue))
            return (false, null, $"取值范围: {DescribeRange(field.Range)}。");
        return (true, longValue, null);
    }

    private static bool InRange((long Min, long? Max)? range, long value) =>
        !range.HasValue || (value >= range.Value.Min && (!range.Value.Max.HasValue || value <= range.Value.Max.Value));

    private static string DescribeRange((long Min, long? Max)? range) =>
        range is { } r ? $"{r.Min}..{r.Max?.ToString() ?? "∞"}" : "(无限制)";

    private static string DescribeNumberRange((double Min, double? Max)? range) =>
        range is { } r ? $"{r.Min}..{r.Max?.ToString(CultureInfo.InvariantCulture) ?? "∞"}" : "(无限制)";

    private static string DescribeKeys() =>
        string.Join(", ", ConfigFieldCatalog.Fields.Select(field => field.Key));
}
