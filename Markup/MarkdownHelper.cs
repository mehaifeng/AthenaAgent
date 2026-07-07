using System;
using Avalonia;
using LiveMarkdown.Avalonia;

namespace Athena.UI.Markup;

public class MarkdownHelper
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<MarkdownHelper, MarkdownRenderer, string?>("Text");

    static MarkdownHelper()
    {
        TextProperty.Changed.AddClassHandler<MarkdownRenderer>((renderer, e) =>
        {
            var text = e.NewValue as string ?? string.Empty;
            var old = e.OldValue as string ?? string.Empty;

            if (renderer.MarkdownBuilder == null)
            {
                renderer.MarkdownBuilder = new ObservableStringBuilder();
                renderer.MarkdownBuilder.Append(text);
                return;
            }

            // 流式输出下新值几乎总是"旧值 + 增量"，只 Append 差量让下游走增量解析
            if (text.Length > old.Length && text.StartsWith(old, StringComparison.Ordinal))
            {
                renderer.MarkdownBuilder.Append(text.Substring(old.Length));
                return;
            }

            if (string.Equals(text, old, StringComparison.Ordinal))
            {
                return;
            }

            // 非追加式变更（重置、fork、回滚）走全量重建
            renderer.MarkdownBuilder.Clear();
            renderer.MarkdownBuilder.Append(text);
        });
    }

    public static string? GetText(MarkdownRenderer element) => element.GetValue(TextProperty);
    public static void SetText(MarkdownRenderer element, string? value) => element.SetValue(TextProperty, value);
}
