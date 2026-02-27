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

    public ConversationContext(int maxTokens = 8000)
    {
        _maxTokens = maxTokens;
    }

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
    public void AddAssistantMessage(string content)
    {
        _messages.Add(new ContextMessage
        {
            Role = "assistant",
            Content = content
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
    /// 清空上下文
    /// </summary>
    public void Clear()
    {
        _messages.Clear();
    }

    /// <summary>
    /// 估算单条消息的 token 数量
    /// 使用保守估算：2 字符/token（适用于中英文混合内容）
    /// </summary>
    public static int EstimateTokens(string content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;
        // 保守估算：2 字符/token，加上消息格式开销
        return content.Length / 2 + 10;
    }

    /// <summary>
    /// 估算当前 token 数量（保守估算：2 字符/token）
    /// </summary>
    public int EstimatedTokenCount
    {
        get
        {
            int total = 0;
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

        // 从最新消息向前累加，直到达到阈值的 80%
        var targetTokens = (int)(targetThreshold * 0.8);
        int accumulatedTokens = 0;
        int keepCount = 0;

        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            var msgTokens = EstimateTokens(_messages[i].Content);
            if (accumulatedTokens + msgTokens > targetTokens)
                break;

            accumulatedTokens += msgTokens;
            keepCount++;
        }

        // 至少保留 2 条消息（一轮对话）
        return Math.Max(2, keepCount);
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
}
