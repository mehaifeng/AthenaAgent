using Avalonia.Controls;

namespace Athena.UI.Controls;

/// <summary>
/// 单次工具调用的过程行：状态标识 + 摘要，点击展开参数与结果。
/// DataContext 为 <see cref="Models.ToolCallEntry"/>。
/// 折叠态与正文同字号、无卡片外观，让工具调用穿插在正文里而不撑高气泡。
/// </summary>
public partial class ToolCallRowView : UserControl
{
    public ToolCallRowView()
    {
        InitializeComponent();
    }
}
