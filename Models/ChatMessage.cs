using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Athena.UI.Models;

/// <summary>
/// 聊天消息模型
/// </summary>
public partial class ChatMessage : ObservableObject
{
    public ObservableCollection<ChatAttachment> Attachments { get; set; } = new();

    public ObservableCollection<ChatMessageSegment> Segments { get; set; } = new();

    /// <summary>
    /// 消息角色: user, assistant, system
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(IsUser))]
    [NotifyPropertyChangedFor(nameof(IsVisibleToUser))]
    [NotifyPropertyChangedFor(nameof(IsBubbleVisible))]
    [NotifyPropertyChangedFor(nameof(ShouldShowLegacyMarkdown))]
    private string _role = string.Empty;

    /// <summary>
    /// 消息内容
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsBubbleVisible))]
    [NotifyPropertyChangedFor(nameof(ShouldShowLegacyMarkdown))]
    private string _content = string.Empty;

    /// <summary>
    /// 编辑中的临时内容
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBubbleVisible))]
    [NotifyPropertyChangedFor(nameof(ShouldShowLegacyMarkdown))]
    private string _editContent = string.Empty;

    /// <summary>
    /// 时间戳
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimestampText))]
    private DateTime _timestamp = DateTime.Now;

    /// <summary>
    /// 是否为心跳消息（AI 主动发起）
    /// </summary>
    [ObservableProperty]
    private bool _isHeartbeat;

    /// <summary>
    /// 是否正在加载中
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBubbleVisible))]
    private bool _isLoading;

    /// <summary>
    /// 是否正在编辑中
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowEdit))]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsBubbleVisible))]
    [NotifyPropertyChangedFor(nameof(ShouldShowLegacyMarkdown))]
    private bool _isEditing;

    /// <summary>
    /// 工具调用 ID (仅用于 tool 角色消息)
    /// </summary>
    [ObservableProperty]
    private string? _toolCallId;

    /// <summary>
    /// 工具调用详情 (JSON 格式，用于 assistant 角色消息)
    /// </summary>
    [ObservableProperty]
    private string? _toolCallsJson;

    /// <summary>
    /// DeepSeek thinking 模式等模型要求回放的推理内容
    /// </summary>
    [ObservableProperty]
    private string? _reasoningContent;

    [ObservableProperty]
    private string? _outputAudioReferenceId;

    /// <summary>
    /// 是否已参与压缩（归档进摘要）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisibleToUser))]
    [NotifyPropertyChangedFor(nameof(CanShowEdit))]
    [NotifyPropertyChangedFor(nameof(CanShowRegenerate))]
    private bool _isCompressed;

    /// <summary>
    /// 是否可以编辑该消息
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowEdit))]
    private bool _canEdit;

    /// <summary>
    /// 是否可以重新生成回复（仅针对助手消息）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowRegenerate))]
    private bool _canRegenerate;

    /// <summary>
    /// 是否可以显示编辑按钮（非编辑状态且允许编辑且未压缩）
    /// </summary>
    public bool CanShowEdit => CanEdit && !IsEditing && !IsCompressed;

    /// <summary>
    /// 是否可以显示重新生成按钮（允许重新生成且未压缩）
    /// </summary>
    public bool CanShowRegenerate => CanRegenerate && !IsCompressed;

    /// <summary>
    /// 是否真正有可见内容需要展示（非编辑状态且内容不为空）
    /// </summary>
    public bool IsContentVisible => !IsEditing && !string.IsNullOrWhiteSpace(Content);

    public bool HasAttachments => Attachments.Count > 0;

    public bool HasSegments => Segments.Count > 0;

    public bool UsesSegmentLayout => HasSegments;

    public bool HasImageSegments => Segments.Any(segment => segment.IsGeneratedImage);

    public bool ShouldShowLegacyMarkdown => !UsesSegmentLayout && IsContentVisible;

    public IEnumerable<ChatAttachment> AttachmentPanelItems =>
        UsesSegmentLayout
            ? Attachments.Where(attachment => !attachment.IsImage)
            : Attachments;

    public bool ShouldShowAttachmentPanel => AttachmentPanelItems.Any();

    /// <summary>
    /// 是否强制隐藏（用于隐藏带有工具调用的中间助手消息）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBubbleVisible))]
    private bool _isHidden;

    /// <summary>
    /// 整体气泡是否可见（有内容、有工具执行提示，或正在加载）
    /// 注意：Role 为 tool 或 system 时强制不可见
    /// </summary>
    public bool IsBubbleVisible => !IsHidden && Role != "system" && Role != "tool" && (IsContentVisible || HasSegments || HasAttachments || HasToolExecutionSummary || IsLoading || IsEditing);

    /// <summary>
    /// 工具执行摘要提示
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolExecutionSummary))]
    [NotifyPropertyChangedFor(nameof(IsBubbleVisible))]
    private string _toolExecutionSummary = string.Empty;

    /// <summary>
    /// 工具名称（仅用于 UI 状态传递）
    /// </summary>
    [ObservableProperty]
    private string _toolName = string.Empty;

    public bool HasToolExecutionSummary => !string.IsNullOrEmpty(ToolExecutionSummary);

    /// <summary>
    /// 是否可以复制该消息
    /// </summary>
    public bool CanCopy => true;

    /// <summary>
    /// 显示文本（纯内容，不带前缀）
    /// </summary>
    public string DisplayText => Content;

    /// <summary>
    /// 时间戳显示格式
    /// </summary>
    public string TimestampText => Timestamp.ToString("[HH:mm:ss]");

    /// <summary>
    /// 角色显示图标
    /// </summary>
    public string RoleIcon => Role switch
    {
        "user" => ">",
        "assistant" => "<",
        "system" => "*",
        "error" => "!",
        _ => "-"
    };

    /// <summary>
    /// 是否为用户消息
    /// </summary>
    public bool IsUser => Role == "user";

    /// <summary>
    /// 是否在 UI 中可见（system 和 tool 消息只对 LLM 可见，不对用户显示）
    /// </summary>
    public bool IsVisibleToUser => Role != "system" && Role != "tool";

    public void NotifyAttachmentsChanged()
    {
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentPanelItems));
        OnPropertyChanged(nameof(ShouldShowAttachmentPanel));
        OnPropertyChanged(nameof(IsBubbleVisible));
    }

    public void NotifySegmentsChanged()
    {
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(UsesSegmentLayout));
        OnPropertyChanged(nameof(HasImageSegments));
        OnPropertyChanged(nameof(ShouldShowLegacyMarkdown));
        OnPropertyChanged(nameof(AttachmentPanelItems));
        OnPropertyChanged(nameof(ShouldShowAttachmentPanel));
        OnPropertyChanged(nameof(IsBubbleVisible));
    }

    public void ResolveSegmentAttachments()
    {
        foreach (var segment in Segments.Where(segment => segment.IsGeneratedImage))
        {
            segment.Attachment = Attachments.FirstOrDefault(attachment =>
                string.Equals(attachment.Id, segment.AttachmentId, StringComparison.Ordinal));
        }
    }
}
