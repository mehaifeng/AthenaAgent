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
    private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif"
    };

    private static readonly Dictionary<string, string> AudioMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".aac"] = "audio/aac",
        [".flac"] = "audio/flac",
        [".opus"] = "audio/opus",
        [".m4a"] = "audio/mp4"
    };

    private readonly IPlatformPathService _pathService;
    private readonly ILogger _logger;

    public AttachmentStoreService(IPlatformPathService pathService, ILogger logger)
    {
        _pathService = pathService;
        _logger = logger.ForContext<AttachmentStoreService>();
    }

    public int MaxPendingAttachments => 10;

    public long MaxImageBytes => 20 * 1024 * 1024;

    public async Task<IReadOnlyList<ChatAttachment>> ImportFilesAsync(
        IEnumerable<IStorageFile> files,
        CancellationToken cancellationToken = default)
    {
        var imported = new List<ChatAttachment>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file.Name);
            if (!ImageMimeTypes.TryGetValue(extension, out var mimeType))
            {
                throw new InvalidOperationException($"Unsupported image file type: {file.Name}");
            }

            await using var input = await file.OpenReadAsync();
            if (input.CanSeek && input.Length > MaxImageBytes)
            {
                throw new InvalidOperationException($"Image is too large: {file.Name}");
            }

            var attachment = CreateAttachment(file.Name, extension, mimeType, AttachmentKind.Image);
            await using (var output = File.Create(attachment.StoredPath))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            var info = new FileInfo(attachment.StoredPath);
            if (info.Length > MaxImageBytes)
            {
                DeleteStoredAttachment(attachment);
                throw new InvalidOperationException($"Image is too large: {file.Name}");
            }

            attachment.SizeBytes = info.Length;
            await LoadPreviewAsync(attachment, cancellationToken);
            imported.Add(attachment);
        }

        return imported;
    }

    public async Task<ChatAttachment> ImportBitmapAsync(
        Bitmap bitmap,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attachment = CreateAttachment(fileName, ".png", "image/png", AttachmentKind.Image);
        bitmap.Save(attachment.StoredPath);
        attachment.SizeBytes = new FileInfo(attachment.StoredPath).Length;
        if (attachment.SizeBytes > MaxImageBytes)
        {
            DeleteStoredAttachment(attachment);
            throw new InvalidOperationException($"Image is too large: {fileName}");
        }

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

    public Task LoadPreviewAsync(ChatAttachment attachment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!attachment.IsImage || string.IsNullOrWhiteSpace(attachment.StoredPath) || !File.Exists(attachment.StoredPath))
        {
            attachment.PreviewImage = null;
            return Task.CompletedTask;
        }

        try
        {
            using var stream = File.OpenRead(attachment.StoredPath);
            var bitmap = new Bitmap(stream);
            attachment.PreviewImage = bitmap;
            attachment.Width = attachment.Width == 0 ? (int)Math.Round(bitmap.Size.Width) : attachment.Width;
            attachment.Height = attachment.Height == 0 ? (int)Math.Round(bitmap.Size.Height) : attachment.Height;
        }
        catch (Exception ex)
        {
            attachment.PreviewImage = null;
            _logger.Warning(ex, "Failed to load attachment preview: {Path}", attachment.StoredPath);
        }

        return Task.CompletedTask;
    }

    public async Task LoadPreviewsAsync(IEnumerable<ChatAttachment> attachments, CancellationToken cancellationToken = default)
    {
        foreach (var attachment in attachments)
        {
            await LoadPreviewAsync(attachment, cancellationToken);
        }
    }

    public void DeleteStoredAttachment(ChatAttachment attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment.StoredPath))
        {
            return;
        }

        try
        {
            if (File.Exists(attachment.StoredPath))
            {
                File.Delete(attachment.StoredPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete attachment: {Path}", attachment.StoredPath);
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

        foreach (var pair in ImageMimeTypes)
        {
            if (string.Equals(pair.Value, mimeType, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        foreach (var pair in AudioMimeTypes)
        {
            if (string.Equals(pair.Value, mimeType, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return fallback;
    }
}
