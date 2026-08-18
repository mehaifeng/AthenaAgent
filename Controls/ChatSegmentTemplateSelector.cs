using Athena.UI.Models;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Athena.UI.Controls;

/// <summary>
/// 按 <see cref="ChatMessageSegmentKind"/> 选模板，只构建命中的那一棵子树。
/// 之前的写法是一个段里并排放四个 <see cref="ContentControl"/>（Markdown / 思考 / 工具 / 生成图各一个，
/// 靠 Content 为 null 不实例化模板），但 ContentControl 自身和它的 ContentPresenter 照样存在：
/// 一条重对话有几百个段，光这三份空壳就是上千个控件，白白参与布局与命中测试。
/// </summary>
public class ChatSegmentTemplateSelector : IDataTemplate
{
    public IDataTemplate? MarkdownTemplate { get; set; }

    public IDataTemplate? ReasoningTemplate { get; set; }

    public IDataTemplate? ToolCallTemplate { get; set; }

    public IDataTemplate? GeneratedImageTemplate { get; set; }

    public bool Match(object? data) => data is ChatMessageSegment;

    public Control? Build(object? param)
    {
        if (param is not ChatMessageSegment segment)
        {
            return null;
        }

        var template = segment.Kind switch
        {
            ChatMessageSegmentKind.Markdown => MarkdownTemplate,
            ChatMessageSegmentKind.Reasoning => ReasoningTemplate,
            ChatMessageSegmentKind.ToolCallGroup => ToolCallTemplate,
            ChatMessageSegmentKind.GeneratedImage => GeneratedImageTemplate,
            _ => null
        };

        return template?.Build(segment);
    }
}
