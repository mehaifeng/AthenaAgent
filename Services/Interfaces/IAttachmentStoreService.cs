using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Athena.UI.Services.Interfaces;

public interface IAttachmentStoreService
{
    int MaxPendingAttachments { get; }

    long MaxImageBytes { get; }

    Task<IReadOnlyList<ChatAttachment>> ImportFilesAsync(
        IEnumerable<IStorageFile> files,
        CancellationToken cancellationToken = default);

    Task<ChatAttachment> ImportBitmapAsync(
        Bitmap bitmap,
        string fileName,
        CancellationToken cancellationToken = default);

    Task LoadPreviewAsync(ChatAttachment attachment, CancellationToken cancellationToken = default);

    Task LoadPreviewsAsync(IEnumerable<ChatAttachment> attachments, CancellationToken cancellationToken = default);

    void DeleteStoredAttachment(ChatAttachment attachment);
}
