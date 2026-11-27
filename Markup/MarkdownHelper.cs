using System;
using Avalonia;
using LiveMarkdown.Avalonia;

namespace Athena.UI.Markup;

public class MarkdownHelper
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<MarkdownHelper, MarkdownRenderer, string?>("Text");

    public static readonly AttachedProperty<string?> SearchTextProperty =
        AvaloniaProperty.RegisterAttached<MarkdownHelper, StyledElement, string?>("SearchText", inherits: true);

    public static readonly AttachedProperty<bool> IsStreamingProperty =
        AvaloniaProperty.RegisterAttached<MarkdownHelper, MarkdownRenderer, bool>("IsStreaming");

    static MarkdownHelper()
    {
        TextProperty.Changed.AddClassHandler<MarkdownRenderer>((renderer, e) =>
        {
            var text = e.NewValue as string ?? string.Empty;
            var old = e.OldValue as string ?? string.Empty;
            if (text == old) return;

            var cached = GetIsStreaming(renderer) ? null : ChatMarkdownDocuments.Find(text);
            if (text.Length == 0 || cached != null)
            {
                // Detach the producer first so a queued parse cannot overwrite a cached document.
                renderer.MarkdownBuilder = null;
                renderer.DocumentUpdate = cached;
            }
            else if (renderer.MarkdownBuilder is { } builder && text.Length > old.Length &&
                     text.StartsWith(old, StringComparison.Ordinal))
            {
                builder.Append(text[old.Length..]);
            }
            else
            {
                // A new source produces a Full update, including for a rewind or replacement.
                renderer.DocumentUpdate = null;
                renderer.MarkdownBuilder = new ObservableStringBuilder(text);
            }
        });

        MarkdownRenderer.DocumentUpdateProperty.Changed.AddClassHandler<MarkdownRenderer>((renderer, _) => RememberCompleted(renderer));
        IsStreamingProperty.Changed.AddClassHandler<MarkdownRenderer>((renderer, _) => RememberCompleted(renderer));
        SearchTextProperty.Changed.AddClassHandler<MarkdownRenderer>((renderer, e) =>
            renderer.ApplyTextSearch((e.NewValue as string)?.Trim()));
    }

    private static void RememberCompleted(MarkdownRenderer renderer)
    {
        if (!GetIsStreaming(renderer) && GetText(renderer) is { Length: > 0 } text
            && renderer.DocumentUpdate is { } update && renderer.MarkdownBuilder?.Version == update.Version)
            ChatMarkdownDocuments.Remember(text, update);
    }

    public static string? GetText(MarkdownRenderer element) => element.GetValue(TextProperty);
    public static void SetText(MarkdownRenderer element, string? value) => element.SetValue(TextProperty, value);
    public static string? GetSearchText(StyledElement element) => element.GetValue(SearchTextProperty);
    public static void SetSearchText(StyledElement element, string? value) => element.SetValue(SearchTextProperty, value);
    public static bool GetIsStreaming(MarkdownRenderer element) => element.GetValue(IsStreamingProperty);
    public static void SetIsStreaming(MarkdownRenderer element, bool value) => element.SetValue(IsStreamingProperty, value);
}
