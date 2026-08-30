using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athena.UI.Models;

/// <summary>
/// 单个工具调用项（属于某个 <see cref="ChatMessageSegment"/> 的工具调用组）。
/// 持有函数名、可读摘要、整形参数、结果预览与执行状态，并支持单行展开详情。
/// </summary>
public partial class ToolCallEntry : ObservableObject
{
    /// <summary>工具调用 ID，用于把工具结果回填到对应项</summary>
    [ObservableProperty]
    private string? _toolCallId;

    /// <summary>工具函数名（如 web_search）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconKey))]
    private string _name = string.Empty;

    /// <summary>人类可读的一行摘要</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>整形后的调用参数（JSON）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArguments))]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    [NotifyPropertyChangedFor(nameof(ExpandedDetails))]
    private string _arguments = string.Empty;

    /// <summary>工具结果预览</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    [NotifyPropertyChangedFor(nameof(ExpandedDetails))]
    private string _result = string.Empty;

    /// <summary>执行状态</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsSuccess))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(StatusIconKey))]
    private ToolCallStatus _status = ToolCallStatus.Running;

    /// <summary>单行是否展开（显示参数与结果）。展开态是当下的浏览状态，不进归档。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(ChevronIconKey))]
    [NotifyPropertyChangedFor(nameof(ExpandedDetails))]
    private bool _isExpanded;

    // 见 ChatMessageSegment：生成的 ICommand 属性不挡住就会进归档。
    [RelayCommand]
    [property: JsonIgnore]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [JsonIgnore]
    public bool IsRunning => Status == ToolCallStatus.Running;

    [JsonIgnore]
    public bool IsSuccess => Status == ToolCallStatus.Success;

    [JsonIgnore]
    public bool IsFailed => Status == ToolCallStatus.Failed;

    [JsonIgnore]
    public bool HasArguments => !string.IsNullOrWhiteSpace(Arguments);

    [JsonIgnore]
    public bool HasResult => !string.IsNullOrWhiteSpace(Result);

    /// <summary>没有参数也没有结果时不给展开箭头：点开一片空白只会让人以为坏了。</summary>
    [JsonIgnore]
    public bool HasDetails => HasArguments || HasResult;

    /// <summary>
    /// 状态图标用一个 PathIcon 按 key 切换，而不是三个图标各自 IsVisible。
    /// 一条重对话里工具行数以百计，每行省两个图标就是省几百个控件。
    /// </summary>
    [JsonIgnore]
    public string StatusIconKey => Status switch
    {
        ToolCallStatus.Success => "AthenaIconSuccess",
        ToolCallStatus.Failed => "AthenaIconFailure",
        _ => "AthenaIconLoading"
    };

    /// <summary>折叠指示同理：换几何而不是换元素（PathIcon 的旋转在本项目不生效）。</summary>
    [JsonIgnore]
    public string ChevronIconKey => IsExpanded ? "AthenaIconChevronDown" : "AthenaIconChevronRight";

    /// <summary>
    /// 收起态返回 null，让 ContentControl 根本不实例化参数/结果子树。
    /// 收起的行是绝大多数，这是气泡里最大的一块无谓开销。
    /// 无详情时也返回 null：否则点一下没有参数也没有结果的行，会展开出一条空的竖线。
    /// </summary>
    [JsonIgnore]
    public ToolCallEntry? ExpandedDetails => IsExpanded && HasDetails ? this : null;

    /// <summary>工具类别图标在图标契约（Styles/AppIcons.axaml）里的资源 key</summary>
    [JsonIgnore]
    public string IconKey => ToolCallDisplay.IconKey(Name);
}
