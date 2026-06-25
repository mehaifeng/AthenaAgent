using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athena.UI.Models;

/// <summary>
/// 单个工具调用项（属于某个 <see cref="ChatMessageSegment"/> 的工具调用组）。
/// 持有函数名、可读摘要、整形参数、结果预览与执行状态，并支持单卡展开详情。
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
    private string _arguments = string.Empty;

    /// <summary>工具结果预览</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _result = string.Empty;

    /// <summary>执行状态</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsSuccess))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private ToolCallStatus _status = ToolCallStatus.Running;

    /// <summary>单卡是否展开（显示参数与结果）</summary>
    [ObservableProperty]
    private bool _isExpanded;

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

    /// <summary>工具类别图标的 Semi 资源 key</summary>
    [JsonIgnore]
    public string IconKey => ToolCallDisplay.IconKey(Name);
}
