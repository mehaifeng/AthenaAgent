using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athena.UI.Models;

public enum ChatMessageSegmentKind
{
    Markdown = 0,
    GeneratedImage = 1,
    ToolCall = 2
}

/// <summary>
/// 工具调用卡片的执行状态
/// </summary>
public enum ToolCallStatus
{
    Running = 0,
    Success = 1,
    Failed = 2
}

public partial class ChatMessageSegment : ObservableObject
{
    [ObservableProperty]
    private ChatMessageSegmentKind _kind;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _attachmentId;

    [ObservableProperty]
    [property: JsonIgnore]
    private ChatAttachment? _attachment;

    // ===== 工具调用卡片相关字段 =====

    /// <summary>
    /// 工具调用 ID，用于把工具结果回填到对应的卡片
    /// </summary>
    [ObservableProperty]
    private string? _toolCallId;

    /// <summary>
    /// 工具函数名（如 web_search）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolIconKey))]
    private string _toolName = string.Empty;

    /// <summary>
    /// 人类可读的一行摘要（如「搜索『Avalonia 12』」）
    /// </summary>
    [ObservableProperty]
    private string _toolSummary = string.Empty;

    /// <summary>
    /// 整形后的调用参数（JSON）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolArguments))]
    private string _toolArguments = string.Empty;

    /// <summary>
    /// 工具结果预览（成功时取 message/data，失败时取错误信息）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolResult))]
    private string _toolResult = string.Empty;

    /// <summary>
    /// 执行状态
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToolRunning))]
    [NotifyPropertyChangedFor(nameof(IsToolSuccess))]
    [NotifyPropertyChangedFor(nameof(IsToolFailed))]
    private ToolCallStatus _toolStatus = ToolCallStatus.Running;

    /// <summary>
    /// 卡片是否展开（显示参数与结果）
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded;

    [JsonIgnore]
    public bool IsMarkdown => Kind == ChatMessageSegmentKind.Markdown;

    [JsonIgnore]
    public bool IsGeneratedImage => Kind == ChatMessageSegmentKind.GeneratedImage;

    [JsonIgnore]
    public bool IsToolCall => Kind == ChatMessageSegmentKind.ToolCall;

    [JsonIgnore]
    public bool IsToolRunning => ToolStatus == ToolCallStatus.Running;

    [JsonIgnore]
    public bool IsToolSuccess => ToolStatus == ToolCallStatus.Success;

    [JsonIgnore]
    public bool IsToolFailed => ToolStatus == ToolCallStatus.Failed;

    [JsonIgnore]
    public bool HasToolArguments => !string.IsNullOrWhiteSpace(ToolArguments);

    [JsonIgnore]
    public bool HasToolResult => !string.IsNullOrWhiteSpace(ToolResult);

    /// <summary>
    /// 工具类别图标的 Semi 资源 key，按函数名归类
    /// </summary>
    [JsonIgnore]
    public string ToolIconKey => ToolCallDisplay.IconKey(ToolName);
}
