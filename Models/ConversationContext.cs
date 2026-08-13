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

    /// <summary>创建本次请求快照时对应的会话持久化修订号。</summary>
    public long Revision { get; set; }

    /// <summary>当前工作区 ID（null 表示未绑定工作区）</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>当前工作区目录路径（注入 system prompt）</summary>
    public string? WorkspaceDirectoryPath { get; set; }

    /// <summary>系统管理的工作区知识文件绝对路径（注入 system prompt）</summary>
    public string? WorkspaceKnowledgeFilePath { get; set; }

    public int ToolsDeclarationTokenCount { get; set; } = 0;

    /// <summary>
    /// 本会话已观测到的真实用量锚点（按前缀长度升序）。它不是消息状态，因此 <see cref="Clear"/>
    /// 不清空它——UpdateConversationContext 会反复重建消息列表，但测量结果必须跨重建存活。
    /// </summary>
    public List<ContextAnchorRecord> Anchors { get; set; } = new();

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
    }

    public ConversationContext Clone()
    {
        var clone = new ConversationContext(_maxTokens)
        {
            ToolsDeclarationTokenCount = ToolsDeclarationTokenCount,
            ConversationId = ConversationId,
            Revision = Revision,
            WorkspaceId = WorkspaceId,
            WorkspaceDirectoryPath = WorkspaceDirectoryPath,
            WorkspaceKnowledgeFilePath = WorkspaceKnowledgeFilePath,
            Anchors = new List<ContextAnchorRecord>(Anchors)
        };

        clone.SetMainPersona(_mainPersona);
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

    // 延迟载入的附件只在上下文里放一张“清单卡”（路径+元信息+预览），固定开销近似值。
    public const int AttachmentManifestTokenCost = 180;

    // 文本 token 估算：CJK 字符约 1 token/字，拉丁/数字/标点约 1 token / 4 字符。
    // 旧实现统一 length/2 对中文低估 ~2-3 倍、对英文高估 ~2 倍，这里按脚本分别计。
    public static int EstimateTokens(string? content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        int cjk = 0, other = 0;
        foreach (var ch in content)
        {
            if (IsCjk(ch)) cjk++;
            else other++;
        }
        return (int)Math.Ceiling(cjk * 1.0 + other / 4.0) + 10;
    }

    private static bool IsCjk(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF) ||   // CJK 统一汉字
        (ch >= 0x3400 && ch <= 0x4DBF) ||   // CJK 扩展 A
        (ch >= 0x3000 && ch <= 0x30FF) ||   // CJK 标点 / 假名
        (ch >= 0xFF00 && ch <= 0xFFEF) ||   // 全角字符
        (ch >= 0xAC00 && ch <= 0xD7A3);     // 韩文音节

    // 图片 token 估算：复刻 OpenAI vision 分块（fit 2048²→短边缩 768→512² 分块）。
    // 尺寸未知时退回保守扁平值，避免对不支持分块的 provider 误判。
    public static int EstimateImageTokens(int width, int height)
    {
        if (width <= 0 || height <= 0) return 1000;
        double s1 = Math.Min(1.0, 2048.0 / Math.Max(width, height));
        double w = width * s1, h = height * s1;
        double s2 = Math.Min(1.0, 768.0 / Math.Min(w, h));
        w *= s2; h *= s2;
        int tiles = (int)Math.Ceiling(w / 512.0) * (int)Math.Ceiling(h / 512.0);
        return 85 + 170 * tiles;
    }

    // 整段上下文的字符启发式估算。自「真实 usage 锚点」上线后，这里降级为兜底：
    // 仅在冷启动（尚无任何 API 响应）、供应商不回 usage、或压缩/回滚改了上下文却未发 API 时使用。
    // 正常路径下上下文占用以 TokenService 的真实锚点（供应商回报的 InputTokenCount）为准。
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
                total += msg.Attachments
                    .Where(a => a.Kind == AttachmentKind.Image)
                    .Sum(a => EstimateImageTokens(a.Width, a.Height));
                total += msg.Attachments.Count * AttachmentManifestTokenCost;
            }
            return total;
        }
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
