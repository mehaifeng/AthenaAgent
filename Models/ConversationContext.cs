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
            total += ToolsDeclarationTokenCount; // 计入工具声明开销
            if (!string.IsNullOrEmpty(_summary)) total += EstimateTokens(_summary);
            foreach (var msg in _messages) 
            {
                total += EstimateTokens(msg.Content);
                if (!string.IsNullOrEmpty(msg.ToolCallsJson))
                {
                    total += EstimateTokens(msg.ToolCallsJson); // 计入模型生成的工具调用 JSON
                }
            }
            return total;
        }
    }

    /// <summary>
    /// 计算保留数量，确保不会切断工具调用链
    /// </summary>
    public int CalculateKeepCount(int targetThreshold)
    {
        if (_messages.Count == 0) return 0;

        var fixedCost = EstimateTokens(_mainPersona) + EstimateTokens(_summary) + ToolsDeclarationTokenCount;
        var availableTokens = (int)(targetThreshold * 0.9) - fixedCost; // 留出 10% 余量
        if (availableTokens <= 0) return 1;

        int accumulatedTokens = 0;
        int keepCount = 0;

        // 从后往前计算
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            var msgTokens = EstimateTokens(_messages[i].Content);
            if (!string.IsNullOrEmpty(_messages[i].ToolCallsJson))
            {
                msgTokens += EstimateTokens(_messages[i].ToolCallsJson);
            }

            if (accumulatedTokens + msgTokens > availableTokens && keepCount > 0) break;

            accumulatedTokens += msgTokens;
            keepCount++;
        }

        // 核心修正：工具链原子性检查
        // 1. 如果保留的第一条消息是 tool，必须向前追溯直到包含对应的 assistant (tool_calls)
        while (keepCount < _messages.Count)
        {
            int firstKeepIndex = _messages.Count - keepCount;
            var firstMsg = _messages[firstKeepIndex];

            if (firstMsg.Role == "tool")
            {
                // 如果是工具结果，强制多保留一条，继续向更早的消息检查
                keepCount++;
            }
            else
            {
                // 此时第一条已不是 tool
                break;
            }
        }

        // 2. 确保第一条消息不是孤立的 assistant (带工具调用的)
        // 某些 API 要求必须由 user 开始，或者 assistant (tool_calls) 之后必须紧跟 tool 结果
        // 这里我们简单确保如果是 assistant (tool_calls)，它必须作为保留块的开始或被包含
        // 实际上上面的一步已经处理了 tool 追溯 assistant。
        
        // 3. 最终检查：如果第一条消息依然是 assistant 且带工具调用，通常没问题，
        // 但如果它前面的消息是 user，建议一起带上以保持语义完整。
        if (keepCount < _messages.Count)
        {
            int firstKeepIndex = _messages.Count - keepCount;
            if (_messages[firstKeepIndex].Role == "assistant" && firstKeepIndex > 0 && _messages[firstKeepIndex-1].Role == "user")
            {
                keepCount++;
            }
        }

        return Math.Clamp(keepCount, 1, _messages.Count);
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
