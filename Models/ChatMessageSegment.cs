using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athena.UI.Models;

public enum ChatMessageSegmentKind
{
    Markdown = 0,
    GeneratedImage = 1,
    ToolCallGroup = 2
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
    public ChatMessageSegment()
    {
        ToolCalls.CollectionChanged += OnToolCallsChanged;
    }

    [ObservableProperty]
    private ChatMessageSegmentKind _kind;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _attachmentId;

    [ObservableProperty]
    [property: JsonIgnore]
    private ChatAttachment? _attachment;

    // ===== 工具调用组相关 =====

    /// <summary>
    /// 该组包含的工具调用项（按发生顺序）
    /// </summary>
    public ObservableCollection<ToolCallEntry> ToolCalls { get; set; } = new();

    /// <summary>
    /// 整组是否展开（自定义折叠容器的展开态）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroupCollapsed))]
    private bool _isGroupExpanded;

    /// <summary>
    /// 用户是否手动切换过整组折叠态（为真时不再自动收起；不参与持久化）
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _userToggledGroup;

    private void OnToolCallsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ToolCallCount));
        OnPropertyChanged(nameof(NeedsCollapse));
        OnPropertyChanged(nameof(IsGroupCollapsed));
    }

    [JsonIgnore]
    public bool IsMarkdown => Kind == ChatMessageSegmentKind.Markdown;

    [JsonIgnore]
    public bool IsGeneratedImage => Kind == ChatMessageSegmentKind.GeneratedImage;

    [JsonIgnore]
    public bool IsToolCallGroup => Kind == ChatMessageSegmentKind.ToolCallGroup;

    [JsonIgnore]
    public int ToolCallCount => ToolCalls.Count;

    /// <summary>
    /// 工具数超过 3 个时才需要折叠（露 3 个 + 第 4 个 peek）
    /// </summary>
    [JsonIgnore]
    public bool NeedsCollapse => ToolCalls.Count > 3;

    /// <summary>
    /// 当前是否处于收起态（需折叠且未展开）
    /// </summary>
    [JsonIgnore]
    public bool IsGroupCollapsed => NeedsCollapse && !IsGroupExpanded;

    /// <summary>
    /// 反序列化/克隆后重新挂接集合变更监听并刷新派生属性
    /// </summary>
    public void RehookToolCalls()
    {
        ToolCalls.CollectionChanged -= OnToolCallsChanged;
        ToolCalls.CollectionChanged += OnToolCallsChanged;
        OnPropertyChanged(nameof(ToolCallCount));
        OnPropertyChanged(nameof(NeedsCollapse));
        OnPropertyChanged(nameof(IsGroupCollapsed));
    }
}
