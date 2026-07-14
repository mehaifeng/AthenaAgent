using Athena.UI.Models;
using Athena.UI.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

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
    /// <param name="onContextCompressed">中途自动压缩时的回调（摘要, 被压缩条数）</param>
    /// <param name="onUsageReported">每轮 API 响应回报真实 token 用量时的回调</param>
    /// <returns>AI 响应文本流</returns>
    IAsyncEnumerable<string> StreamMessageAsync(
        string userMessage,
        ConversationContext context,
        IReadOnlyList<ChatAttachment>? attachments = null,
        CancellationToken cancellationToken = default,
        Action<ChatMessage>? onMessageAdded = null,
        Action<string, int>? onContextCompressed = null,
        Action<TokenUsageSnapshot>? onUsageReported = null,
        bool addToContext = true);

    /// <summary>
    /// 测试 API 连接
    /// </summary>
    /// <returns>是否连接成功</returns>
    Task<(bool Success, string? Message)> TestConnectionAsync();

    /// <summary>
    /// 构建即将发送给主模型的「原始上下文」快照（按消息拆分），用于调试。
    /// 完整复用真实发送时的消息组装逻辑（系统提示、摘要、时间戳、文档/附件注入、工具调用等）。
    /// </summary>
    IReadOnlyList<RawContextEntry> BuildRawContext(ConversationContext context);

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
