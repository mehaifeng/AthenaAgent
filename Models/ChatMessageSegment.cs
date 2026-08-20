using System;
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

    /// <summary>
    /// 重切尾随窗口时一次性让出的字符数。窗口起点每前进一次，整块文本就左移一次、
    /// 换行位置随之全部重排；按「末尾 N 字」逐字重切等于每个增量都重排一遍——
    /// 顶部的字看上去也在变，读起来就是一片乱。攒够这么多字才重切一次，
    /// 两次重切之间起点纹丝不动，增量只在底部生长。
    /// </summary>
    private const int TrailingWindowSlack = 160;

    /// <summary>
    /// 尾随窗口保留的最大逻辑行数。字符预算挡不住高度：480 个字里塞满换行照样能撑出几十行。
    /// </summary>
    private const int TrailingWindowMaxLines = ClampMaxLines;

    /// <summary>重切时一次性让出的行数。同样是余量，否则每来一行就重切一次。</summary>
    private const int TrailingWindowLineSlack = 4;

    /// <summary>
    /// 向后找换行符的前瞻距离。切在行首时余下的行一字不改、只是整体上滚，
    /// 是唯一不会引起重排的切法；切在行中至少也要攒够余量才来一次。
    /// </summary>
    private const int TrailingWindowSnapLookahead = 120;

    /// <summary>
    /// 尾随窗口在 <see cref="Text"/> 中的起点。只前进不后退——后退会把已经划过去的
    /// 文字重新拉回来，比不裁剪还乱。
    /// </summary>
    private int _trailingWindowStart;

    /// <summary>
    /// <see cref="Text"/> 的逻辑行数缓存。追加时只数新增的那一截：限高判断读得很频繁，
    /// 每次增量重数全文就是一次 O(n²) 的空转。
    /// </summary>
    private int _lineCount = 1;

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
    /// 文本是否长到需要限高。阈值判断放在模型层（字符数或行数），布局回合里绝不回写属性——
    /// 在 Measure/Arrange 里改属性会触发无限布局循环。
    /// 行数这一半不能省：逐条列步骤的思考几百字就能排出几十行，字符预算根本挡不住高度。
    /// </summary>
    [JsonIgnore]
    public bool NeedsClamp => Text.Length > ClampCharBudget || _lineCount > ClampMaxLines;

    /// <summary>
    /// 限高时实际渲染的文本。追加期间跟随尾部：正在读的是最新的思考，而不是被冻结的开头。
    /// 起点由 <see cref="AdvanceTrailingWindow"/> 分段前进，不随每个增量滑动。
    /// </summary>
    [JsonIgnore]
    public string VisibleText => IsClamped && IsAppending && NeedsClamp
        ? Text[Math.Min(_trailingWindowStart, Text.Length)..]
        : Text;

    /// <summary>0 表示不限行数。追加期间由尾随窗口控高，不再叠加行数限制。</summary>
    [JsonIgnore]
    public int TextMaxLines => IsClamped && !IsAppending && NeedsClamp ? ClampMaxLines : 0;

    /// <summary>追加期间不显示「显示全部」：它会和正在滚动的尾随窗口打架。</summary>
    [JsonIgnore]
    public bool ShowClampToggle => NeedsClamp && !IsAppending;

    // 行数与窗口起点都在增量落地时就地更新：VisibleText / NeedsClamp 是纯读取属性，
    // 绝不能在取值时改状态（绑定求值发生在布局回合里，写属性会再次触发通知）。
    partial void OnTextChanged(string? oldValue, string newValue)
    {
        var appended = oldValue != null
            && newValue.Length >= oldValue.Length
            && newValue.AsSpan(0, oldValue.Length).SequenceEqual(oldValue);

        if (appended)
        {
            _lineCount += CountLineBreaks(newValue, oldValue!.Length);
        }
        else
        {
            // 整体替换（归档回放、改写）而不是追加：全部重来。
            _lineCount = CountLineBreaks(newValue, 0) + 1;
            _trailingWindowStart = 0;
        }

        _trailingWindowStart = AdvanceTrailingWindow(newValue, _trailingWindowStart);
    }

    /// <summary>
    /// 算出尾随窗口的新起点：只有窗口超出字符预算或行数上限时才前进一次，
    /// 前进时优先切在换行处——余下的行不会重新折行，界面上就是「整体上滚了几行」。
    /// </summary>
    private static int AdvanceTrailingWindow(string text, int start)
    {
        if (start > text.Length)
        {
            start = 0;
        }

        if (text.Length - start <= ClampCharBudget
            && CountLineBreaks(text, start) < TrailingWindowMaxLines)
        {
            return start;
        }

        var next = Math.Max(start, text.Length - (ClampCharBudget - TrailingWindowSlack));
        next = SnapToLineStart(text, next);
        next = Math.Max(next, LastLinesStart(text, TrailingWindowMaxLines - TrailingWindowLineSlack));

        // 切在代理对中间会留下半个字符（emoji 变豆腐块），往后挪一位补齐。
        if (next < text.Length && char.IsLowSurrogate(text[next]))
        {
            next++;
        }

        return next;
    }

    private static int CountLineBreaks(string text, int start) =>
        text.AsSpan(start).Count('\n');

    /// <summary>前瞻范围内有换行就切到它后面，没有就原地切（宁可重排一次，也不多丢一大段）。</summary>
    private static int SnapToLineStart(string text, int index)
    {
        var limit = Math.Min(text.Length, index + TrailingWindowSnapLookahead);
        if (limit <= index)
        {
            return index;
        }

        var lineBreak = text.IndexOf('\n', index, limit - index);
        return lineBreak >= 0 ? lineBreak + 1 : index;
    }

    /// <summary>从末尾往回数 <paramref name="maxLines"/> 行的起点。</summary>
    private static int LastLinesStart(string text, int maxLines)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var index = text.Length;
        for (var line = 0; line < maxLines; line++)
        {
            var lineBreak = text.LastIndexOf('\n', Math.Max(index - 1, 0));
            if (lineBreak <= 0)
            {
                return 0;
            }

            index = lineBreak;
        }

        return index + 1;
    }
}
