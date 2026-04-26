using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Browser;

public class SomAnnotator : ISomAnnotator
{
    private const string BrowserScreenshotDirectoryName = "Browser";
    private const string ScreenshotDirectoryName = "Screenshots";
    private const string ScreenshotExtension = ".png";

    private readonly ILogger _logger;
    private readonly IPlatformPathService _pathService;
    private readonly ConcurrentDictionary<string, ScreenshotSaveState> _lastSavedScreenshots = new();

    public SomAnnotator(IPlatformPathService pathService, ILogger logger)
    {
        _pathService = pathService;
        _logger = logger.ForContext<SomAnnotator>();
    }

    public async Task<SomObservation> AnnotateAsync(SomAnnotationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var bitmap = SKBitmap.Decode(request.ScreenshotPng);
            if (bitmap == null)
            {
                return await CreateObservationAsync(request, request.ScreenshotPng, cancellationToken);
            }

            using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height, bitmap.ColorType, bitmap.AlphaType));
            var canvas = surface.Canvas;
            canvas.DrawBitmap(bitmap, 0, 0);

            var scaleX = request.ViewportWidth > 0 ? bitmap.Width / (float)request.ViewportWidth : 1f;
            var scaleY = request.ViewportHeight > 0 ? bitmap.Height / (float)request.ViewportHeight : 1f;

            using var borderPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(255, 71, 87),
                StrokeWidth = Math.Max(2, bitmap.Width / 640f),
                IsAntialias = true
            };
            using var labelPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(255, 71, 87),
                IsAntialias = true
            };
            using var textPaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true
            };
            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
            using var labelFont = new SKFont(typeface, Math.Max(13, bitmap.Width / 80f));

            foreach (var element in request.Elements.Take(Math.Max(0, request.MaxElements)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DrawElement(canvas, element, scaleX, scaleY, borderPaint, labelPaint, textPaint, labelFont);
            }

            using var image = surface.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var annotatedBytes = encoded.ToArray();

            return await CreateObservationAsync(request, annotatedBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "SoM annotation failed, returning original screenshot");
            return await CreateObservationAsync(request, request.ScreenshotPng, cancellationToken);
        }
    }

    private static void DrawElement(SKCanvas canvas, SomElement element, float scaleX, float scaleY, SKPaint borderPaint, SKPaint labelPaint, SKPaint textPaint, SKFont labelFont)
    {
        var box = element.BoundingBox;
        if (box.Width <= 0 || box.Height <= 0) return;

        var rect = SKRect.Create(
            (float)box.X * scaleX,
            (float)box.Y * scaleY,
            (float)box.Width * scaleX,
            (float)box.Height * scaleY);

        canvas.DrawRoundRect(rect, 4, 4, borderPaint);

        var label = element.Index.ToString();
        var textWidth = labelFont.MeasureText(label);
        var metrics = labelFont.Metrics;
        var labelWidth = textWidth + 10;
        var labelHeight = metrics.Descent - metrics.Ascent + 6;
        var labelX = Math.Max(0, rect.Left);
        var labelY = Math.Max(0, rect.Top - labelHeight);
        if (labelY <= 0)
        {
            labelY = rect.Top;
        }

        var labelRect = SKRect.Create(labelX, labelY, labelWidth, labelHeight);
        canvas.DrawRoundRect(labelRect, 4, 4, labelPaint);
        canvas.DrawText(label, labelX + 5, labelY + 3 - metrics.Ascent, SKTextAlign.Left, labelFont, textPaint);
    }

    private async Task<SomObservation> CreateObservationAsync(SomAnnotationRequest request, byte[] screenshotBytes, CancellationToken cancellationToken)
    {
        var localPath = await SaveAnnotatedScreenshotAsync(request, screenshotBytes, cancellationToken);

        return new SomObservation
        {
            SessionId = request.SessionId,
            Url = request.Url,
            Title = request.Title,
            ScreenshotBase64 = Convert.ToBase64String(screenshotBytes),
            ScreenshotMimeType = "image/png",
            AnnotatedScreenshotPath = localPath,
            ViewportWidth = request.ViewportWidth,
            ViewportHeight = request.ViewportHeight,
            ScrollX = request.ScrollX,
            ScrollY = request.ScrollY,
            CapturedAt = DateTime.Now,
            Elements = request.Elements.Take(Math.Max(0, request.MaxElements)).ToList()
        };
    }

    private async Task<string?> SaveAnnotatedScreenshotAsync(SomAnnotationRequest request, byte[] screenshotBytes, CancellationToken cancellationToken)
    {
        if (screenshotBytes.Length == 0)
        {
            return null;
        }

        try
        {
            var safeSessionId = SanitizePathSegment(request.SessionId);
            var contentHash = Convert.ToHexString(SHA256.HashData(screenshotBytes));
            if (_lastSavedScreenshots.TryGetValue(safeSessionId, out var lastSaved)
                && string.Equals(lastSaved.ContentHash, contentHash, StringComparison.Ordinal)
                && File.Exists(lastSaved.FilePath))
            {
                return lastSaved.FilePath;
            }

            var directory = Path.Combine(
                _pathService.GetAppDataDirectory(),
                BrowserScreenshotDirectoryName,
                ScreenshotDirectoryName,
                safeSessionId);
            Directory.CreateDirectory(directory);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var fileName = $"{timestamp}_{contentHash[..12]}{ScreenshotExtension}";
            var filePath = Path.Combine(directory, fileName);

            await File.WriteAllBytesAsync(filePath, screenshotBytes, cancellationToken);
            _lastSavedScreenshots[safeSessionId] = new ScreenshotSaveState(contentHash, filePath);
            return filePath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Failed to save annotated browser screenshot for session {SessionId}", request.SessionId);
            return null;
        }
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown-session";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown-session" : sanitized;
    }

    private sealed record ScreenshotSaveState(string ContentHash, string FilePath);
}
