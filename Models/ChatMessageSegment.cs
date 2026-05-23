using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athena.UI.Models;

public enum ChatMessageSegmentKind
{
    Markdown = 0,
    GeneratedImage = 1
}

public partial class ChatMessageSegment : ObservableObject
{
    [ObservableProperty]
    private ChatMessageSegmentKind _kind;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string? _attachmentId;

    [ObservableProperty]
    [property: JsonIgnore]
    private ChatAttachment? _attachment;

    [JsonIgnore]
    public bool IsMarkdown => Kind == ChatMessageSegmentKind.Markdown;

    [JsonIgnore]
    public bool IsGeneratedImage => Kind == ChatMessageSegmentKind.GeneratedImage;
}
