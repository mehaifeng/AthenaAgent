using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athena.UI.Models;

public enum ChatMessageSegmentKind
{
    Markdown = 0,
    GeneratedImage = 1,
    ToolCallGroup = 2,
    // 数字序列化：新成员只能追加在末尾，否则老归档的 kind 会错位。
    Reasoning = 3
}

/// <summary>
/// 工具调用项的执行状态
/// </summary>
public enum ToolCallStatus
{
    Running = 0,
    Success = 1,
    Failed = 2
}

public partial class ChatMessageSegment : ObservableObject
{
    /// <summary>追加期间尾随窗口的字符预算，同时也是「需要限高」的阈值</summary>
    private const int ClampCharBudget = 480;

    /// <summary>限高时展示的最大行数（0 = 不限）</summary>
    private const int ClampMaxLines = 12;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMarkdown))]
    [NotifyPropertyChangedFor(nameof(IsGeneratedImage))]
    [NotifyPropertyChangedFor(nameof(IsToolCallGroup))]
    [NotifyPropertyChangedFor(nameof(IsReasoning))]
    [NotifyPropertyChangedFor(nameof(MarkdownContent))]
    [NotifyPropertyChangedFor(nameof(GeneratedImageContent))]
    [NotifyPropertyChangedFor(nameof(ToolCallContent))]
    [NotifyPropertyChangedFor(nameof(ReasoningContent))]
    private ChatMessageSegmentKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsClamp))]
    [NotifyPropertyChangedFor(nameof(VisibleText))]
    [NotifyPropertyChangedFor(nameof(TextMaxLines))]
    [NotifyPropertyChangedFor(nameof(ShowClampToggle))]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _attachmentId;

    [ObservableProperty]
    [property: JsonIgnore]
    private ChatAttachment? _attachment;

    // ===== 工具调用组相关 =====

    /// <summary>
    /// 该组包含的工具调用项。一个组 = 模型一轮里发出的全部调用（并行调用同属一组），
    /// 组本身不再是可折叠的 UI 区域，只是「同一轮」这个事实的载体。
    /// </summary>
    public ObservableCollection<ToolCallEntry> ToolCalls { get; set; } = new();

    // ===== 单段折叠态（思考段用；工具行的折叠态在 ToolCallEntry 上） =====

    /// <summary>本段是否展开。展开态是当下的浏览状态，不进归档。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(ChevronIconKey))]
    [NotifyPropertyChangedFor(nameof(ExpandedReasoning))]
    private bool _isExpanded;

    /// <summary>
    /// 用户是否手动切换过本段的展开态。为真时回合结束不再自动收起，
    /// 尊重「我正在读这段思考」的意图。不参与持久化。
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _userToggled;

    /// <summary>
    /// 当前是否正在向本段追加推理增量。仅驱动灯泡动画与尾随窗口，回合结束立即复位。
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(VisibleText))]
    [NotifyPropertyChangedFor(nameof(TextMaxLines))]
    [NotifyPropertyChangedFor(nameof(ShowClampToggle))]
    private bool _isAppending;

    /// <summary>长文本是否处于限高态（默认限高，点「显示全部」解除）。不参与持久化。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(VisibleText))]
    [NotifyPropertyChangedFor(nameof(TextMaxLines))]
    private bool _isClamped = true;

    [RelayCommand]
    private void ToggleExpanded()
    {
        UserToggled = true;
        IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    private void ToggleClamp() => IsClamped = !IsClamped;

    [JsonIgnore]
    public bool IsMarkdown => Kind == ChatMessageSegmentKind.Markdown;

    [JsonIgnore]
    public bool IsGeneratedImage => Kind == ChatMessageSegmentKind.GeneratedImage;

    [JsonIgnore]
    public bool IsToolCallGroup => Kind == ChatMessageSegmentKind.ToolCallGroup;

    [JsonIgnore]
    public bool IsReasoning => Kind == ChatMessageSegmentKind.Reasoning;

    [JsonIgnore]
    public ChatMessageSegment? MarkdownContent => IsMarkdown ? this : null;

    [JsonIgnore]
    public ChatMessageSegment? GeneratedImageContent => IsGeneratedImage ? this : null;

    [JsonIgnore]
    public ChatMessageSegment? ToolCallContent => IsToolCallGroup ? this : null;

    [JsonIgnore]
    public ChatMessageSegment? ReasoningContent => IsReasoning ? this : null;

    /// <summary>收起态返回 null：折叠的思考段不实例化正文子树。</summary>
    [JsonIgnore]
    public ChatMessageSegment? ExpandedReasoning => IsReasoning && IsExpanded ? this : null;

    /// <summary>折叠指示换几何而不是旋转（PathIcon 的 RenderTransform 在本项目不生效）。</summary>
    [JsonIgnore]
    public string ChevronIconKey => IsExpanded ? "AthenaIconChevronDown" : "AthenaIconChevronRight";

    /// <summary>
    /// 文本是否长到需要限高。阈值判断放在模型层（按字符数），布局回合里绝不回写属性——
    /// 在 Measure/Arrange 里改属性会触发无限布局循环。
    /// </summary>
    [JsonIgnore]
    public bool NeedsClamp => Text.Length > ClampCharBudget;

    /// <summary>
    /// 限高时实际渲染的文本。追加期间跟随尾部：正在读的是最新的思考，而不是被冻结的开头。
    /// </summary>
    [JsonIgnore]
    public string VisibleText => IsClamped && IsAppending && NeedsClamp
        ? Text[^ClampCharBudget..]
        : Text;

    /// <summary>0 表示不限行数。追加期间由尾随窗口控高，不再叠加行数限制。</summary>
    [JsonIgnore]
    public int TextMaxLines => IsClamped && !IsAppending && NeedsClamp ? ClampMaxLines : 0;

    /// <summary>追加期间不显示「显示全部」：它会和正在滚动的尾随窗口打架。</summary>
    [JsonIgnore]
    public bool ShowClampToggle => NeedsClamp && !IsAppending;
}
