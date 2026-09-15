using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 一轮请求以「错误文本」而不是异常收场时的失败信号。
///
/// 错误文本照旧流进气泡——交互式会话里那是比异常好得多的呈现方式。但流本身是正常结束的，
/// 调用方一个异常都拿不到，于是「这一轮到底成没成」无从判断：一次供应商故障因此被
/// 定时任务记成 succeeded、还弹了「已完成」通知，而流水线其实停在半路。
/// 需要知道结果的调用方（cron）订阅这个回调；交互式发送忽略它即可。
/// </summary>
/// <param name="Message">已脱敏、可直接落进运行记录的失败说明。</param>
/// <param name="Category">供应商错误归类；请求尚未发出（运行时快照都没建起来）时为 null。</param>
public sealed record ChatTurnFailure(string Message, ProviderErrorCategory? Category);

/// <summary>
/// 本轮请求被上游在流中途掐断、即将自动重发时的通知。
///
/// 它不宣告失败——失败仍然只由 <see cref="ChatTurnFailure"/> 说出口（在重试全部耗尽之后）。
/// 这个回调只负责让界面把这段沉默解释清楚：不说，用户看到的就是一个卡住不动的气泡。
/// </summary>
/// <param name="Attempt">这是第几次重试（从 1 起）。</param>
/// <param name="MaxAttempts">本轮最多重试几次。</param>
/// <param name="Delay">发出重试之前还要等多久。</param>
/// <param name="Category">被判定为可重试的那个错误的归类。</param>
public sealed record ProviderRetryNotice(int Attempt, int MaxAttempts, TimeSpan Delay, ProviderErrorCategory? Category);

/// <summary>
/// AI 对话服务接口
/// </summary>
public interface IChatService
{
    /// <summary>
    /// 流式发送消息
    /// </summary>
    /// <param name="userMessage">用户消息</param>
    /// <param name="context">对话上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="onMessageAdded">当产生中间消息（如工具结果）时的回调</param>
    /// <param name="onUsageReported">每轮 API 响应回报真实 token 用量时的回调</param>
    /// <param name="onCompressionProgress">自动压缩的阶段回报，供界面把这段等待解释清楚</param>
    /// <param name="skipCompressionToken">
    /// 只取消自动压缩、不取消整轮请求。用户放弃这次压缩时，本轮仍带原上下文继续发出。
    /// </param>
    /// <param name="onProviderError">
    /// 本轮因 API/供应商故障收场时的回调（见 <see cref="ChatTurnFailure"/>）。
    /// 错误文本仍会照常出现在返回的流里，这个回调只是把「失败」这件事说出来。
    /// </param>
    /// <returns>AI 响应文本流</returns>
    IAsyncEnumerable<string> StreamMessageAsync(
        string userMessage,
        ConversationContext context,
        IReadOnlyList<ChatAttachment>? attachments = null,
        CancellationToken cancellationToken = default,
        Action<ChatMessage>? onMessageAdded = null,
        Action<TokenUsageSnapshot>? onUsageReported = null,
        Action<string>? onToolCallArgumentsStreaming = null,
        Action<string>? onReasoningDelta = null,
        bool addToContext = true,
        Func<CompressionTransition, CancellationToken, Task<CompressionCommitResult>>? onCompressionTransition = null,
        Action<string>? onContextWarning = null,
        Action<ContextAnchorRecord>? onAnchorObserved = null,
        Action<CompressionProgress>? onCompressionProgress = null,
        CancellationToken skipCompressionToken = default,
        Action<ChatTurnFailure>? onProviderError = null,
        Action<ProviderRetryNotice>? onProviderRetry = null);

    /// <summary>
    /// 测试 API 连接
    /// </summary>
    /// <returns>是否连接成功</returns>
    Task<(bool Success, string? Message)> TestConnectionAsync();

    /// <summary>
    /// 构建即将发送给主模型的「原始上下文」快照（按消息拆分），用于调试。
    /// 完整复用真实发送时的消息组装逻辑（系统提示、摘要、时间戳、文档/附件注入、工具调用等）。
    /// </summary>
    IReadOnlyList<RawContextEntry> BuildRawContext(
        ConversationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新配置
    /// </summary>
    void UpdateConfig(AppConfig config);

    Task<AudioOutputTestResult> TestAudioOutputAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 为给定文本生成助手语音附件（TTS）。从流式回复中解耦，供 UI 在文本回复结束后
    /// 于后台单独调用，避免语音生成阻塞发送/回缩/分支等交互。
    /// </summary>
    /// <returns>成功时返回音频附件；失败时 Attachment 为 null 且 ErrorMessage 非空。</returns>
    Task<(ChatAttachment? Attachment, string ErrorMessage)> GenerateAssistantSpeechAsync(
        string text,
        CancellationToken cancellationToken = default);
}
