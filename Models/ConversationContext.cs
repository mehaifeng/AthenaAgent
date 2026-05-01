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

    public void AddUserMessage(string content, DateTime? timestamp = null)
    {
        _messages.Add(new ContextMessage { Role = "user", Content = content, Timestamp = timestamp ?? DateTime.Now });
    }

    public void AddAssistantMessage(string content, string? toolCallsJson = null, string? reasoningContent = null)
    {
        _messages.Add(new ContextMessage
        {
            Role = "assistant",
            Content = content,
            ToolCallsJson = toolCallsJson,
            ReasoningContent = reasoningContent
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
            }
            return total;
        }
    }

    public bool NeedsCompression(int threshold) => EstimatedTokenCount > threshold;
}

public class ContextMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string? ToolCallsJson { get; set; }
    public string? ReasoningContent { get; set; }
    public DateTime Timestamp { get; set; }
}
