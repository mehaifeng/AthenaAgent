using Avalonia.Controls;

namespace Athena.UI.Controls;

/// <summary>
/// 一轮思考的过程行：灯泡 + 「思考过程」，点击展开该轮的推理文本。
/// DataContext 为 <see cref="Models.ChatMessageSegment"/>（Kind = Reasoning）。
/// 与 <see cref="ToolCallRowView"/> 共用 App.axaml 里的过程行样式，
/// 让思考与工具在气泡里是同一种视觉语法。
/// </summary>
public partial class ReasoningRowView : UserControl
{
    public ReasoningRowView()
    {
        InitializeComponent();
    }
}
