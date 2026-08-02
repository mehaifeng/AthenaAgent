using CommunityToolkit.Mvvm.ComponentModel;

namespace Athena.UI.Models;

/// <summary>
/// 调试用「原始上下文」中的一条消息块。拆分成独立条目可让选择只作用于单块、
/// 并配合虚拟化列表避免大文本下的卡顿；默认折叠，仅显示序号与角色。
/// </summary>
public sealed partial class RawContextEntry : ObservableObject
{
    private const int PreviewCharacterLimit = 8_000;

    /// <summary>角色类别：system / user / assistant / tool / error，用于标题着色。</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>形如 "[0] system" / "[2] tool (tool_call_id=…)" 的标题行。</summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>当前显示正文。折叠时只发布短预览，展开时才发布完整内容。</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    public string FullText { get; init; } = string.Empty;

    public bool IsTruncated => FullText.Length > PreviewCharacterLimit;

    public string PreviewText => IsTruncated
        ? FullText[..PreviewCharacterLimit] + "\n\n…"
        : FullText;

    /// <summary>是否展开正文。默认折叠。</summary>
    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
        => Text = value ? FullText : PreviewText;

    public void InitializePreview() => Text = PreviewText;
}
