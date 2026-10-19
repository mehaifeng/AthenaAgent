using Athena.UI.Models;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Athena.UI.Controls;

/// <summary>
/// 过程段里的一行：思考段（<see cref="ChatMessageSegment"/>）或一次工具调用（<see cref="ToolCallEntry"/>）。
/// 用选择器而不是按类型匹配的 DataTemplates：应用级的 ViewLocator 也在 DataTemplates 里，
/// 交给类型查找就要赌它不认这两个 ObservableObject。
/// </summary>
public class ProcessRowTemplateSelector : IDataTemplate
{
    public IDataTemplate? ReasoningTemplate { get; set; }

    public IDataTemplate? ToolCallTemplate { get; set; }

    public bool Match(object? data) => data is ToolCallEntry or ChatMessageSegment;

    public Control? Build(object? param) => param switch
    {
        ToolCallEntry entry => ToolCallTemplate?.Build(entry),
        ChatMessageSegment segment => ReasoningTemplate?.Build(segment),
        _ => null
    };
}
