using System;
using System.IO;
using System.Text.Json.Serialization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athena.UI.Models;

public partial class ChatAttachment : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImage))]
    [NotifyPropertyChangedFor(nameof(IsAudio))]
    [NotifyPropertyChangedFor(nameof(IsDocument))]
    [NotifyPropertyChangedFor(nameof(IsGenericFile))]
    [NotifyPropertyChangedFor(nameof(DisplayKind))]
    [NotifyPropertyChangedFor(nameof(ImageContent))]
    [NotifyPropertyChangedFor(nameof(GenericFileContent))]
    [NotifyPropertyChangedFor(nameof(AudioContent))]
    private AttachmentKind _kind = AttachmentKind.Unknown;

    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(FileExtension))]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _storedPath = string.Empty;

    [ObservableProperty]
    private string _mimeType = "application/octet-stream";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySize))]
    private long _sizeBytes;

    [ObservableProperty]
    private int _width;

    [ObservableProperty]
    private int _height;

    [ObservableProperty]
    private DateTime _createdAt = DateTime.Now;

    /// <summary>源文件在被添加为附件时报告的文件系统创建时间。</summary>
    [ObservableProperty]
    private DateTimeOffset? _fileCreatedAt;

    /// <summary>源文件在被添加为附件时报告的文件系统最后修改时间。</summary>
    [ObservableProperty]
    private DateTimeOffset? _fileModifiedAt;

    [ObservableProperty]
    private string _audioProvider = string.Empty;

    [ObservableProperty]
    [property: JsonIgnore]
    private IImage? _previewImage;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isPlaying;

    [ObservableProperty]
    [property: JsonIgnore]
    private TimeSpan _duration;

    [ObservableProperty]
    [property: JsonIgnore]
    private TimeSpan _position;

    [JsonIgnore]
    public bool IsImage => Kind == AttachmentKind.Image;

    [JsonIgnore]
    public bool IsAudio => Kind == AttachmentKind.Audio;

    [JsonIgnore]
    public bool IsGenericFile => !IsImage && !IsAudio;

    [JsonIgnore]
    public bool IsDocument => Kind == AttachmentKind.Document;

    [JsonIgnore]
    public ChatAttachment? ImageContent => IsImage ? this : null;

    [JsonIgnore]
    public ChatAttachment? GenericFileContent => IsGenericFile ? this : null;

    [JsonIgnore]
    public ChatAttachment? AudioContent => IsAudio ? this : null;

    [JsonIgnore]
    public string DisplayKind => Kind switch
    {
        AttachmentKind.Image => "Image",
        AttachmentKind.Audio => "Audio",
        AttachmentKind.Document => "Document",
        AttachmentKind.Code => "Code",
        _ => "File"
    };

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(FileName) ? "Attachment" : FileName;

    [JsonIgnore]
    public string FileExtension
    {
        get
        {
            var extension = Path.GetExtension(FileName);
            return string.IsNullOrWhiteSpace(extension) ? "FILE" : extension.TrimStart('.').ToUpperInvariant();
        }
    }

    [JsonIgnore]
    public string DisplaySize
    {
        get
        {
            if (SizeBytes < 1024) return $"{SizeBytes} B";
            if (SizeBytes < 1024 * 1024) return $"{SizeBytes / 1024d:0.#} KB";
            return $"{SizeBytes / 1024d / 1024d:0.#} MB";
        }
    }
}
