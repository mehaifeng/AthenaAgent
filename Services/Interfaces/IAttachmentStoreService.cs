using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using System;

namespace Athena.UI.Services.Interfaces;

public interface IAttachmentStoreService
{
    int MaxPendingAttachments { get; }

    long MaxImageBytes { get; }

    long MaxDocumentBytes { get; }

    long MaxTextBytes { get; }

    /// <summary>支持作为文档解析的扩展名（含点，小写），如 ".pdf"。</summary>
    IReadOnlyCollection<string> SupportedDocumentExtensions { get; }

    /// <summary>支持直接读入内容的纯文本/代码扩展名（含点，小写），如 ".cs"。</summary>
    IReadOnlyCollection<string> SupportedTextExtensions { get; }

    Task<IReadOnlyList<ChatAttachment>> ImportFilesAsync(
        IEnumerable<IStorageFile> files,
        CancellationToken cancellationToken = default);

    Task<ChatAttachment> ImportBitmapAsync(
        Bitmap bitmap,
        string fileName,
        CancellationToken cancellationToken = default);

    Task<ChatAttachment> CreateGeneratedImageAsync(
        byte[] bytes,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken = default);

    Task<ChatAttachment> CreateGeneratedAudioAsync(
        byte[] bytes,
        string fileName,
        string mimeType,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default);

    /// <summary>将解析出的 Markdown 写为附件 sidecar 文件，返回其完整路径。</summary>
    Task<string> WriteParsedSidecarAsync(
        ChatAttachment attachment,
        string markdown,
        CancellationToken cancellationToken = default);

    Task LoadPreviewAsync(ChatAttachment attachment, CancellationToken cancellationToken = default);

    Task LoadPreviewsAsync(IEnumerable<ChatAttachment> attachments, CancellationToken cancellationToken = default);

    void DeleteStoredAttachment(ChatAttachment attachment);
}
