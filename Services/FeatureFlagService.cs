using System;
using System.IO;
using System.Text.Json;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services;

/// <summary>
/// 隐藏功能开关实现（订阅计划 Phase 0）。
/// 开关在启动时解析一次并缓存：环境变量优先，其次 <c>AthenaData/feature-flags.json</c>，默认全部关闭。
/// 该文件不经 UI 双向绑定、不进 <c>config.json</c>，避免开关状态污染用户配置。
/// </summary>
public sealed class FeatureFlagService : IFeatureFlagService
{
    private const string FlagsFileName = "feature-flags.json";
    private const string PreviewEnvVar = "ATHENA_SUBSCRIPTION_PREVIEW";

    private readonly bool _subscriptionFeatureEnabled;

    public FeatureFlagService(IPlatformPathService pathService)
    {
        _subscriptionFeatureEnabled = Resolve(pathService);
        if (_subscriptionFeatureEnabled)
        {
            Log.Information("功能开关：订阅托管模式预览已启用（SubscriptionFeatureEnabled=true）");
        }
    }

    public bool SubscriptionFeatureEnabled => _subscriptionFeatureEnabled;

    private static bool Resolve(IPlatformPathService pathService)
    {
        // 1) 环境变量优先（CI / 开发者本机快速切换）。
        var env = Environment.GetEnvironmentVariable(PreviewEnvVar);
        if (IsTruthy(env))
        {
            return true;
        }

        // 2) AthenaData/feature-flags.json 的 subscriptionPreview 字段。
        try
        {
            var path = Path.Combine(pathService.GetAppDataDirectory(), FlagsFileName);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("subscriptionPreview", out var value))
                {
                    return value.ValueKind == JsonValueKind.True
                        || (value.ValueKind == JsonValueKind.String && IsTruthy(value.GetString()));
                }
            }
        }
        catch (Exception ex)
        {
            // 解析失败一律视为关闭：功能开关不得因坏文件而误开或崩溃。
            Log.Warning(ex, "读取 feature-flags.json 失败，订阅预览开关按默认关闭处理");
        }

        return false;
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim() is "1" or "true" or "TRUE" or "True" or "yes" or "on";
    }
}
