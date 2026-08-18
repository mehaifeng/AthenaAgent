using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Athena.UI.Models;

/// <summary>
/// 对话消息与附件的持久化辅助方法
/// </summary>
public static class ConversationPersistenceHelper
{
    public static bool ShouldPersistMessage(ChatMessage msg)
    {
        return !(msg.Role == "assistant"
            && msg.IsLoading
            && string.IsNullOrWhiteSpace(msg.Content)
            && msg.Attachments.Count == 0
            && string.IsNullOrWhiteSpace(msg.ToolCallsJson)
            && string.IsNullOrWhiteSpace(msg.ReasoningContent));
    }

    public static List<ChatMessage> CloneMessages(IEnumerable<ChatMessage> messages)
    {
        return messages.Select(CloneMessage).ToList();
    }

    public static ChatMessage CloneMessage(ChatMessage msg)
    {
        return new ChatMessage
        {
            Id = msg.Id,
            Role = msg.Role,
            Content = msg.Content,
            Timestamp = msg.Timestamp,
            ProviderId = msg.ProviderId,
            ModelId = msg.ModelId,
            IsHeartbeat = msg.IsHeartbeat,
            IsLoading = false,
            ToolCallId = msg.ToolCallId,
            ToolCallsJson = msg.ToolCallsJson,
            ReasoningContent = msg.ReasoningContent,
            OutputAudioReferenceId = msg.OutputAudioReferenceId,
            AudioErrorMessage = msg.AudioErrorMessage,
            Attachments = new ObservableCollection<ChatAttachment>(msg.Attachments.Select(CloneAttachment)),
            Segments = new ObservableCollection<ChatMessageSegment>(msg.Segments.Select(CloneSegment)),
            IsCompressed = msg.IsCompressed,
            CanRewind = false,
            IsHidden = msg.IsHidden,
            ToolExecutionSummary = string.Empty,
            ToolName = msg.ToolName
        };
    }

    public static ChatMessageSegment CloneSegment(ChatMessageSegment segment)
    {
        var clone = new ChatMessageSegment
        {
            Kind = segment.Kind,
            Text = segment.Text,
            AttachmentId = segment.AttachmentId,
            IsExpanded = segment.IsExpanded
        };

        foreach (var entry in segment.ToolCalls)
        {
            clone.ToolCalls.Add(new ToolCallEntry
            {
                ToolCallId = entry.ToolCallId,
                Name = entry.Name,
                Summary = entry.Summary,
                Arguments = entry.Arguments,
                Result = entry.Result,
                Status = entry.Status,
                IsExpanded = entry.IsExpanded
            });
        }

        return clone;
    }

    public static ChatAttachment CloneAttachment(ChatAttachment attachment)
    {
        return new ChatAttachment
        {
            Id = attachment.Id,
            Kind = attachment.Kind,
            FileName = attachment.FileName,
            StoredPath = attachment.StoredPath,
            MimeType = attachment.MimeType,
            SizeBytes = attachment.SizeBytes,
            Width = attachment.Width,
            Height = attachment.Height,
            AudioProvider = attachment.AudioProvider,
            CreatedAt = attachment.CreatedAt,
            FileCreatedAt = attachment.FileCreatedAt,
            FileModifiedAt = attachment.FileModifiedAt,
            PreviewImage = attachment.PreviewImage,
            Duration = attachment.Duration,
        };
    }

    public static void PrepareRestoredMessage(ChatMessage msg)
    {
        msg.IsLoading = false;
        msg.CanRewind = false;
        msg.ToolExecutionSummary = string.Empty;
        foreach (var attachment in msg.Attachments)
        {
            attachment.IsPlaying = false;
            attachment.Position = TimeSpan.Zero;
        }

        MigrateLegacyReasoning(msg);

        // 所有可折叠的东西统一恢复成收起态：打开一段历史对话看到的应该是一条条摘要，
        // 而不是上次离开时摊开的一屏参数与结果。
        foreach (var segment in msg.Segments)
        {
            foreach (var entry in segment.ToolCalls)
            {
                entry.IsExpanded = false;
            }

            segment.UserToggled = false;
            segment.IsExpanded = false;
            segment.IsAppending = false;
            segment.IsClamped = true;
        }

        msg.ResolveSegmentAttachments();
    }

    /// <summary>
    /// 老归档里推理是消息级的一整块，没有位置信息。恢复时把它迁成一个排在最前的思考段，
    /// 让老会话也走同一套分段渲染。
    /// 注意顺序：正文尚未成段时必须先固化正文——一旦有了任何 segment，
    /// <see cref="ChatMessage.UsesSegmentLayout"/> 即为真、legacy Markdown 渲染器关闭，
    /// 只在 Content 里的正文会从界面消失。
    /// </summary>
    private static void MigrateLegacyReasoning(ChatMessage msg)
    {
        if (!msg.HasReasoningContent || msg.Segments.Any(segment => segment.IsReasoning))
        {
            return;
        }

        if (msg.Segments.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(msg.Content))
            {
                return;
            }

            msg.Segments.Add(new ChatMessageSegment
            {
                Kind = ChatMessageSegmentKind.Markdown,
                Text = msg.Content
            });
        }

        msg.Segments.Insert(0, new ChatMessageSegment
        {
            Kind = ChatMessageSegmentKind.Reasoning,
            Text = msg.ReasoningContent ?? string.Empty
        });

        msg.NotifySegmentsChanged();
    }
}
