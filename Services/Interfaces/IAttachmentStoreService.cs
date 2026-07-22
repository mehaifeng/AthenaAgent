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
    /// <summary>
    /// 将用户选择的任意文件复制到应用的受信附件区，只采集文件系统元数据；
    /// 除图片预览外，不读取、解析或索引文件内容。
    /// </summary>
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

    /// <summary>
    /// 物理克隆附件：复制存储文件并返回使用独立路径的附件。
    /// 用于会话 fork，避免两个会话共享同一附件文件后互相误删。
    /// </summary>
    Task<ChatAttachment> CloneStoredAttachmentAsync(ChatAttachment source, CancellationToken cancellationToken = default);
}
