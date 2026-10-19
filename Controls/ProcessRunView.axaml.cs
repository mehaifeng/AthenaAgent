using Avalonia.Controls;

namespace Athena.UI.Controls;

/// <summary>
/// 一个过程段（<see cref="Models.ProcessRun"/>）：可折叠时先是一行摘要，下面是当前该显示的那几行。
/// 摘要与行的取舍全在模型里，这里只负责摆放。
/// </summary>
public partial class ProcessRunView : UserControl
{
    public ProcessRunView()
    {
        InitializeComponent();
    }
}
