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
            using (var baseImage = SKImage.FromBitmap(bitmap))
            {
                canvas.DrawImage(baseImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
            }

            var scaleX = request.ViewportWidth > 0 ? bitmap.Width / (float)request.ViewportWidth : 1f;
            var scaleY = request.ViewportHeight > 0 ? bitmap.Height / (float)request.ViewportHeight : 1f;

            using var borderPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(255, 71, 87),
                StrokeWidth = Math.Max(1.5f, bitmap.Width / 900f),
                IsAntialias = true
            };
            using var labelPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                // 近乎不透明：半透明底色叠在页面内容上时白色数字会糊掉，而这个数字是标注
                // 存在的唯一理由。
                Color = new SKColor(255, 71, 87, 230),
                IsAntialias = true
            };
            using var textPaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true
            };
            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
            // 序号字号按图宽缩放，1280 宽约 12px。两头都是坑：10px 以下的数字经模型侧缩放
            // 就剩几个像素，等于没标；而 18px 的号码牌在密集页面上会盖住相邻控件本身，模型
            // 看得见号却看不清被标的是什么。截图按 High 细节度上传（1280×900 → 约 0.85 倍），
            // 12px 到模型那头仍有 10px 上下，三位数也读得出来。
            using var labelFont = new SKFont(typeface, Math.Clamp(bitmap.Width / 110f, 10f, 15f));

            if (request.DrawAnnotations)
            {
                foreach (var element in request.Elements.Take(Math.Max(0, request.MaxElements)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DrawElement(canvas, element, scaleX, scaleY, borderPaint, labelPaint, textPaint, labelFont);
                }
            }

            using var image = surface.Snapshot();
            using var outputImage = ResizeImageIfNeeded(image, request.ScreenshotScale);
            // PNG 是无损格式，Skia 的 PNG 编码器忽略 quality 参数——这里传 100 只是把
            // "没有质量档可调"写明白，别再挂一个不起作用的设置项出来。
            using var encoded = outputImage.Encode(SKEncodedImageFormat.Png, 100);
            var annotatedBytes = encoded.ToArray();

            return await CreateObservationAsync(request, annotatedBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "SoM annotation failed, returning original screenshot");
            return await CreateObservationAsync(request, request.ScreenshotPng, cancellationToken);
        }
    }

    private static SKImage ResizeImageIfNeeded(SKImage image, double scale)
    {
        var normalizedScale = Math.Clamp(scale, 0.25, 2.0);
        if (Math.Abs(normalizedScale - 1.0) < 0.001)
        {
            return image;
        }

        var width = Math.Max(1, (int)Math.Round(image.Width * normalizedScale));
        var height = Math.Max(1, (int)Math.Round(image.Height * normalizedScale));
        var info = new SKImageInfo(width, height, image.ColorType, image.AlphaType);
        using var surface = SKSurface.Create(info);
        var dest = SKRect.Create(0, 0, width, height);
        surface.Canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return surface.Snapshot();
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
        // 内边距跟着字号一起收：号码牌是纯粹的遮挡面积，留白越多盖掉的页面内容越多。
        var labelWidth = textWidth + 5;
        var labelHeight = metrics.Descent - metrics.Ascent + 2;
        var labelX = Math.Max(0, rect.Left);
        var labelY = Math.Max(0, rect.Top - labelHeight);
        if (labelY <= 0)
        {
            labelY = rect.Top;
        }

        var labelRect = SKRect.Create(labelX, labelY, labelWidth, labelHeight);
        canvas.DrawRoundRect(labelRect, 2, 2, labelPaint);
        canvas.DrawText(label, labelX + 2.5f, labelY + 1 - metrics.Ascent, SKTextAlign.Left, labelFont, textPaint);
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
