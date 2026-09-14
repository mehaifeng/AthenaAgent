using Athena.UI.Services.OrcaRouter;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>接入流程失败的原因。<see cref="OrcaRouterConnectFailure.None"/> 只在成功时出现。</summary>
public enum OrcaRouterConnectFailure
{
    None,

    /// <summary>随包的端点配置缺失或不合法，入口应当处于禁用态。</summary>
    Unavailable,

    /// <summary>已经有一次授权在进行中。同时开两个浏览器页会让两个 state 互相污染。</summary>
    AlreadyRunning,

    /// <summary>用户没在预算内完成授权。</summary>
    TimedOut,

    /// <summary>调用方取消。</summary>
    Canceled,

    /// <summary>授权页明确拒绝，或回调没带回可用的 code。</summary>
    ProviderDenied,

    /// <summary>换取 API Key 的请求失败（网络错误或非 2xx）。</summary>
    ExchangeFailed,

    /// <summary>换取成功但响应里找不到 API Key。绝不把空 key 当成接入成功。</summary>
    MalformedResponse
}

/// <summary>接入流程当前所处的阶段，供 UI 呈现。</summary>
public enum OrcaRouterConnectStage
{
    /// <summary>已绑定环回端口，正准备打开浏览器。</summary>
    Starting,

    /// <summary>等待用户在浏览器里完成授权。</summary>
    AwaitingAuthorization,

    /// <summary>已拿到授权码，正在换取 API Key。</summary>
    ExchangingCode
}

/// <summary>
/// 一次进度通报。
/// <paramref name="AuthorizationUrl"/> 始终带着，因为浏览器没能自动打开时，
/// 用户手动复制这个链接就是唯一的退路——那种情况下流程不失败，仍在等回调。
/// </summary>
public sealed record OrcaRouterConnectProgress(
    OrcaRouterConnectStage Stage,
    string AuthorizationUrl,
    bool BrowserOpened);

/// <summary>接入结果。只有 <see cref="Succeeded"/> 为 true 时 <see cref="ApiKey"/> 才有值，且必定非空。</summary>
public sealed record OrcaRouterConnectResult
{
    public bool Succeeded { get; init; }

    /// <summary>换回来的 API Key。调用方负责写入配置。</summary>
    public string? ApiKey { get; init; }

    /// <summary>本次使用的端点配置，调用方据此填 BaseUrl / 供应商类型 / 默认模型。</summary>
    public OrcaRouterEndpoints? Endpoints { get; init; }

    public OrcaRouterConnectFailure Failure { get; init; }

    /// <summary>失败原因的可展示描述；已做凭据脱敏。</summary>
    public string? Error { get; init; }
}

/// <summary>
/// OrcaRouter 的 PKCE 接入流程。签名只出现领域类型，UI 层负责把结果投影成配置与提示。
/// </summary>
public interface IOrcaRouterConnectService
{
    /// <summary>端点配置是否可用。为 false 时入口应禁用而不是点了没反应。</summary>
    bool IsAvailable { get; }

    /// <summary>随包的端点配置；<see cref="IsAvailable"/> 为 false 时为 null。</summary>
    OrcaRouterEndpoints? Endpoints { get; }

    /// <summary>跑一次完整的授权流程。同一时刻只允许一次。</summary>
    Task<OrcaRouterConnectResult> ConnectAsync(
        IProgress<OrcaRouterConnectProgress>? progress,
        CancellationToken cancellationToken);
}
