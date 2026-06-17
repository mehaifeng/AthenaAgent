using CommunityToolkit.Mvvm.ComponentModel;

namespace Athena.UI.Models;

/// <summary>
/// 调试用「原始上下文」中的一条消息块。拆分成独立条目可让选择只作用于单块、
/// 并配合虚拟化列表避免大文本下的卡顿；默认折叠，仅显示序号与角色。
/// </summary>
public sealed partial class RawContextEntry : ObservableObject
{
    /// <summary>角色类别：system / user / assistant / tool / error，用于标题着色。</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>形如 "[0] system" / "[2] tool (tool_call_id=…)" 的标题行。</summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>该消息的正文（已对工具调用/返回做 JSON 美化与转义还原）。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>是否展开正文。默认折叠。</summary>
    [ObservableProperty]
    private bool _isExpanded;
}
