using System.Collections.Generic;

namespace Athena.UI.Services;

/// <summary>
/// API 供应商目录：候选名单与默认端点的唯一数据源（配置页/扩展页共用）。
/// 音频供应商的默认端点归 <see cref="AudioConfigResolver"/> 所有，此处不重复维护。
/// </summary>
public static class ProviderCatalog
{
    private static readonly Dictionary<string, string> ChatProviderUrls = new()
    {
        ["OpenAI"] = "https://api.openai.com/v1",
        ["Google"] = "https://generativelanguage.googleapis.com/v1beta/openai/",
        ["Zhipu"] = "https://open.bigmodel.cn/api/paas/v4",
        ["Mimimaxi"] = "https://api.minimaxi.com/v1",
        ["Alibaba"] = "https://dashscope.aliyuncs.com/compatible-mode/v1",
        ["Deepseek"] = "https://api.deepseek.com/v1",
        ["OpenRouter"] = "https://openrouter.ai/api/v1",
        ["Custom"] = ""
    };

    private static readonly Dictionary<string, string> WebSearchProviderUrls = new()
    {
        ["Tavily"] = "https://api.tavily.com",
        ["WebSearchAPI"] = "https://api.websearchapi.ai",
        ["Zhipu"] = "https://open.bigmodel.cn/api/paas/v4",
        ["Baidu"] = "https://qianfan.baidubce.com/v2/ai_search/web_search"
    };

    /// <summary>对话/嵌入类模型的 API 供应商候选。</summary>
    public static IReadOnlyCollection<string> ChatProviders => ChatProviderUrls.Keys;

    /// <summary>联网搜索供应商候选。</summary>
    public static IReadOnlyCollection<string> WebSearchProviders => WebSearchProviderUrls.Keys;

    /// <summary>按供应商取对话类 API 的默认端点（仅精确匹配；Custom 返回空串，未知供应商返回 false 不动原值）。</summary>
    public static bool TryGetChatBaseUrl(string? provider, out string url)
        => ChatProviderUrls.TryGetValue(provider ?? string.Empty, out url!);

    /// <summary>按供应商取联网搜索的默认端点（仅精确匹配）。</summary>
    public static bool TryGetWebSearchBaseUrl(string? provider, out string url)
        => WebSearchProviderUrls.TryGetValue(provider ?? string.Empty, out url!);
}
