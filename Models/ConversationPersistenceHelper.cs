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
            IsGroupExpanded = segment.IsGroupExpanded
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

        clone.RehookToolCalls();
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

        // 还原后恢复工具组的集合监听与派生属性，并默认收起（用户未手动展开）
        foreach (var segment in msg.Segments)
        {
            if (segment.IsToolCallGroup)
            {
                segment.RehookToolCalls();
                segment.UserToggledGroup = false;
                segment.IsGroupExpanded = false;
            }
        }

        msg.ResolveSegmentAttachments();
    }
}
