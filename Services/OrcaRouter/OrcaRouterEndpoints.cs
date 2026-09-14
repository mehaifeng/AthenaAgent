using Avalonia.Platform;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Athena.UI.Services.OrcaRouter;

/// <summary>
/// OrcaRouter 的接入端点与归因码，来自 <c>Assets/Providers/orcarouter.json</c>。
///
/// 之所以放在资源文件而不是 C# 常量里：推广码和端点是运营数据，改它不该改代码；
/// 同时开源目录的接入审核要在仓库的配置文件（.json/.toml/…）里搜到 orcarouter，写在 README 里不算。
///
/// 但配置文件可写 ≠ 端点可变：<see cref="Parse"/> 强制 https 并校验 host 白名单，
/// 所以改掉 json 里的域名不会把用户引到别处，只会让接入整体不可用（而且是**大声**不可用——
/// <see cref="Load"/> 记 Error 并返回 null，调用方据此禁用入口，而不是留一个点了没反应的按钮）。
/// </summary>
public sealed record OrcaRouterEndpoints
{
    private static readonly Uri AssetUri = new("avares://Athena.UI/Assets/Providers/orcarouter.json");

    /// <summary>授权页允许的 host。</summary>
    private static readonly HashSet<string> AuthHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "www.orcarouter.ai",
        "orcarouter.ai"
    };

    /// <summary>换取 API Key 的 host。</summary>
    private static readonly HashSet<string> ApiHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.orcarouter.ai"
    };

    /// <summary>写入 <c>OpenAiProviderConfiguration.ProviderPreset</c> 的供应商类型。</summary>
    public required string ProviderPreset { get; init; }

    /// <summary>新建连接时的默认显示名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>OpenAI 兼容 API 根（已含 /v1）。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>浏览器授权页。</summary>
    public required Uri AuthUrl { get; init; }

    /// <summary>用 code + code_verifier 换 API Key 的端点。</summary>
    public required Uri TokenUrl { get; init; }

    /// <summary>环回回调路径；按 RFC 8252 §7.3 只有端口可变，路径必须与注册值一致。</summary>
    public required string CallbackPath { get; init; }

    /// <summary>授权页上展示的应用名。</summary>
    public required string AppName { get; init; }

    /// <summary>推广归因码。只出现在授权 URL 的 query 中，绝不进入任何 API 请求。</summary>
    public required string ReferralCode { get; init; }

    /// <summary>接入成功后给主对话角色的默认模型。</summary>
    public required string DefaultModel { get; init; }

    /// <summary>读取随应用打包的端点配置。失败时记 Error 并返回 null，由调用方禁用接入入口。</summary>
    public static OrcaRouterEndpoints? Load(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            using var stream = AssetLoader.Open(AssetUri);
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            // 资源缺失或被改坏。静默降级会变成"按钮点了没反应"，所以这里必须留下痕迹。
            logger.Error(ex, "OrcaRouter endpoint configuration is unusable; the connect entry point stays disabled");
            return null;
        }
    }

    /// <summary>解析并校验端点配置。任何一项不合法都直接抛出，绝不返回半个可用的配置。</summary>
    /// <exception cref="InvalidOperationException">字段缺失、非 https、或 host 不在白名单内。</exception>
    public static OrcaRouterEndpoints Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var authUrl = ReadUri(root, "authUrl", AuthHosts);
        var tokenUrl = ReadUri(root, "tokenUrl", ApiHosts);
        var baseUrl = ReadUri(root, "baseUrl", ApiHosts);
        var callbackPath = ReadString(root, "callbackPath");
        if (!callbackPath.StartsWith('/') || callbackPath.AsSpan().ContainsAny('?', '#'))
        {
            throw new InvalidOperationException($"OrcaRouter callbackPath must be a bare absolute path: {callbackPath}");
        }

        return new OrcaRouterEndpoints
        {
            ProviderPreset = ReadString(root, "providerPreset"),
            DisplayName = ReadString(root, "displayName"),
            BaseUrl = baseUrl.ToString(),
            AuthUrl = authUrl,
            TokenUrl = tokenUrl,
            CallbackPath = callbackPath,
            AppName = ReadString(root, "appName"),
            ReferralCode = ReadString(root, "referralCode"),
            DefaultModel = ReadString(root, "defaultModel")
        };
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidOperationException($"OrcaRouter endpoint configuration is missing '{name}'.");
        }
        return element.GetString()!.Trim();
    }

    private static Uri ReadUri(JsonElement root, string name, HashSet<string> allowedHosts)
    {
        var raw = ReadString(root, name);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"OrcaRouter '{name}' is not an absolute URL: {raw}");
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"OrcaRouter '{name}' must use https: {raw}");
        }
        if (!allowedHosts.Contains(uri.Host))
        {
            throw new InvalidOperationException($"OrcaRouter '{name}' points at an unexpected host: {uri.Host}");
        }
        return uri;
    }
}
