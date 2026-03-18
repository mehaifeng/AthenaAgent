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
            if (renderer.MarkdownBuilder == null)
            {
                renderer.MarkdownBuilder = new ObservableStringBuilder();
            }
            // Workaround: ObservableStringBuilder might only have Append/Clear or we just recreate it
            renderer.MarkdownBuilder.Clear();
            renderer.MarkdownBuilder.Append(text);
        });
    }

    public static string? GetText(MarkdownRenderer element) => element.GetValue(TextProperty);
    public static void SetText(MarkdownRenderer element, string? value) => element.SetValue(TextProperty, value);
}
