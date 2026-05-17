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
    [NotifyPropertyChangedFor(nameof(DisplayKind))]
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
    [property: JsonIgnore]
    private IImage? _previewImage;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isPlaying;

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private TimeSpan _duration;

    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyPropertyChangedFor(nameof(PlaybackProgress))]
    private TimeSpan _position;

    [JsonIgnore]
    public bool IsImage => Kind == AttachmentKind.Image;

    [JsonIgnore]
    public bool IsAudio => Kind == AttachmentKind.Audio;

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

    [JsonIgnore]
    public string DurationText => Duration > TimeSpan.Zero ? FormatTime(Duration) : "--:--";

    [JsonIgnore]
    public string PositionText => FormatTime(Position);

    [JsonIgnore]
    public double PlaybackProgress =>
        Duration > TimeSpan.Zero
            ? Math.Clamp(Position.TotalSeconds / Duration.TotalSeconds, 0d, 1d)
            : 0d;

    private static string FormatTime(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return value.ToString(@"h\:mm\:ss");
        }

        return value.ToString(@"m\:ss");
    }
}
