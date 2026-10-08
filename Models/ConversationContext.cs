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
    private string? _summary;

    public string ConversationId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>创建本次请求快照时对应的会话持久化修订号。</summary>
    public long Revision { get; set; }

    /// <summary>当前工作区 ID（null 表示未绑定工作区）</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>当前工作区目录路径（注入 system prompt）</summary>
    public string? WorkspaceDirectoryPath { get; set; }

    /// <summary>系统管理的工作区知识文件绝对路径（注入 system prompt）</summary>
    public string? WorkspaceKnowledgeFilePath { get; set; }

    /// <summary>
    /// 本会话已观测到的真实用量锚点（按前缀长度升序）。它不是消息状态，因此 <see cref="Clear"/>
    /// 不清空它——UpdateConversationContext 会反复重建消息列表，但测量结果必须跨重建存活。
    /// </summary>
    public List<ContextAnchorRecord> Anchors { get; set; } = new();

    /// <summary>
    /// 已被「工具结果清理」换成占位说明的工具消息 ID。与 <see cref="Anchors"/> 一样是会话状态而非消息状态：
    /// <see cref="Clear"/> 不清它，<see cref="Reset"/> 才清。请求投影按它把旧工具结果换成占位文本。
    /// </summary>
    public List<string> ClearedToolResultIds { get; set; } = new();

    /// <summary>
    /// 自动全量压缩的防抖门槛（token）：上次压缩提交后若压缩后的估算仍不低于阈值，说明再压也压不下去，
    /// 此时只有当前用量涨过这个值（压缩后用量 + 阈值的 1/4）才允许再次自动压缩，避免每轮都重压。
    /// 0 表示没有门槛。会话内状态，不落盘；<see cref="Reset"/> 清零。
    /// </summary>
    public long AutoCompactionFloorTokens { get; set; }

    /// <summary>
    /// 刚提交过压缩、还在等第一次实测。压缩后的用量只有下一次响应的 usage 才知道（本地不估算），
    /// 到那时若仍不低于阈值，就按 <see cref="AutoCompactionFloorTokens"/> 设防抖门槛。会话内状态，不落盘。
    /// </summary>
    public bool PostCompactionMeasurePending { get; set; }

    /// <summary>
    /// 刚做过一次工具结果清理、还在等第一次实测。到那时若用量仍高于阈值，说明光清理压不下来，
    /// 置 <see cref="CompactionDueAfterClearing"/>。会话内状态，不落盘；<see cref="Reset"/> 清零。
    /// </summary>
    public bool PostClearingMeasurePending { get; set; }

    /// <summary>
    /// 清理之后的第一次实测仍高于阈值：下一次超阈值时直接进入全量压缩，不再因为「又有新的可清项」而只做清理。
    /// 工具密集的长任务每一轮都会把更早的结果挤出保留区，若可清项总是优先，压缩就永远轮不到，
    /// 而工具参数、正文、推理这些清理碰不到的内容会一路涨到供应商报超限。会话内状态，不落盘。
    /// </summary>
    public bool CompactionDueAfterClearing { get; set; }

    public void SetSummary(string? summary)
    {
        _summary = summary;
    }

    public string? Summary => _summary;

    public IReadOnlyList<ContextMessage> Messages => _messages.AsReadOnly();

    public void AddUserMessage(
        string content,
        DateTime? timestamp = null,
        IEnumerable<ChatAttachment>? attachments = null,
        string? id = null)
    {
        _messages.Add(new ContextMessage
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
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
        string? outputAudioReferenceId = null,
        string? id = null)
    {
        _messages.Add(new ContextMessage
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            Role = "assistant",
            Content = content,
            ToolCallsJson = toolCallsJson,
            ReasoningContent = reasoningContent,
            OutputAudioReferenceId = outputAudioReferenceId,
            Attachments = attachments?.Select(CloneAttachment).ToList() ?? new List<ChatAttachment>()
        });
    }

    public void AddToolMessage(string content, string? toolCallId = null, string? id = null)
    {
        _messages.Add(new ContextMessage
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            Role = "tool",
            Content = content,
            ToolCallId = toolCallId
        });
    }

    public void AddSystemMessage(string content, string? id = null)
    {
        _messages.Add(new ContextMessage
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            Role = "system",
            Content = content
        });
    }

    public void Clear() => _messages.Clear();

    public void RemoveMessages(int count)
    {
        if (count <= 0) return;
        int toRemove = Math.Min(count, _messages.Count);
        _messages.RemoveRange(0, toRemove);
    }

    public bool RemoveMessagesById(IReadOnlyCollection<string> messageIds)
    {
        if (messageIds.Count == 0) return false;
        var ids = new HashSet<string>(messageIds, StringComparer.Ordinal);
        if (ids.Count != messageIds.Count || ids.Any(id => _messages.All(message => message.Id != id)))
            return false;
        _messages.RemoveAll(message => ids.Contains(message.Id));
        return true;
    }

    public void Reset()
    {
        _messages.Clear();
        _summary = null;
        Anchors = new List<ContextAnchorRecord>();
        ClearedToolResultIds = new List<string>();
        AutoCompactionFloorTokens = 0;
        PostCompactionMeasurePending = false;
        PostClearingMeasurePending = false;
        CompactionDueAfterClearing = false;
    }

    public ConversationContext Clone()
    {
        var clone = new ConversationContext
        {
            ConversationId = ConversationId,
            Revision = Revision,
            WorkspaceId = WorkspaceId,
            WorkspaceDirectoryPath = WorkspaceDirectoryPath,
            WorkspaceKnowledgeFilePath = WorkspaceKnowledgeFilePath,
            Anchors = new List<ContextAnchorRecord>(Anchors),
            ClearedToolResultIds = new List<string>(ClearedToolResultIds),
            AutoCompactionFloorTokens = AutoCompactionFloorTokens,
            PostCompactionMeasurePending = PostCompactionMeasurePending,
            PostClearingMeasurePending = PostClearingMeasurePending,
            CompactionDueAfterClearing = CompactionDueAfterClearing
        };

        clone.SetSummary(_summary);

        foreach (var message in _messages)
        {
            switch (message.Role)
            {
                case "user":
                    clone.AddUserMessage(message.Content, message.Timestamp, message.Attachments, message.Id);
                    break;
                case "assistant":
                    clone.AddAssistantMessage(
                        message.Content,
                        message.ToolCallsJson,
                        message.ReasoningContent,
                        message.Attachments,
                        message.OutputAudioReferenceId,
                        message.Id);
                    break;
                case "tool":
                    clone.AddToolMessage(message.Content, message.ToolCallId, message.Id);
                    break;
                case "system":
                    clone.AddSystemMessage(message.Content, message.Id);
                    break;
            }
        }

        return clone;
    }

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
            FileCreatedAt = attachment.FileCreatedAt,
            FileModifiedAt = attachment.FileModifiedAt,
            Duration = attachment.Duration,
        };
    }
}

public class ContextMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ToolCallId { get; set; }
    public string? ToolCallsJson { get; set; }
    public string? ReasoningContent { get; set; }
    public string? OutputAudioReferenceId { get; set; }
    public DateTime Timestamp { get; set; }
    public List<ChatAttachment> Attachments { get; set; } = new();
}
