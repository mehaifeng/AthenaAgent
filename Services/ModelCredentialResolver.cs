using Athena.UI.Models;

namespace Athena.UI.Services;

/// <summary>
/// 角色凭据解析后的有效配置（Provider/BaseUrl/ApiKey + 角色自身的模型名）。
/// </summary>
public readonly record struct EffectiveModelConfig(
    string Provider, string BaseUrl, string ApiKey, string Model);

/// <summary>
/// 统一凭据继承树的共享解析器：所有模型角色（次级/Embedding/图像/音频/子代理/浏览器/KB整理）
/// 的「继承主 AI 或使用独立凭据」逻辑集中在此，避免各服务重复实现回退语义。
/// InheritMain：直接复用主 AI 的 Provider/BaseUrl/ApiKey；
/// Custom：使用角色字段，留空逐字段回退主 AI（与既有 SubAgent/Browser 解析器语义一致）。
/// 模型名始终取角色自身字段（roleModel → roleModelDefault → 主模型）。
/// </summary>
public static class ModelCredentialResolver
{
    public static EffectiveModelConfig Resolve(
        ModelCredentialSource source,
        AppConfig config,
        string roleProvider, string roleBaseUrl, string roleApiKey,
        string roleModel, string roleModelDefault = "")
    {
        var model = FirstNonEmpty(roleModel, roleModelDefault, config.Model);
        if (source == ModelCredentialSource.InheritMain)
        {
            return new EffectiveModelConfig(config.Provider, config.BaseUrl, config.ApiKey, model);
        }

        return new EffectiveModelConfig(
            FirstNonEmpty(roleProvider, config.Provider),
            FirstNonEmpty(roleBaseUrl, config.BaseUrl),
            FirstNonEmpty(roleApiKey, config.ApiKey),
            model);
    }

    public static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return string.Empty;
    }
}
