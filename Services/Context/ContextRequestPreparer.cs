using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Athena.UI.Services.Context;

public sealed partial class ContextRequestPreparer(TokenFingerprintService fingerprints) : IContextRequestPreparer
{
    public const int ImageEncodingVersion = 1;

    public PreparedChatRequest Prepare(
        EffectiveRequestRuntimeSnapshot runtime,
        IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
        ConversationContext context,
        string requestId,
        long conversationRevision = 0,
        bool imageBinaryIncluded = true,
        bool isImageFallback = false)
    {
        var exact = new StringBuilder();
        var fixedOverhead = new StringBuilder();

        foreach (var message in messages)
        {
            var role = message switch
            {
                SystemChatMessage => "system",
                UserChatMessage => "user",
                AssistantChatMessage => "assistant",
                ToolChatMessage => "tool",
                _ => "other"
            };
            exact.Append(role).Append('\u001e');
            foreach (var part in message.Content)
            {
                if (part.Kind != ChatMessageContentPartKind.Text) continue;
                var text = part.Text ?? string.Empty;
                exact.Append(text).Append('\u001d');
                if (role == "system") fixedOverhead.Append(NormalizeVolatile(text));
            }
            if (message is AssistantChatMessage assistantMessage)
            {
                var reasoning = GetReasoningContent(assistantMessage);
                if (!string.IsNullOrEmpty(reasoning))
                    exact.Append("reasoning:").Append(reasoning).Append('\u001d');
                foreach (var call in assistantMessage.ToolCalls)
                {
                    var arguments = call.FunctionArguments?.ToString() ?? string.Empty;
                    exact.Append(call.FunctionName).Append(arguments);
                }
            }
        }

        fixedOverhead.Append(runtime.ToolFingerprint);
        // 清理改变了请求内容却不改变消息 ID。把已清理集合并进固定开销指纹，清理之后
        // ContextAnchorLedger 就不会再把清理前的测量当作精确值；从未清理过的会话指纹不变。
        var clearedDigest = ToolResultClearing.ComputeDigest(context.ClearedToolResultIds);
        if (clearedDigest.Length > 0) fixedOverhead.Append("cleared-tool-results:").Append(clearedDigest);
        exact.Append("tool-definitions:").Append(runtime.ToolFingerprint).Append('\u001e');
        exact.Append("image-mode:")
            .Append(imageBinaryIncluded)
            .Append(':')
            .Append(isImageFallback)
            .Append('\u001e');
        foreach (var image in context.Messages.SelectMany(message => message.Attachments).Where(item => item.IsImage))
        {
            exact.Append("image:")
                .Append(image.Id)
                .Append(':')
                .Append(image.MimeType)
                .Append(':')
                .Append(image.SizeBytes)
                .Append(':')
                .Append(image.Width)
                .Append('x')
                .Append(image.Height)
                .Append('\u001e');
        }

        var identity = new RequestIdentity(
            BuildProfileKey(runtime),
            fingerprints.Compute(fixedOverhead.ToString()),
            fingerprints.Compute(exact.ToString()),
            imageBinaryIncluded,
            isImageFallback);
        return new PreparedChatRequest(
            requestId,
            conversationRevision,
            runtime,
            Array.AsReadOnly(messages.ToArray()),
            runtime.ChatOptions,
            identity,
            identity.ContextFingerprint);
    }

    private static string BuildProfileKey(EffectiveRequestRuntimeSnapshot runtime)
    {
        var host = Uri.TryCreate(runtime.MainModel.BaseUrl, UriKind.Absolute, out var uri)
            ? uri.Host.ToLowerInvariant()
            : "invalid-host";
        return string.Join('|',
            runtime.ExecutionPolicyIdentity.ProviderId,
            host,
            runtime.ExecutionPolicyIdentity.ExternalModelId,
            runtime.ModelMetadata.TokenizerHint ?? "unknown-tokenizer",
            runtime.RequestFormatVersion,
            runtime.ExecutionPolicyIdentity.Protocol,
            ImageEncodingVersion,
            runtime.ToolFingerprint);
    }

    private static string? GetReasoningContent(AssistantChatMessage message)
    {
#pragma warning disable SCME0001
        return message.Patch.TryGetValue("$.reasoning_content"u8, out string? reasoning)
            ? reasoning
            : null;
#pragma warning restore SCME0001
    }

    private static string NormalizeVolatile(string value)
    {
        value = TimestampRegex().Replace(value, "<timestamp>");
        return GuidRegex().Replace(value, "<id>");
    }

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}\b")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{32}\b|\b[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,36}\b")]
    private static partial Regex GuidRegex();
}
