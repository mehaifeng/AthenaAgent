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
            Role = msg.Role,
            Content = msg.Content,
            EditContent = string.Empty,
            Timestamp = msg.Timestamp,
            IsHeartbeat = msg.IsHeartbeat,
            IsLoading = false,
            IsEditing = false,
            ToolCallId = msg.ToolCallId,
            ToolCallsJson = msg.ToolCallsJson,
            ReasoningContent = msg.ReasoningContent,
            OutputAudioReferenceId = msg.OutputAudioReferenceId,
            AudioErrorMessage = msg.AudioErrorMessage,
            Attachments = new ObservableCollection<ChatAttachment>(msg.Attachments.Select(CloneAttachment)),
            Segments = new ObservableCollection<ChatMessageSegment>(msg.Segments.Select(CloneSegment)),
            IsCompressed = msg.IsCompressed,
            CanEdit = false,
            CanRegenerate = false,
            IsHidden = msg.IsHidden,
            ToolExecutionSummary = string.Empty,
            ToolName = msg.ToolName
        };
    }

    public static ChatMessageSegment CloneSegment(ChatMessageSegment segment)
    {
        return new ChatMessageSegment
        {
            Kind = segment.Kind,
            Text = segment.Text,
            AttachmentId = segment.AttachmentId
        };
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
            PreviewImage = attachment.PreviewImage,
            Duration = attachment.Duration,
            ParseState = attachment.ParseState,
            ExtractedText = attachment.ExtractedText,
            ParseError = attachment.ParseError
        };
    }

    public static void PrepareRestoredMessage(ChatMessage msg)
    {
        msg.IsLoading = false;
        msg.IsEditing = false;
        msg.EditContent = string.Empty;
        msg.CanEdit = false;
        msg.CanRegenerate = false;
        msg.ToolExecutionSummary = string.Empty;
        foreach (var attachment in msg.Attachments)
        {
            attachment.IsPlaying = false;
            attachment.Position = TimeSpan.Zero;
        }

        msg.ResolveSegmentAttachments();
    }
}
