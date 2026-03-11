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

    public void AddUserMessage(string content)
    {
        _messages.Add(new ContextMessage { Role = "user", Content = content });
    }

    public void AddAssistantMessage(string content, string? toolCallsJson = null)
    {
        _messages.Add(new ContextMessage { Role = "assistant", Content = content, ToolCallsJson = toolCallsJson });
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
            if (!string.IsNullOrEmpty(_summary)) total += EstimateTokens(_summary);
            foreach (var msg in _messages) total += EstimateTokens(msg.Content);
            return total;
        }
    }

    /// <summary>
    /// 计算保留数量，确保不会切断工具调用链
    /// </summary>
    public int CalculateKeepCount(int targetThreshold)
    {
        if (_messages.Count == 0) return 0;

        var fixedCost = EstimateTokens(_mainPersona) + EstimateTokens(_summary);
        var availableTokens = (int)(targetThreshold * 0.8) - fixedCost;
        if (availableTokens <= 0) return 1;

        int accumulatedTokens = 0;
        int keepCount = 0;

        // 从后往前计算
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            var msgTokens = EstimateTokens(_messages[i].Content);
            if (accumulatedTokens + msgTokens > availableTokens && keepCount > 0) break;

            accumulatedTokens += msgTokens;
            keepCount++;
        }

        // 核心修正：工具链原子性检查
        // 如果保留的第一条消息是 tool，必须向前追溯直到包含对应的 assistant (tool_calls)
        while (keepCount < _messages.Count)
        {
            int firstKeepIndex = _messages.Count - keepCount;
            var firstMsg = _messages[firstKeepIndex];

            if (firstMsg.Role == "tool")
            {
                // 如果是工具结果，强制多保留一条，继续检查上一条
                keepCount++;
            }
            else if (firstMsg.Role == "assistant" && !string.IsNullOrEmpty(firstMsg.ToolCallsJson))
            {
                // 如果是带工具调用的助手消息，目前它已经在保留范围内了，检查结束
                break;
            }
            else
            {
                // 既不是 tool 也不是带调用的助手消息，切分点安全
                break;
            }
        }

        return Math.Max(1, keepCount);
    }

    public bool NeedsCompression(int threshold) => EstimatedTokenCount > threshold;
}

public class ContextMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string? ToolCallsJson { get; set; }
}
