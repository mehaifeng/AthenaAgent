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

    // MinerU 支持的文档格式：PDF / Word / PowerPoint / Excel。
    private static readonly Dictionary<string, string> DocumentMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    };

    // 纯文本 / 代码文件：无需远端解析，直接读取内容作为上下文交给 AI。
    private static readonly Dictionary<string, string> TextMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".text"] = "text/plain",
        [".log"] = "text/plain",
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".rst"] = "text/x-rst",
        [".json"] = "application/json",
        [".jsonc"] = "application/json",
        [".json5"] = "application/json",
        [".xml"] = "application/xml",
        [".xaml"] = "application/xml",
        [".axaml"] = "application/xml",
        [".svg"] = "image/svg+xml",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".scss"] = "text/x-scss",
        [".less"] = "text/x-less",
        [".yaml"] = "application/x-yaml",
        [".yml"] = "application/x-yaml",
        [".toml"] = "application/toml",
        [".ini"] = "text/plain",
        [".config"] = "application/xml",
        [".conf"] = "text/plain",
        // 注意：.env 装的是密钥，且在 FileSystemPolicy.BlockedExtensions 中被读工具拒绝，
        // 因此不纳入文本附件白名单（既避免密钥泄露，也避免“能传不能读”的割裂）。
        [".properties"] = "text/plain",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",
        // 代码
        [".cs"] = "text/x-csharp",
        [".csx"] = "text/x-csharp",
        [".fs"] = "text/x-fsharp",
        [".vb"] = "text/x-vb",
        [".py"] = "text/x-python",
        [".pyw"] = "text/x-python",
        [".java"] = "text/x-java",
        [".kt"] = "text/x-kotlin",
        [".kts"] = "text/x-kotlin",
        [".go"] = "text/x-go",
        [".rs"] = "text/x-rust",
        [".rb"] = "text/x-ruby",
        [".php"] = "text/x-php",
        [".swift"] = "text/x-swift",
        [".c"] = "text/x-c",
        [".h"] = "text/x-c",
        [".cpp"] = "text/x-c++",
        [".cc"] = "text/x-c++",
        [".cxx"] = "text/x-c++",
        [".hpp"] = "text/x-c++",
        [".m"] = "text/x-objectivec",
        [".mm"] = "text/x-objectivec",
        [".js"] = "text/javascript",
        [".jsx"] = "text/javascript",
        [".mjs"] = "text/javascript",
        [".cjs"] = "text/javascript",
        [".ts"] = "text/x-typescript",
        [".tsx"] = "text/x-typescript",
        [".vue"] = "text/x-vue",
        [".sql"] = "text/x-sql",
        [".sh"] = "text/x-sh",
        [".bash"] = "text/x-sh",
        [".zsh"] = "text/x-sh",
        [".ps1"] = "text/x-powershell",
        // .bat / .cmd 在 BlockedExtensions 中，省略以保持导入白名单与读工具策略一致。
        [".r"] = "text/x-r",
        [".lua"] = "text/x-lua",
        [".pl"] = "text/x-perl",
        [".dart"] = "text/x-dart",
        [".scala"] = "text/x-scala",
        [".groovy"] = "text/x-groovy",
        [".gradle"] = "text/x-groovy",
        [".dockerfile"] = "text/plain",
        [".makefile"] = "text/plain",
        [".gitignore"] = "text/plain",
        [".editorconfig"] = "text/plain"
    };

    private static readonly Dictionary<string, string> AudioMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".aac"] = "audio/aac",
        [".aiff"] = "audio/aiff",
        [".aif"] = "audio/aiff",
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

    // MinerU 精度解析单文件上限 200MB；此处按上限放行，超限交由远端报错。
    public long MaxDocumentBytes => 200L * 1024 * 1024;

    // 纯文本 / 代码文件直接读入上下文，限制 5MB 以避免一次性塞爆上下文窗口。
    public long MaxTextBytes => 5L * 1024 * 1024;

    public IReadOnlyCollection<string> SupportedDocumentExtensions => DocumentMimeTypes.Keys;

    public IReadOnlyCollection<string> SupportedTextExtensions => TextMimeTypes.Keys;

    public async Task<IReadOnlyList<ChatAttachment>> ImportFilesAsync(
        IEnumerable<IStorageFile> files,
        CancellationToken cancellationToken = default)
    {
        var imported = new List<ChatAttachment>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file.Name);

            if (ImageMimeTypes.TryGetValue(extension, out var imageMime))
            {
                imported.Add(await ImportSingleAsync(file, extension, imageMime, AttachmentKind.Image, MaxImageBytes, "Image", cancellationToken));
            }
            else if (DocumentMimeTypes.TryGetValue(extension, out var docMime))
            {
                imported.Add(await ImportSingleAsync(file, extension, docMime, AttachmentKind.Document, MaxDocumentBytes, "Document", cancellationToken));
            }
            else if (TextMimeTypes.TryGetValue(extension, out var textMime))
            {
                imported.Add(await ImportSingleAsync(file, extension, textMime, AttachmentKind.Code, MaxTextBytes, "Text", cancellationToken));
            }
            else
            {
                throw new InvalidOperationException($"Unsupported file type: {file.Name}");
            }
        }

        return imported;
    }

    private async Task<ChatAttachment> ImportSingleAsync(
        IStorageFile file,
        string extension,
        string mimeType,
        AttachmentKind kind,
        long maxBytes,
        string label,
        CancellationToken cancellationToken)
    {
        await using var input = await file.OpenReadAsync();
        if (input.CanSeek && input.Length > maxBytes)
        {
            throw new InvalidOperationException($"{label} is too large: {file.Name}");
        }

        var attachment = CreateAttachment(file.Name, extension, mimeType, kind);
        await using (var output = File.Create(attachment.StoredPath))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        var info = new FileInfo(attachment.StoredPath);
        if (info.Length > maxBytes)
        {
            DeleteStoredAttachment(attachment);
            throw new InvalidOperationException($"{label} is too large: {file.Name}");
        }

        attachment.SizeBytes = info.Length;
        if (kind == AttachmentKind.Image)
        {
            await LoadPreviewAsync(attachment, cancellationToken);
        }
        else if (kind == AttachmentKind.Code)
        {
            // 文本/代码文件无需远端解析，直接读入内容并标记为已解析。
            // 模型按需读取时直接复用原始文件，不再复制一份。
            attachment.ExtractedText = await File.ReadAllTextAsync(attachment.StoredPath, cancellationToken);
            attachment.ParseState = DocumentParseState.Parsed;
            attachment.RetrievalPath = attachment.StoredPath;
            attachment.EstimatedTokens = Models.ConversationContext.EstimateTokens(attachment.ExtractedText);
        }

        return attachment;
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

            void Apply()
            {
                attachment.PreviewImage = bitmap;
                attachment.Width = attachment.Width == 0 ? realWidth : attachment.Width;
                attachment.Height = attachment.Height == 0 ? realHeight : attachment.Height;
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
        catch (Exception ex)
        {
            attachment.PreviewImage = null;
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

    public async Task<string> WriteParsedSidecarAsync(
        ChatAttachment attachment,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        // 把解析出的 Markdown 落盘为与附件同生命周期的 sidecar，供模型按需读取。
        var directory = Path.GetDirectoryName(attachment.StoredPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(_pathService.GetAttachmentDirectory(), DateTime.Now.ToString("yyyyMMdd"));
        }
        Directory.CreateDirectory(directory);

        var sidecarPath = Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(attachment.StoredPath)}.parsed.md");
        await File.WriteAllTextAsync(sidecarPath, markdown ?? string.Empty, cancellationToken);
        return sidecarPath;
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

            // 解析 sidecar（RetrievalPath 指向独立文件时）随附件一并复制
            if (!string.IsNullOrWhiteSpace(source.RetrievalPath)
                && !string.Equals(source.RetrievalPath, source.StoredPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(source.RetrievalPath))
            {
                var sidecarPath = Path.Combine(
                    dayDirectory,
                    $"{Path.GetFileNameWithoutExtension(newStoredPath)}.parsed.md");
                await CopyFileAsync(source.RetrievalPath, sidecarPath, cancellationToken);
                clone.RetrievalPath = sidecarPath;
            }
            else if (!string.IsNullOrWhiteSpace(source.RetrievalPath)
                && string.Equals(source.RetrievalPath, source.StoredPath, StringComparison.OrdinalIgnoreCase))
            {
                clone.RetrievalPath = newStoredPath;
            }
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

        // 同时清理解析 sidecar（若 RetrievalPath 指向独立文件而非原始附件）。
        if (!string.IsNullOrWhiteSpace(attachment.RetrievalPath)
            && !string.Equals(attachment.RetrievalPath, attachment.StoredPath, StringComparison.OrdinalIgnoreCase))
        {
            DeleteFileQuietly(attachment.RetrievalPath);
        }
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
