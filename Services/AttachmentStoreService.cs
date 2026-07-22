using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Serilog;

namespace Athena.UI.Services;

public class AttachmentStoreService : IAttachmentStoreService
{
    private readonly IPlatformPathService _pathService;
    private readonly ILogger _logger;

    public AttachmentStoreService(IPlatformPathService pathService, ILogger logger)
    {
        _pathService = pathService;
        _logger = logger.ForContext<AttachmentStoreService>();
    }

    public async Task<IReadOnlyList<ChatAttachment>> ImportFilesAsync(
        IEnumerable<IStorageFile> files,
        CancellationToken cancellationToken = default)
    {
        var imported = new List<ChatAttachment>();
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(file.Name);
                var isImage = TryGetImageMimeType(extension, out var mimeType);
                var kind = isImage ? AttachmentKind.Image : AttachmentKind.Unknown;
                imported.Add(await ImportSingleAsync(file, extension, mimeType, kind, cancellationToken));
            }

            return imported;
        }
        catch
        {
            foreach (var attachment in imported)
            {
                DeleteStoredAttachment(attachment);
            }
            throw;
        }
    }

    private async Task<ChatAttachment> ImportSingleAsync(
        IStorageFile file,
        string extension,
        string mimeType,
        AttachmentKind kind,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? fileCreatedAt = null;
        DateTimeOffset? fileModifiedAt = null;
        try
        {
            var properties = await file.GetBasicPropertiesAsync();
            fileCreatedAt = properties.DateCreated;
            fileModifiedAt = properties.DateModified;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Basic file metadata is unavailable for {FileName}", file.Name);
        }

        var attachment = CreateAttachment(file.Name, extension, mimeType, kind);
        try
        {
            await using var input = await file.OpenReadAsync();
            await using (var output = File.Create(attachment.StoredPath))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            var info = new FileInfo(attachment.StoredPath);
            attachment.SizeBytes = info.Length;
            attachment.FileCreatedAt = fileCreatedAt;
            attachment.FileModifiedAt = fileModifiedAt;
            if (kind == AttachmentKind.Image)
            {
                await LoadPreviewAsync(attachment, cancellationToken);
            }

            return attachment;
        }
        catch
        {
            DeleteStoredAttachment(attachment);
            throw;
        }
    }

    private static bool TryGetImageMimeType(string extension, out string mimeType)
    {
        mimeType = extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };

        return mimeType != "application/octet-stream";
    }

    public async Task<ChatAttachment> ImportBitmapAsync(
        Bitmap bitmap,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attachment = CreateAttachment(fileName, ".png", "image/png", AttachmentKind.Image);
        bitmap.Save(attachment.StoredPath, PngBitmapEncoderOptions.Default);
        attachment.SizeBytes = new FileInfo(attachment.StoredPath).Length;
        attachment.PreviewImage = bitmap;
        attachment.Width = (int)Math.Round(bitmap.Size.Width);
        attachment.Height = (int)Math.Round(bitmap.Size.Height);
        await Task.CompletedTask;
        return attachment;
    }

    public async Task<ChatAttachment> CreateGeneratedImageAsync(
        byte[] bytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = GuessExtension(fileName, mimeType, ".png");
        var attachment = CreateAttachment(fileName, extension, mimeType, AttachmentKind.Image);
        await File.WriteAllBytesAsync(attachment.StoredPath, bytes, cancellationToken);
        attachment.SizeBytes = new FileInfo(attachment.StoredPath).Length;
        await LoadPreviewAsync(attachment, cancellationToken);
        return attachment;
    }

    public async Task<ChatAttachment> CreateGeneratedAudioAsync(
        byte[] bytes,
        string fileName,
        string mimeType,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = GuessExtension(fileName, mimeType, ".mp3");
        var attachment = CreateAttachment(fileName, extension, mimeType, AttachmentKind.Audio);
        await File.WriteAllBytesAsync(attachment.StoredPath, bytes, cancellationToken);
        attachment.SizeBytes = new FileInfo(attachment.StoredPath).Length;
        attachment.Duration = duration ?? TimeSpan.Zero;
        return attachment;
    }

    // 聊天气泡内嵌图最大逻辑宽度 560；2x 缩放下 1024 物理像素足够清晰
    private const int PreviewDecodeWidth = 1024;

    public async Task LoadPreviewAsync(ChatAttachment attachment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!attachment.IsImage || string.IsNullOrWhiteSpace(attachment.StoredPath) || !File.Exists(attachment.StoredPath))
        {
            attachment.PreviewImage = null;
            return;
        }

        Bitmap? decodedBitmap = null;
        try
        {
            var storedPath = attachment.StoredPath;

            // 解码在线程池；顺带用 SKCodec 只读元数据拿真实尺寸（避免因预览降分辨率而误报图像 token）
            var (bitmap, realWidth, realHeight) = await Task.Run(() =>
            {
                int rw = 0, rh = 0;
                using (var codec = SkiaSharp.SKCodec.Create(storedPath))
                {
                    if (codec != null)
                    {
                        rw = codec.Info.Width;
                        rh = codec.Info.Height;
                    }
                }

                using var stream = File.OpenRead(storedPath);
                Bitmap bmp;
                if (rw > PreviewDecodeWidth)
                {
                    bmp = Bitmap.DecodeToWidth(stream, PreviewDecodeWidth);
                }
                else
                {
                    bmp = new Bitmap(stream);
                    if (rw == 0)
                    {
                        rw = (int)Math.Round(bmp.Size.Width);
                        rh = (int)Math.Round(bmp.Size.Height);
                    }
                }

                return (bmp, rw, rh);
            }, cancellationToken);
            decodedBitmap = bitmap;
            cancellationToken.ThrowIfCancellationRequested();

            void Apply()
            {
                cancellationToken.ThrowIfCancellationRequested();
                attachment.PreviewImage = decodedBitmap;
                attachment.Width = attachment.Width == 0 ? realWidth : attachment.Width;
                attachment.Height = attachment.Height == 0 ? realHeight : attachment.Height;
                decodedBitmap = null; // ownership transferred to the attachment
            }

            // PreviewImage 触发 UI 绑定刷新，属性写入必须回到 UI 线程
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Apply();
            }
            else
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(Apply);
            }
        }
        catch (OperationCanceledException)
        {
            decodedBitmap?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            decodedBitmap?.Dispose();
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                attachment.PreviewImage = null;
            }
            else
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => attachment.PreviewImage = null);
            }
            _logger.Warning(ex, "Failed to load attachment preview: {Path}", attachment.StoredPath);
        }
    }

    public async Task LoadPreviewsAsync(IEnumerable<ChatAttachment> attachments, CancellationToken cancellationToken = default)
    {
        foreach (var attachment in attachments)
        {
            await LoadPreviewAsync(attachment, cancellationToken);
        }
    }

    public async Task<ChatAttachment> CloneStoredAttachmentAsync(ChatAttachment source, CancellationToken cancellationToken = default)
    {
        // 保留原 Id：消息 Segment 与图像会话按 AttachmentId 关联；仅物理文件换新路径。
        var clone = ConversationPersistenceHelper.CloneAttachment(source);

        if (!string.IsNullOrWhiteSpace(source.StoredPath) && File.Exists(source.StoredPath))
        {
            var extension = Path.GetExtension(source.StoredPath);
            var dayDirectory = Path.Combine(_pathService.GetAttachmentDirectory(), DateTime.Now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dayDirectory);
            var newStoredPath = Path.Combine(dayDirectory, $"{Guid.NewGuid():N}{extension}");

            await CopyFileAsync(source.StoredPath, newStoredPath, cancellationToken);
            clone.StoredPath = newStoredPath;
        }

        return clone;
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var sourceStream = File.OpenRead(sourcePath);
        await using var destinationStream = File.Create(destinationPath);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
    }

    public void DeleteStoredAttachment(ChatAttachment attachment)
    {
        DeleteFileQuietly(attachment.StoredPath);
    }

    private void DeleteFileQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete attachment file: {Path}", path);
        }
    }

    private ChatAttachment CreateAttachment(string fileName, string extension, string mimeType, AttachmentKind kind)
    {
        var id = Guid.NewGuid().ToString("N");
        var safeExtension = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension.ToLowerInvariant();
        var dayDirectory = Path.Combine(_pathService.GetAttachmentDirectory(), DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dayDirectory);

        return new ChatAttachment
        {
            Id = id,
            Kind = kind,
            FileName = string.IsNullOrWhiteSpace(fileName) ? $"{id}{safeExtension}" : fileName,
            StoredPath = Path.Combine(dayDirectory, $"{id}{safeExtension}"),
            MimeType = mimeType,
            CreatedAt = DateTime.Now
        };
    }

    private static string GuessExtension(string fileName, string mimeType, string fallback)
    {
        var currentExtension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(currentExtension))
        {
            return currentExtension;
        }

        return mimeType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "audio/mpeg" => ".mp3",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/aac" => ".aac",
            "audio/aiff" or "audio/x-aiff" => ".aiff",
            "audio/flac" => ".flac",
            "audio/opus" => ".opus",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            _ => fallback
        };
    }
}
