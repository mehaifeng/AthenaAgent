using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace Athena.UI.Models;

/// <summary>
/// 聊天消息模型
/// </summary>
public partial class ChatMessage : ObservableObject
{
    /// <summary>
    /// 消息稳定标识（持久化），用于 fork 锚点等跨会话引用
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

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
    [NotifyPropertyChangedFor(nameof(LegacyMarkdownContent))]
    private string _role = string.Empty;

    /// <summary>
    /// 消息内容
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsBubbleVisible))]
    [NotifyPropertyChangedFor(nameof(ShouldShowLegacyMarkdown))]
    [NotifyPropertyChangedFor(nameof(LegacyMarkdownContent))]
    [NotifyPropertyChangedFor(nameof(CanGenerateAudio))]
    private string _content = string.Empty;

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
    [NotifyPropertyChangedFor(nameof(ShowThinkingIndicator))]
    [NotifyPropertyChangedFor(nameof(CanGenerateAudio))]
    private bool _isLoading;

    /// <summary>
    /// Whether the model is streaming text for a file-write tool call.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDefaultThinkingText))]
    [NotifyPropertyChangedFor(nameof(ShowComposingFileText))]
    private bool _isComposingFileText;

    /// <summary>
    /// 该气泡是否处于"回复进行中"。贯穿整个流式回复生命周期，与 IsLoading/内容解耦，
    /// 保证空气泡在 loading→内容 的切换缝隙里也不会塌缩消失。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBubbleVisible))]
    [NotifyPropertyChangedFor(nameof(CanGenerateAudio))]
    private bool _isStreaming;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioError))]
    [NotifyPropertyChangedFor(nameof(IsBubbleVisible))]
    private string _audioErrorMessage = string.Empty;

    /// <summary>
    /// 是否正在后台生成语音。与回复生命周期(IsSending)解耦：文本回复结束后
    /// 语音异步生成时为 true，仅驱动本气泡的"生成语音中"提示，不影响任何命令可用性。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBubbleVisible))]
    [NotifyPropertyChangedFor(nameof(CanGenerateAudio))]
    private bool _isGeneratingAudio;

    /// <summary>
    /// 语音功能是否开启（由 VM 依据 Config.ChatAudioEnabled 回填）。
    /// 仅决定「生成语音」按钮是否出现，不影响任何已有音频的播放。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerateAudio))]
    private bool _audioFeatureEnabled;

    /// <summary>
    /// 是否已参与压缩（归档进摘要）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisibleToUser))]
    [NotifyPropertyChangedFor(nameof(CanShowRewind))]
    private bool _isCompressed;

    /// <summary>
    /// 是否允许回滚/fork（仅 user 消息，发送/压缩期间由 VM 统一关闭）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowRewind))]
    private bool _canRewind;

    /// <summary>
    /// 是否显示回滚/fork 按钮（允许操作且未压缩）
    /// </summary>
    public bool CanShowRewind => CanRewind && !IsCompressed;

    /// <summary>
    /// 是否真正有可见内容需要展示
    /// </summary>
    public bool IsContentVisible => !string.IsNullOrWhiteSpace(Content);

    public bool HasAttachments => Attachments.Count > 0;

    public bool HasSegments => Segments.Count > 0;

    public bool UsesSegmentLayout => HasSegments;

    public bool HasImageSegments => Segments.Any(segment => segment.IsGeneratedImage);

    /// <summary>
    /// 无 Segment 布局时，user / assistant 消息统一使用 Markdown 渲染
    /// </summary>
    public bool ShouldShowLegacyMarkdown => !UsesSegmentLayout && IsContentVisible;

    /// <summary>非空时才实例化 legacy Markdown 模板。</summary>
    [JsonIgnore]
    public ChatMessage? LegacyMarkdownContent => ShouldShowLegacyMarkdown ? this : null;

    /// <summary>Segment 布局未启用时不创建无效子项。</summary>
    [JsonIgnore]
    public IEnumerable<ChatMessageSegment>? SegmentContent => UsesSegmentLayout ? Segments : null;

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
    public bool IsBubbleVisible => !IsHidden && Role != "system" && Role != "tool" && (IsContentVisible || HasSegments || HasAttachments || HasToolExecutionSummary || HasAudioError || IsLoading || IsStreaming || IsGeneratingAudio);

    /// <summary>
    /// 工具执行摘要提示
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolExecutionSummary))]
    [NotifyPropertyChangedFor(nameof(IsBubbleVisible))]
    [NotifyPropertyChangedFor(nameof(ShowThinkingIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowDefaultThinkingText))]
    [NotifyPropertyChangedFor(nameof(ShowComposingFileText))]
    private string _toolExecutionSummary = string.Empty;

    /// <summary>
    /// 工具名称（仅用于 UI 状态传递）
    /// </summary>
    [ObservableProperty]
    private string _toolName = string.Empty;

    public bool HasToolExecutionSummary => !string.IsNullOrEmpty(ToolExecutionSummary);

    /// <summary>
    /// 是否展示"思考中"回纹加载指示。动画样式类必须绑定此属性挂/摘，
    /// 不能只靠 IsVisible 隐藏：Avalonia 对不可见元素的关键帧动画仍会持续
    /// 驱动合成器逐帧渲染（AvaloniaUI/Avalonia#17139）。
    /// </summary>
    public bool ShowThinkingIndicator => IsLoading && !HasToolExecutionSummary;

    public bool ShowDefaultThinkingText => !HasToolExecutionSummary && !IsComposingFileText;

    public bool ShowComposingFileText => !HasToolExecutionSummary && IsComposingFileText;

    public bool HasAudioError => !string.IsNullOrWhiteSpace(AudioErrorMessage);

    /// <summary>该消息是否已带有语音附件。</summary>
    public bool HasAudioAttachment => Attachments.Any(a => a.IsAudio);

    /// <summary>
    /// 是否显示「生成语音」按钮：语音功能已开启、是有正文的助手消息、
    /// 尚无语音附件、且当前既未在生成语音、也不在流式回复中。
    /// 覆盖两类场景：语音生成被新消息挤掉后的手动补生成，以及历史消息补生成。
    /// </summary>
    public bool CanGenerateAudio =>
        AudioFeatureEnabled
        && Role == "assistant"
        && !string.IsNullOrWhiteSpace(Content)
        && !HasAudioAttachment
        && !IsGeneratingAudio
        && !IsLoading
        && !IsStreaming;

    /// <summary>
    /// 是否可以复制该消息
    /// </summary>
    public bool CanCopy => true;

    /// <summary>
    /// 显示文本；Segment 布局启用时 legacy renderer 不可见，返回空避免其做无效 Markdown 解析。
    /// </summary>
    public string DisplayText => UsesSegmentLayout ? string.Empty : Content;

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
        OnPropertyChanged(nameof(HasAudioAttachment));
        OnPropertyChanged(nameof(CanGenerateAudio));
    }

    public void NotifySegmentsChanged()
    {
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(UsesSegmentLayout));
        OnPropertyChanged(nameof(DisplayText)); // DisplayText 随布局切换取值
        OnPropertyChanged(nameof(HasImageSegments));
        OnPropertyChanged(nameof(ShouldShowLegacyMarkdown));
        OnPropertyChanged(nameof(LegacyMarkdownContent));
        OnPropertyChanged(nameof(SegmentContent));
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
