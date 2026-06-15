using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Models;

/// <summary>
/// 对话上下文管理
/// </summary>
public class ConversationContext
{
    private readonly List<ContextMessage> _messages = new();
    private readonly int _maxTokens;
    private string _mainPersona = string.Empty;
    private string? _summary;

    public string ConversationId { get; set; } = Guid.NewGuid().ToString("N");

    public int ToolsDeclarationTokenCount { get; set; } = 0;

    public ConversationContext(int maxTokens = 8000)
    {
        _maxTokens = maxTokens;
    }

    public void SetMainPersona(string persona)
    {
        _mainPersona = persona;
    }

    public void SetSummary(string? summary)
    {
        _summary = summary;
    }

    public string? Summary => _summary;

    public IReadOnlyList<ContextMessage> Messages => _messages.AsReadOnly();

    public void AddUserMessage(string content, DateTime? timestamp = null, IEnumerable<ChatAttachment>? attachments = null)
    {
        _messages.Add(new ContextMessage
        {
            Role = "user",
            Content = content,
            Timestamp = timestamp ?? DateTime.Now,
            Attachments = attachments?.Select(CloneAttachment).ToList() ?? new List<ChatAttachment>()
        });
    }

    public void AddAssistantMessage(
        string content,
        string? toolCallsJson = null,
        string? reasoningContent = null,
        IEnumerable<ChatAttachment>? attachments = null,
        string? outputAudioReferenceId = null)
    {
        _messages.Add(new ContextMessage
        {
            Role = "assistant",
            Content = content,
            ToolCallsJson = toolCallsJson,
            ReasoningContent = reasoningContent,
            OutputAudioReferenceId = outputAudioReferenceId,
            Attachments = attachments?.Select(CloneAttachment).ToList() ?? new List<ChatAttachment>()
        });
    }

    public void AddToolMessage(string content, string? toolCallId = null)
    {
        _messages.Add(new ContextMessage { Role = "tool", Content = content, ToolCallId = toolCallId });
    }

    public void AddSystemMessage(string content)
    {
        _messages.Add(new ContextMessage { Role = "system", Content = content });
    }

    public void Clear() => _messages.Clear();

    public void RemoveMessages(int count)
    {
        if (count <= 0) return;
        int toRemove = Math.Min(count, _messages.Count);
        _messages.RemoveRange(0, toRemove);
    }

    public void Reset()
    {
        _messages.Clear();
        _summary = null;
    }

    public ConversationContext Clone()
    {
        var clone = new ConversationContext(_maxTokens)
        {
            ToolsDeclarationTokenCount = ToolsDeclarationTokenCount,
            ConversationId = ConversationId
        };

        clone.SetMainPersona(_mainPersona);
        clone.SetSummary(_summary);

        foreach (var message in _messages)
        {
            switch (message.Role)
            {
                case "user":
                    clone.AddUserMessage(message.Content, message.Timestamp, message.Attachments);
                    break;
                case "assistant":
                    clone.AddAssistantMessage(
                        message.Content,
                        message.ToolCallsJson,
                        message.ReasoningContent,
                        message.Attachments,
                        message.OutputAudioReferenceId);
                    break;
                case "tool":
                    clone.AddToolMessage(message.Content, message.ToolCallId);
                    break;
                case "system":
                    clone.AddSystemMessage(message.Content);
                    break;
            }
        }

        return clone;
    }

    public static int EstimateTokens(string? content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        return content.Length / 2 + 10;
    }

    public int EstimatedTokenCount
    {
        get
        {
            int total = EstimateTokens(_mainPersona);
            total += ToolsDeclarationTokenCount; // 计入工具声明开销
            if (!string.IsNullOrEmpty(_summary)) total += EstimateTokens(_summary);
            foreach (var msg in _messages) 
            {
                total += EstimateTokens(msg.Content);
                if (!string.IsNullOrEmpty(msg.ToolCallsJson))
                {
                    total += EstimateTokens(msg.ToolCallsJson); // 计入模型生成的工具调用 JSON
                }
                if (!string.IsNullOrEmpty(msg.ReasoningContent))
                {
                    total += EstimateTokens(msg.ReasoningContent); // 计入思维链回放所需的 reasoning_content
                }
                total += msg.Attachments.Count(a => a.Kind == AttachmentKind.Image) * 1000;
                total += msg.Attachments.Count(a => a.Kind == AttachmentKind.Audio) * 300;
                // 文档附件解析出的 Markdown 会被注入到发往 AI 的消息中，需计入开销
                foreach (var doc in msg.Attachments.Where(a => a.Kind == AttachmentKind.Document))
                {
                    total += EstimateTokens(doc.ExtractedText);
                }
            }
            return total;
        }
    }

    public bool NeedsCompression(int threshold) => EstimatedTokenCount > threshold;

    private static ChatAttachment CloneAttachment(ChatAttachment attachment)
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
            CreatedAt = attachment.CreatedAt,
            Duration = attachment.Duration,
            ParseState = attachment.ParseState,
            ExtractedText = attachment.ExtractedText,
            ParseError = attachment.ParseError
        };
    }
}

public class ContextMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string? ToolCallsJson { get; set; }
    public string? ReasoningContent { get; set; }
    public string? OutputAudioReferenceId { get; set; }
    public DateTime Timestamp { get; set; }
    public List<ChatAttachment> Attachments { get; set; } = new();
}
