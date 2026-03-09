using System;
using System.Collections.Generic;

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

    /// <summary>
    /// 设置人格提示词
    /// </summary>
    public void SetMainPersona(string persona)
    {
        _mainPersona = persona;
    }

    /// <summary>
    /// 设置上下文摘要
    /// </summary>
    public void SetSummary(string? summary)
    {
        _summary = summary;
    }

    /// <summary>
    /// 获取当前摘要
    /// </summary>
    public string? Summary => _summary;

    /// <summary>
    /// 所有消息
    /// </summary>
    public IReadOnlyList<ContextMessage> Messages => _messages.AsReadOnly();

    /// <summary>
    /// 添加用户消息
    /// </summary>
    public void AddUserMessage(string content)
    {
        _messages.Add(new ContextMessage
        {
            Role = "user",
            Content = content
        });
    }

    /// <summary>
    /// 添加助手消息
    /// </summary>
    public void AddAssistantMessage(string content, string? toolCallsJson = null)
    {
        _messages.Add(new ContextMessage
        {
            Role = "assistant",
            Content = content,
            ToolCallsJson = toolCallsJson
        });
    }

    /// <summary>
    /// 添加工具消息
    /// </summary>
    public void AddToolMessage(string content, string? toolCallId = null)
    {
        _messages.Add(new ContextMessage
        {
            Role = "tool",
            Content = content,
            ToolCallId = toolCallId
        });
    }

    /// <summary>
    /// 添加系统消息
    /// </summary>
    public void AddSystemMessage(string content)
    {
        _messages.Add(new ContextMessage
        {
            Role = "system",
            Content = content
        });
    }

    /// <summary>
    /// 清空消息列表（不包括人格和摘要）
    /// </summary>
    public void Clear()
    {
        _messages.Clear();
    }

    /// <summary>
    /// 彻底清空（包括摘要，不包括人格）
    /// </summary>
    public void Reset()
    {
        _messages.Clear();
        _summary = null;
    }

    /// <summary>
    /// 估算单条消息的 token 数量
    /// 使用保守估算：2 字符/token（适用于中英文混合内容）
    /// </summary>
    public static int EstimateTokens(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;
        // 保守估算：2 字符/token，加上消息格式开销
        return content.Length / 2 + 10;
    }

    /// <summary>
    /// 估算当前 token 数量
    /// </summary>
    public int EstimatedTokenCount
    {
        get
        {
            int total = EstimateTokens(_mainPersona);
            if (!string.IsNullOrEmpty(_summary))
            {
                total += EstimateTokens(_summary);
            }
            foreach (var msg in _messages)
            {
                total += EstimateTokens(msg.Content);
            }
            return total;
        }
    }

    /// <summary>
    /// 计算需要保留多少条消息才能使 token 数低于目标阈值
    /// </summary>
    /// <param name="targetThreshold">目标 token 阈值</param>
    /// <returns>需要保留的最近消息数量</returns>
    public int CalculateKeepCount(int targetThreshold)
    {
        if (_messages.Count == 0)
            return 0;

            // 扣除固定成本
        var fixedCost = EstimateTokens(_mainPersona) + EstimateTokens(_summary);
        var availableTokens = (int)(targetThreshold * 0.8) - fixedCost;
        
        if (availableTokens <= 0) return 1;

        int accumulatedTokens = 0;
        int keepCount = 0;

        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            var msgTokens = EstimateTokens(_messages[i].Content);
            if (accumulatedTokens + msgTokens > availableTokens)
                break;

            accumulatedTokens += msgTokens;
            keepCount++;
        }

        // 至少保留 1 条消息
        return Math.Max(1, keepCount);
    }

    /// <summary>
    /// 是否需要压缩
    /// </summary>
    public bool NeedsCompression(int threshold)
    {
        return EstimatedTokenCount > threshold;
    }
}

/// <summary>
/// 上下文消息
/// </summary>
public class ContextMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string? ToolCallsJson { get; set; }
}
