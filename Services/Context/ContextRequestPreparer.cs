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
    public const int EstimatorVersion = 2;
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
        long cjk = 0, other = 0, json = 0, attachmentManifest = 0;
        int system = 0, user = 0, assistant = 0, tool = 0;
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
            switch (role)
            {
                case "system": system++; break;
                case "user": user++; break;
                case "assistant": assistant++; break;
                case "tool": tool++; break;
            }
            exact.Append(role).Append('\u001e');
            foreach (var part in message.Content)
            {
                if (part.Kind != ChatMessageContentPartKind.Text) continue;
                var text = part.Text ?? string.Empty;
                exact.Append(text).Append('\u001d');
                if (role == "system") fixedOverhead.Append(NormalizeVolatile(text));
                CountText(text, role == "tool", ref cjk, ref other, ref json, ref attachmentManifest);
            }
            if (message is AssistantChatMessage assistantMessage)
            {
                var reasoning = GetReasoningContent(assistantMessage);
                if (!string.IsNullOrEmpty(reasoning))
                {
                    CountText(reasoning, structured: false, ref cjk, ref other, ref json, ref attachmentManifest);
                    exact.Append("reasoning:").Append(reasoning).Append('\u001d');
                }
                foreach (var call in assistantMessage.ToolCalls)
                {
                    var arguments = call.FunctionArguments?.ToString() ?? string.Empty;
                    json += arguments.Length + (call.FunctionName?.Length ?? 0);
                    exact.Append(call.FunctionName).Append(arguments);
                }
            }
        }

        long toolChars = 0;
        foreach (var definition in runtime.ToolDefinitions)
        {
            toolChars += (definition.FunctionName?.Length ?? 0)
                         + (definition.FunctionDescription?.Length ?? 0)
                         + (definition.FunctionParameters?.ToString().Length ?? 0);
        }
        fixedOverhead.Append(runtime.ToolFingerprint);
        exact.Append("tool-definitions:").Append(runtime.ToolFingerprint).Append('\u001e');
        var images = context.Messages.SelectMany(message => message.Attachments).Where(item => item.IsImage).ToArray();
        exact.Append("image-mode:")
            .Append(imageBinaryIncluded)
            .Append(':')
            .Append(isImageFallback)
            .Append('\u001e');
        foreach (var image in images)
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
        var known = images.Count(image => image.Width > 0 && image.Height > 0);
        var unknown = images.Length - known;
        var tileUnits = images.Sum(image => Math.Max(1, (ConversationContext.EstimateImageTokens(image.Width, image.Height) - 85) / 170));
        var messageCount = system + user + assistant + tool;
        var imagePriorTokens = imageBinaryIncluded
            ? images.Sum(image => (long)ConversationContext.EstimateImageTokens(image.Width, image.Height))
            : 0;
        var heuristic = checked(
            cjk
            + (other + 3) / 4
            + (json + 3) / 4
            + (attachmentManifest + 3) / 4
            + messageCount * 4L
            + (toolChars + 3) / 4
            + imagePriorTokens);
        var profileKey = BuildProfileKey(runtime);
        var features = new ContextFeatureSnapshot(
            requestId, profileKey, EstimatorVersion, cjk, other, json,
            system, user, assistant, tool, toolChars, attachmentManifest,
            images.Length, known, unknown, tileUnits, imageBinaryIncluded ? images.Length : 0,
            imagePriorTokens,
            heuristic,
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
            features,
            features.ContextFingerprint);
    }

    /// <summary>
    /// 「自锚点以来新增消息」的字符权重分，口径与整段启发式一致（CJK 1:1、其余 4:1、
    /// 工具结果按结构化 JSON 计）。绝对准确性并不重要——只要预测与训练用同一把尺，
    /// 系统性偏差会被增量标度整体吸收。
    /// </summary>
    public static long ComputeDeltaCharScore(IEnumerable<ContextMessage> messages)
    {
        long cjk = 0, other = 0, json = 0, attachmentManifest = 0, attachmentPrior = 0;
        var count = 0;
        foreach (var message in messages)
        {
            count++;
            var structured = string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase);
            CountText(message.Content, structured, ref cjk, ref other, ref json, ref attachmentManifest);
            if (!string.IsNullOrEmpty(message.ReasoningContent))
                CountText(message.ReasoningContent, structured: false, ref cjk, ref other, ref json, ref attachmentManifest);
            if (!string.IsNullOrEmpty(message.ToolCallsJson))
                json += message.ToolCallsJson.Length;
            foreach (var attachment in message.Attachments)
            {
                attachmentPrior += attachment.Kind == AttachmentKind.Image
                    ? ConversationContext.EstimateImageTokens(attachment.Width, attachment.Height)
                    : ConversationContext.AttachmentManifestTokenCost;
            }
        }
        return cjk
               + (other + 3) / 4
               + (json + 3) / 4
               + (attachmentManifest + 3) / 4
               + count * 4L
               + attachmentPrior;
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

    private static void CountText(
        string text,
        bool structured,
        ref long cjk,
        ref long other,
        ref long json,
        ref long attachmentManifest)
    {
        var manifestStart = text.IndexOf("<attachments>", StringComparison.Ordinal);
        if (manifestStart >= 0)
        {
            attachmentManifest += text.Length - manifestStart;
            text = text[..manifestStart];
        }
        if (structured)
        {
            json += text.Length;
            return;
        }
        foreach (var ch in text)
        {
            if (IsCjk(ch)) cjk++;
            else other++;
        }
    }

    private static bool IsCjk(char ch) =>
        ch is >= '\u4E00' and <= '\u9FFF'
        or >= '\u3400' and <= '\u4DBF'
        or >= '\u3000' and <= '\u30FF'
        or >= '\uFF00' and <= '\uFFEF'
        or >= '\uAC00' and <= '\uD7A3';

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
