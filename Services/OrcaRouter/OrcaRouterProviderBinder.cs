using Athena.UI.Models;
using System;
using System.Linq;

namespace Athena.UI.Services.OrcaRouter;

/// <summary>
/// 把一次成功的接入落到配置上。纯函数，不碰 UI，也不保存——保存由各自的配置会话负责。
///
/// 两条规则都来自"用户看到的和实际生效的必须是同一个连接"：
/// - 同一个 host 只允许有一个连接。新建第二个的话，业务角色还指着旧的那个，
///   用户看到"已接入"却发现模型一个没变。
/// - 已经有模型的角色绝不重指。接入一个新供应商不该悄悄换掉用户选好的主对话模型。
/// </summary>
public static class OrcaRouterProviderBinder
{
    /// <summary>找到或建立 OrcaRouter 连接并写入 API Key，返回该连接。</summary>
    public static OpenAiProviderConfiguration Bind(
        AiModelConfiguration models,
        OrcaRouterEndpoints endpoints,
        string apiKey)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var provider = models.Providers.FirstOrDefault(candidate => IsOrcaRouter(candidate.BaseUrl, endpoints));
        if (provider == null)
        {
            provider = new OpenAiProviderConfiguration
            {
                DisplayName = endpoints.DisplayName,
                ProviderPreset = endpoints.ProviderPreset,
                BaseUrl = endpoints.BaseUrl
            };
            models.Providers.Add(provider);
        }
        else if (string.IsNullOrWhiteSpace(provider.ProviderPreset))
        {
            // 已有连接的 BaseUrl 与显示名都是用户的，不覆盖——他可能指向同一 host 的另一条路径。
            provider.ProviderPreset = endpoints.ProviderPreset;
        }

        provider.ApiKey = apiKey;
        return provider;
    }

    /// <summary>主对话角色尚未选定模型时，把它指向本次接入的默认模型。已选过则原样不动。</summary>
    public static bool AssignMainConversationIfUnset(
        AiModelConfiguration models,
        OpenAiProviderConfiguration provider,
        string defaultModel)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(defaultModel)) return false;
        if (!string.IsNullOrWhiteSpace(models.MainConversation.Model)) return false;

        models.MainConversation.ProviderId = provider.Id;
        models.MainConversation.Model = defaultModel;
        EnsureModelListed(provider, defaultModel);
        return true;
    }

    /// <summary>把默认模型补进库存，让下拉框里选得到它——库存刷新可能还没跑完或失败了。</summary>
    public static void EnsureModelListed(OpenAiProviderConfiguration provider, string modelId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(modelId)) return;
        if (provider.Models.Any(model => string.Equals(model.Id, modelId, StringComparison.Ordinal))) return;

        provider.Models.Add(new ProviderModelDescriptor
        {
            Id = modelId,
            DisplayName = modelId,
            Capability = ModelCapability.Text,
            IsManual = true
        });
    }

    /// <summary>按 host 判断一个连接是否就是 OrcaRouter。</summary>
    public static bool IsOrcaRouter(string? baseUrl, OrcaRouterEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || !Uri.TryCreate(endpoints.BaseUrl, UriKind.Absolute, out var reference))
        {
            return false;
        }
        return string.Equals(uri.Host, reference.Host, StringComparison.OrdinalIgnoreCase);
    }
}
