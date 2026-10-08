using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>
/// 一次请求的「身份」：只回答「这份请求和另一份是不是同一种形状」，不回答「它有多少 token」。
/// 用量只来自供应商回报的 usage，本地不再有任何按字符猜 token 的东西。
/// </summary>
/// <param name="ModelProfileKey">模型、主机、协议、分词器线索、工具集与图片编码版本的组合键：任一变化，旧测量即不可比。</param>
/// <param name="FixedOverheadFingerprint">
/// 系统提示（去掉时间戳与 ID 一类易变值）、工具集和已清理工具结果集合的指纹。
/// <c>ContextAnchorLedger</c> 凭它判断历史测量是否仍属于同一种请求。
/// </param>
/// <param name="ContextFingerprint">整份请求内容的精确指纹，用于识别「同一份请求」。</param>
public sealed record RequestIdentity(
    string ModelProfileKey,
    string FixedOverheadFingerprint,
    string ContextFingerprint,
    bool ImageBinaryIncluded,
    bool IsImageFallback);

public sealed record PreparedChatRequest(
    string RequestId,
    long ConversationRevision,
    Athena.UI.Services.Context.EffectiveRequestRuntimeSnapshot Runtime,
    IReadOnlyList<OpenAI.Chat.ChatMessage> Messages,
    OpenAI.Chat.ChatCompletionOptions Options,
    RequestIdentity Identity,
    string ContextFingerprint);

public sealed record ProviderInputModalityUsage(
    long? TextTokens = null,
    long? ImageTokens = null,
    long? AudioTokens = null);
