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

    /// <summary>支持作为文档解析的扩展名（含点，小写），如 ".pdf"。</summary>
    IReadOnlyCollection<string> SupportedDocumentExtensions { get; }

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

    Task LoadPreviewAsync(ChatAttachment attachment, CancellationToken cancellationToken = default);

    Task LoadPreviewsAsync(IEnumerable<ChatAttachment> attachments, CancellationToken cancellationToken = default);

    void DeleteStoredAttachment(ChatAttachment attachment);
}
