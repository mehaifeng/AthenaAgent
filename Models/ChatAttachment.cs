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

    [ObservableProperty]
    private string _audioProvider = string.Empty;

    // 文档解析（MinerU）：解析状态、抽取出的 Markdown 文本与错误信息。
    // ExtractedText 需要持久化，以便从历史恢复时仍保留可供 AI 阅读的内容。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParsing))]
    [NotifyPropertyChangedFor(nameof(IsParsed))]
    [NotifyPropertyChangedFor(nameof(IsParseFailed))]
    private DocumentParseState _parseState = DocumentParseState.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExtractedText))]
    private string _extractedText = string.Empty;

    [ObservableProperty]
    private string _parseError = string.Empty;

    // 大附件取用：决定该附件的文本内容是内联进上下文，还是仅注入指针由模型按需读取。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeferred))]
    private AttachmentRetrievalMode _retrievalMode = AttachmentRetrievalMode.Inline;

    // 模型按需读取时使用的本地文件路径：文本/代码为原始文件，文档为解析后的 .parsed.md sidecar。
    [ObservableProperty]
    private string _retrievalPath = string.Empty;

    // 估算的 token 开销，用于内联/延迟门控与上下文统计。
    [ObservableProperty]
    private int _estimatedTokens;

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
    public bool UsesSystemAudioPlayback => string.Equals(AudioProvider, "System", StringComparison.OrdinalIgnoreCase);

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
    public bool IsParsing => ParseState == DocumentParseState.Parsing;

    [JsonIgnore]
    public bool IsParsed => ParseState == DocumentParseState.Parsed;

    [JsonIgnore]
    public bool IsParseFailed => ParseState == DocumentParseState.Failed;

    [JsonIgnore]
    public bool HasExtractedText => !string.IsNullOrWhiteSpace(ExtractedText);

    [JsonIgnore]
    public bool IsDeferred => RetrievalMode == AttachmentRetrievalMode.Deferred;

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
