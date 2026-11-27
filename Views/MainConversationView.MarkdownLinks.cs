using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Markup;
using Athena.UI.Services;
using Athena.UI.Services.Preview;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace Athena.UI.Views;

public partial class MainConversationView
{
    private Action? _cancelPreview;
    private MarkdownTextBlock? _previewBlock;
    private Link? _previewLink;
    private Point _linkPointer;

    private void TrackLinkPointer(object? sender, PointerEventArgs e)
    {
        if (!e.Pointer.IsPrimary) return;
        _linkPointer = e.GetPosition(this);
        if (MarkdownLinkPreviewPopup.IsOpen && _previewBlock is { } block && _previewLink is { } link &&
            MarkdownLinkPreviewPopup.PlacementRect?.Contains(e.GetPosition(block)) != true)
            PositionLinkPreview(block, link);
    }

    private bool PositionLinkPreview(MarkdownTextBlock block, Link link)
    {
        if (this.TranslatePoint(_linkPointer, block) is not { } point || block.GetInlineGeometry(link) is not { } geometry)
            return false;
        var rect = geometry.Rects.FirstOrDefault(r => r.Contains(point));
        if (rect == default) return false;
        MarkdownLinkPreviewPopup.PlacementTarget = block;
        MarkdownLinkPreviewPopup.PlacementRect = rect;
        return true;
    }

    private void OnMarkdownLinkHoverChanged(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not MarkdownTextBlock block) return;
        if (block.PointerOverLink == null && !ReferenceEquals(block, _previewBlock)) return;
        CloseLinkPreview();
        if (block.PointerOverLink is not { Image: null, HRef: { IsAbsoluteUri: true } uri } link ||
            !(uri.IsFile || MarkdownWebPreview.IsWebTarget(uri))) return;
        _previewBlock = block;
        _previewLink = link;
        link.PropertyChanged += OnPreviewLinkChanged;
        AsyncEventGuard.Run(() => ShowLinkPreviewAsync(block, link, uri), "MarkdownLinkPreview");
    }

    private void OnPreviewLinkChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Link.HRefProperty || e.Property == MarkdownConfiguration.LinkTitleProperty)
            CloseLinkPreview();
    }

    private async Task ShowLinkPreviewAsync(MarkdownTextBlock block, Link link, Uri uri)
    {
        using var lifetime = new CancellationTokenSource();
        Action cancel = lifetime.Cancel;
        _cancelPreview = cancel;
        try
        {
            await Task.Delay(400, lifetime.Token);
            if (!ReferenceEquals(block.PointerOverLink, link) || !block.IsEffectivelyVisible || !PositionLinkPreview(block, link)) return;
            var title = link.GetValue(MarkdownConfiguration.LinkTitleProperty);
            var address = uri.IsFile ? uri.LocalPath : uri.AbsoluteUri;
            var caption = string.IsNullOrWhiteSpace(title) ? link.Inlines.Text : title;
            LinkPreviewTitle.Text = string.IsNullOrWhiteSpace(caption) || caption == address
                ? (uri.IsFile ? Path.GetFileName(address) : uri.IdnHost) : caption;
            LinkPreviewAddress.Text = address;
            LinkPreviewDescription.IsVisible = false;
            MarkdownLinkPreviewPopup.IsOpen = true;
            if (uri.IsFile || App.Services == null) return;
            var page = await App.Services.GetRequiredService<MarkdownWebPreview>().ReadAsync(uri, lifetime.Token);
            if (lifetime.IsCancellationRequested || page == null) return;
            if (page.Title != null) LinkPreviewTitle.Text = page.Title;
            LinkPreviewDescription.Text = page.Description;
            LinkPreviewDescription.IsVisible = page.Description != null;
        }
        catch (OperationCanceledException) { } // Hover ended or the page exceeded the preview time budget.
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Serilog.Log.Debug("Markdown link preview unavailable ({ErrorType})", ex.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(cancel, _cancelPreview)) _cancelPreview = null;
        }
    }

    private void CloseLinkPreview()
    {
        _cancelPreview?.Invoke();
        _cancelPreview = null;
        if (_previewLink != null) _previewLink.PropertyChanged -= OnPreviewLinkChanged;
        _previewBlock = null;
        _previewLink = null;
        MarkdownLinkPreviewPopup.IsOpen = false;
        MarkdownLinkPreviewPopup.PlacementTarget = null;
    }
}
